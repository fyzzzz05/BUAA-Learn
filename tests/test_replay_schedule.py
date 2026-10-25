from datetime import datetime
from types import SimpleNamespace
from zoneinfo import ZoneInfo

import pytest

from buaa_agent import replay_schedule
from buaa_agent.portal import LoginRequired
from buaa_agent.service import Service
from buaa_agent.store import Store

TZ = ZoneInfo("Asia/Shanghai")


def at(day, hour=10):
    return datetime(2026, 10, day, hour, tzinfo=TZ)


def timetable(day=21, slot="08:00-09:35", course="demo"):
    return {"course_id": course, "date": f"2026-10-{day}", "time": slot}


def test_next_day_boundary_is_shanghai_midnight():
    result = replay_schedule.plan({"course_id": "demo", "enabled": True}, [timetable()], at(21))
    assert result["next_run"] == "2026-10-22T00:00:00+08:00"
    assert result["pending_count"] == 1
    # The same instant expressed in UTC must not move the boundary.
    assert replay_schedule.parse_date("2026-10-21T16:00:00Z") == at(22, 0)


def test_no_timetable_means_wait_no_guessed_weekly_polling():
    result = replay_schedule.plan({"course_id": "demo", "enabled": True}, [], at(22))
    assert result["next_run"] is None
    assert result["pending_count"] == 0


def test_older_and_other_courses_are_excluded_and_future_dates_preserved():
    rows = [timetable(19), timetable(21, course="other"), timetable(23)]
    result = replay_schedule.plan({"course_id": "demo", "enabled": True}, rows, at(22))
    assert result["pending_count"] == 1
    assert result["next_run"] == "2026-10-24T00:00:00+08:00"


def test_overdue_watch_survives_rolling_timetable_replacement():
    first = replay_schedule.plan({"course_id": "demo", "enabled": True}, [timetable()], at(22))
    second = replay_schedule.plan(first, [timetable(28)], at(25))
    assert second["pending_count"] == 2
    assert second["next_run"] == first["next_run"]


def test_separate_same_day_classes_do_not_match_each_other():
    result = replay_schedule.plan({"course_id": "demo", "enabled": True},
                                  [timetable(), timetable(slot="14:00-15:35")], at(22))
    watches = list(result["watches"].values())
    morning = {"date": "2026-10-21T08:00:00+08:00"}
    afternoon = {"date": "2026-10-21T14:00:00+08:00"}
    assert replay_schedule.matches(watches[0], morning)
    assert not replay_schedule.matches(watches[0], afternoon)
    assert replay_schedule.matches(watches[1], afternoon)


@pytest.fixture
def setup(tmp_path, monkeypatch):
    monkeypatch.setattr(replay_schedule, "local_now", lambda: at(22))
    store = Store(tmp_path)
    store.put("alice", "course", "demo", {"id": "demo"})
    store.put("alice", "meta", "schedule", {"rows": [timetable()]})
    auth = SimpleNamespace(owner="alice", session=None)
    service = Service(store, auth, SimpleNamespace(ready=True))
    service.configure_automation("alice", "demo", True, 120, False)
    return service


def test_not_ready_retries_in_two_hours_and_scheduler_deduplicates(setup, monkeypatch):
    service = setup
    service.sync_course = lambda *_: []
    service.scheduler_tick()
    service.scheduler_tick()
    assert service.queue.qsize() == 1
    service.run_automation("alice", "demo", lambda _: None)
    rule = service.store.get("alice", "automation", "demo")
    assert rule["next_run"] == "2026-10-22T12:00:00+08:00"
    # Completing the queued task is irrelevant to the planning decision.
    assert rule["pending_count"] == 1


def test_success_stops_polling_this_occurrence(setup):
    service = setup
    calls = []
    service.sync_course = lambda *_: [{"id": "1001", "title": "昨日课程", "has_recording": True,
                                      "date": "2026-10-21T08:00:00+08:00"}]
    service.fetch_materials = lambda *args: calls.append(args) or {"segments": ["fixture"]}
    service.run_automation("alice", "demo", lambda _: None)
    service.run_automation("alice", "demo", lambda _: None)
    rule = service.store.get("alice", "automation", "demo")
    assert len(calls) == 1
    assert rule["pending_count"] == 0 and rule["next_run"] is None
    assert rule["known"] == ["1001"]


def test_login_failure_keeps_watch_and_retry_time(setup):
    service = setup
    def fail(*_): raise LoginRequired("expired")
    service.sync_course = fail
    with pytest.raises(LoginRequired):
        service.run_automation("alice", "demo", lambda _: None)
    rule = service.store.get("alice", "automation", "demo")
    assert rule["pending_count"] == 1
    assert rule["known"] == []
    assert rule["next_run"] == "2026-10-22T12:00:00+08:00"


def test_daily_timetable_refresh_is_throttled_and_cross_week_is_real_dates(setup):
    service = setup
    service.auth.session = object()
    service.scheduler_tick()
    service.scheduler_tick()
    jobs = service.store.list("alice", "job")
    assert sum(job["kind"] == "schedule" for job in jobs) == 1
    calls = []
    session = SimpleNamespace(schedule=lambda start, end: calls.append((start, end)) or [timetable(28)])
    service._session = lambda _: session
    service.sync_timetable("alice")
    assert calls == [("2026-10-15", "2026-11-12")]
    assert service.store.get("alice", "meta", "schedule")["rows"] == []
    assert service.store.get("alice", "automation", "demo")["pending_count"] == 2


def test_empty_materials_remain_pending_even_when_video_is_ready(setup):
    service = setup
    service.sync_course = lambda *_: [{"id": "1001", "title": "昨日课程", "has_recording": True,
                                      "date": "2026-10-21T08:00:00+08:00"}]
    service.fetch_materials = lambda *_: {"segments": [], "slides": []}
    service.run_automation("alice", "demo", lambda _: None)
    rule = service.store.get("alice", "automation", "demo")
    assert rule["pending_count"] == 1 and rule["known"] == []
    assert rule["next_run"] == "2026-10-22T12:00:00+08:00"
