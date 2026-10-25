from datetime import datetime
from types import SimpleNamespace
from zoneinfo import ZoneInfo

import pytest

from buaa_agent.portal import LoginRequired
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
