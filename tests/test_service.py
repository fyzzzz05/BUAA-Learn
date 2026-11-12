from datetime import datetime
from types import SimpleNamespace
from zoneinfo import ZoneInfo

import pytest

from buaa_agent.portal import LoginRequired, PortalError
from buaa_agent.service import Service
from buaa_agent.store import Store


@pytest.fixture
def service(tmp_path, monkeypatch):
    monkeypatch.setattr("buaa_agent.replay_schedule.local_now", lambda: datetime(2026, 10, 22, 10, tzinfo=ZoneInfo("Asia/Shanghai")))
    store = Store(tmp_path)
    auth = SimpleNamespace(owner="alice", require=lambda: SimpleNamespace(owner="alice"))
    return Service(store, auth, SimpleNamespace(ready=True))


def test_accounts_have_separate_course_records(service):
    service.store.put("alice", "course", "demo", {"id": "demo", "name": "Alice's course"})
    assert service.store.list("bob", "course") == []


def test_duplicate_running_jobs_are_not_enqueued_twice(service):
    first = service.enqueue("course", "demo")
    second = service.enqueue("course", "demo")
    assert first["id"] == second["id"]
    assert service.queue.qsize() == 1


def test_account_switch_does_not_run_a_previous_accounts_job(service):
    service.auth.require = lambda: SimpleNamespace(owner="bob")
    with pytest.raises(LoginRequired):
        service._session("alice")


def prepare_recordings(service):
    service.store.put("alice", "meta", "semester", {"id": "current"})
    for identifier, semester in [("a", "current"), ("b", "current"), ("old", "previous")]:
        service.store.put("alice", "course", identifier, {
            "id": identifier, "name": identifier, "semester": semester})


def test_sync_recordings_checks_current_semester_and_counts_newly_available_replays(service):
    prepare_recordings(service)
    for identifier, available in [("1", True), ("2", False)]:
        service.store.put("alice", "lesson", identifier, {
            "id": identifier, "course_id": "a", "has_recording": available})
    calls = []

    def sync(owner, identifier):
        calls.append((owner, identifier))
        return [{"id": str(i), "has_recording": True} for i in (1, 2, 3)] if identifier == "a" else []

    service.sync_course = sync
    job = service.enqueue("recordings")
    assert service._execute("alice", job) == "已同步 2 门课程，新增 2 节回放。"
    assert sorted(calls) == [("alice", "a"), ("alice", "b")]
    assert service.store.get("alice", "job", job["id"])["progress"] == {
        "completed": 2, "total": 2, "unit": "门"}


def test_sync_recordings_continues_after_course_failure(service):
    prepare_recordings(service)
    calls = []

    def sync(owner, identifier):
        calls.append(identifier)
        if identifier == "a":
            raise PortalError("回放服务暂不可用")
        return [{"id": "new", "has_recording": True}]

    service.sync_course = sync
    with pytest.raises(PortalError, match="已同步 1 门课程，新增 1 节回放。以下课程同步失败，请重试：a"):
        service.sync_recordings("alice", lambda _: None)
    assert sorted(calls) == ["a", "b"]


def test_sync_recordings_stops_when_login_expires(service):
    prepare_recordings(service)
    calls = []

    def sync(owner, identifier):
        calls.append(identifier)
        raise LoginRequired("请重新登录")

    service.sync_course = sync
    with pytest.raises(LoginRequired):
        service.sync_recordings("alice", lambda _: None)
    assert len(calls) == 1


def prepare_schedule(service):
    service.store.put("alice", "course", "demo", {"id": "demo"})
    service.store.put("alice", "meta", "schedule", {"rows": [
        {"course_id": "demo", "date": "2026-10-21", "time": "08:00-09:35"}]})


def test_automation_does_not_analyze_older_historical_recordings(service):
    prepare_schedule(service)
    service.configure_automation("alice", "demo", True, 120, True)
    service.sync_course = lambda *_: [{"id": "1001", "has_recording": True,
                                     "date": "2026-10-19T08:00:00+08:00"}]
    service.fetch_materials = lambda *_: pytest.fail("must not fetch historical lessons")
    service.run_automation("alice", "demo", lambda _: None)
    config = service.store.get("alice", "automation", "demo")
    assert config["known"] == []
    assert config["pending_count"] == 1


def test_automation_retries_failed_new_recording_without_marking_it_seen(service):
    from buaa_agent.learning import LearningError
    prepare_schedule(service)
    service.configure_automation("alice", "demo", True, 120, True)
    config = service.store.get("alice", "automation", "demo")
    config.update(initialized=True, known=[])
    service.store.put("alice", "automation", "demo", config)
    service.sync_course = lambda *_: [{"id": "1001", "title": "新课次", "has_recording": True, "date": "2026-10-21T08:00:00+08:00"}]
    service.fetch_materials = lambda *_: {"segments": ["fixture"]}
    def fail(*_): raise LearningError("provider failed")
    service.analyze_lesson = fail
    with pytest.raises(LearningError):
        service.run_automation("alice", "demo", lambda _: None)
    assert service.store.get("alice", "automation", "demo")["known"] == []


def test_pausing_a_running_automation_is_not_overwritten_by_old_config(service):
    prepare_schedule(service)
    service.configure_automation("alice", "demo", True, 120, False)
    def sync_and_pause(*_):
        service.configure_automation("alice", "demo", False, 360, False)
        return [{"id": "1001", "has_recording": True, "date": "2026-10-21T08:00:00+08:00"}]
    service.sync_course = sync_and_pause
    service.run_automation("alice", "demo", lambda _: None)
    config = service.store.get("alice", "automation", "demo")
    assert config["enabled"] is False
    assert config["interval_minutes"] == 120
    assert config["next_run"] is None


def test_semester_timetable_sync_batches_and_preserves_cache_on_failure(service):
    service.store.put("alice", "meta", "semester", {"id": "2026-20271"})
    calls = []

    def schedule(first, last):
        calls.append((first, last))
        return [{"date": first, "name": "测试课"}, {"date": "2026-09-01"}]

    service.auth.require = lambda: SimpleNamespace(owner="alice", schedule=schedule)
    service.sync_semester_timetable("alice", lambda _: None)
    cached = service.timetable("alice", "2026-20271")
    assert calls[0] == ("2026-09-07", "2026-10-04")
    assert calls[-1] == ("2026-12-28", "2027-01-17")
    assert len(calls) == len(cached["rows"]) == 5
    assert cached["complete"] and cached["weeks"] == 19
    assert not service.timetable("bob", "2026-20271")["rows"]
    assert not service.timetable("alice", "2026-20272")["complete"]

    def fail_after_one_batch(first, last):
        if first != "2026-09-07":
            raise PortalError("同步失败")
        return [{"date": first, "name": "新课程"}]

    service.auth.require = lambda: SimpleNamespace(owner="alice", schedule=fail_after_one_batch)
    with pytest.raises(PortalError, match="同步失败"):
        service.sync_semester_timetable("alice", lambda _: None)
    assert service.timetable("alice", "2026-20271") == cached


def test_analysis_failure_log_reports_stage_and_reason(service, caplog):
    from buaa_agent.learning import LearningError

    job = service.enqueue("analyze", "1001")

    def analyze(_owner, _target, progress):
        progress({"message": "正在分析第 5 段", "progress": {
            "stage": "课堂文本分析", "completed": 4, "total": 16,
        }})
        service.stop.set()
        raise LearningError("第 5/16 段：等待模型响应超时")

    service.analyze_lesson = analyze
    service._worker()
    assert "job=analyze" in caplog.text
    assert f"id={job['id']}" in caplog.text
    assert "stage=课堂文本分析 completed=4/16" in caplog.text
    assert "reason=第 5/16 段：等待模型响应超时" in caplog.text
    assert "+08:00" in caplog.text
