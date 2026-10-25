from fastapi.testclient import TestClient

from buaa_agent.app import create_app


def test_cross_origin_mutation_and_dns_rebinding_are_blocked(tmp_path):
    app = create_app(tmp_path)
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        assert client.post("/api/auth/login").status_code == 403
        assert client.post("/api/auth/login", headers={
            "X-BUAA-Local": "1", "Origin": "https://unrelated.example"}).status_code == 403
        assert client.get("/api/state", headers={"Host": "unrelated.example"}).status_code == 400


def test_cached_account_cannot_read_another_accounts_lessons(tmp_path):
    app = create_app(tmp_path)
    app.state.auth.profile = {"owner": "alice", "name": "示例同学"}
    app.state.store.put("bob", "course", "demo", {"id": "demo"})
    app.state.store.put("bob", "lesson", "1001", {"id": "1001", "course_id": "demo"})
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        assert client.get("/api/lessons/1001").status_code == 502


def test_settings_do_not_return_or_persist_api_keys(tmp_path):
    app = create_app(tmp_path)
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        response = client.put("/api/model", headers={"X-BUAA-Local": "1"}, json={
            "base_url": "http://localhost:11434/v1", "model": "fixture", "api_key": "private-key"})
        assert response.status_code == 200
        assert "private-key" not in response.text
        assert "private-key" not in str(app.state.store.setting("model"))
        assert client.get("/").status_code == 200


def test_courses_sorted_by_recordings_and_active_jobs_not_hidden(tmp_path):
    app = create_app(tmp_path)
    app.state.auth.profile = {"owner": "alice"}
    store = app.state.store
    store.put("alice", "meta", "semester", {"id": "term"})
    for code, count in [("A", 0), ("B", 12), ("C", 5)]:
        store.put("alice", "course", code, {
            "id": code, "code": code, "semester": "term", "recording_count": count})
    store.put("alice", "job", "active", {
        "id": "active", "status": "running", "created_at": "2025-01-01"})
    for i in range(35):
        store.put("alice", "job", str(i), {
            "id": str(i), "status": "completed", "created_at": "2026-01-01"})
    # No lifespan: the startup recovery intentionally interrupts old running jobs.
    client = TestClient(app, base_url="http://127.0.0.1:8765")
    result = client.get("/api/state").json()
    assert [c["id"] for c in result["courses"]] == ["B", "C", "A"]
    assert result["jobs"][0]["id"] == "active"
    assert len(result["jobs"]) == 31


def test_timetable_automation_api_and_worker_complete_one_occurrence(tmp_path, monkeypatch):
    import time
    from datetime import datetime
    from zoneinfo import ZoneInfo

    current = datetime(2026, 10, 22, 10, tzinfo=ZoneInfo("Asia/Shanghai"))
    monkeypatch.setattr("buaa_agent.replay_schedule.local_now", lambda: current)
    app = create_app(tmp_path)
    app.state.auth.profile = {"owner": "alice"}
    app.state.store.put("alice", "course", "demo", {"id": "demo"})
    app.state.store.put("alice", "meta", "schedule", {"rows": [{
        "course_id": "demo", "date": "2026-10-21", "time": "08:00-09:35"}]})
    service = app.state.service
    service.sync_course = lambda *_: [{"id": "1001", "title": "昨日课程", "has_recording": True,
                                     "date": "2026-10-21T08:00:00+08:00"}]
    calls = []
    service.fetch_materials = lambda *args: calls.append(args) or {"segments": ["fixture"]}
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        headers = {"X-BUAA-Local": "1"}
        rule = client.put("/api/courses/demo/automation", headers=headers,
                          json={"enabled": True, "auto_analyze": False}).json()
        assert rule["interval_minutes"] == 120 and rule["pending_count"] == 1
        assert client.put("/api/courses/demo/automation", headers=headers,
                          json={"enabled": True, "interval_minutes": 30}).status_code == 422
        service.scheduler_tick()
        limit = time.monotonic() + 3
        while time.monotonic() < limit:
            rule = app.state.store.get("alice", "automation", "demo")
            if rule["pending_count"] == 0:
                break
            time.sleep(.02)
        assert rule["pending_count"] == 0 and rule["next_run"] is None
        assert calls == [("alice", "1001")]
