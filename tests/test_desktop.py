from fastapi.testclient import TestClient

from buaa_agent.app import create_app


def test_desktop_requires_launch_token_for_static_and_read_requests(tmp_path):
    app = create_app(tmp_path, desktop_token="a" * 64)
    with TestClient(app, base_url="http://127.0.0.1:12345") as client:
        for route in ("/", "/app.js", "/api/state", "/api/desktop/health"):
            assert client.get(route).status_code == 403
            assert client.get(route, headers={"X-BUAA-Desktop": "b" * 64}).status_code == 403
            assert client.get(route, headers={"X-BUAA-Desktop": "a" * 64}).status_code == 200


def test_desktop_shutdown_requires_token_and_existing_write_boundary(tmp_path):
    app = create_app(tmp_path, desktop_token="a" * 64)
    calls = []
    app.state.desktop_shutdown = lambda: calls.append("quit")
    with TestClient(app, base_url="http://127.0.0.1:12345") as client:
        route = "/api/desktop/quit"
        assert client.post(route, headers={"X-BUAA-Local": "1"}).status_code == 403
        assert client.post(route, headers={"X-BUAA-Desktop": "a" * 64}).status_code == 403
        headers = {"X-BUAA-Desktop": "a" * 64, "X-BUAA-Local": "1"}
        assert client.post(route, headers={**headers, "Origin": "https://evil.test"}).status_code == 403
        assert not calls
        assert client.post(route, headers=headers).status_code == 200
        assert calls == ["quit"]


def test_web_mode_has_no_desktop_control_endpoints(tmp_path):
    client = TestClient(create_app(tmp_path), base_url="http://127.0.0.1:8765")
    assert client.get("/api/state").status_code == 200
    assert client.post("/api/desktop/quit", headers={"X-BUAA-Local": "1"}).status_code == 404


def test_transcript_download_uses_owned_full_materials_and_desktop_token(tmp_path):
    app = create_app(tmp_path, desktop_token="a" * 64)
    app.state.auth.profile = {"owner": "alice", "name": "示例"}
    store = app.state.store
    store.put("alice", "course", "demo", {"name": "线性代数"})
    store.put("alice", "lesson", "1001", {"course_id": "demo", "course_name": "线性代数", "title": '第一课 / "矩阵"'})
    store.put("alice", "materials", "1001", {"segments": [
        {"start": 10, "end": 20, "text": "矩阵的第一段"},
        {"start": 30, "end": 40, "text": "最后一段 English"},
    ]})
    store.put("alice", "lesson", "1002", {"course_id": "demo", "title": "暂无文本"})
    store.put("bob", "lesson", "1003", {"title": "其他账号"})
    store.put("bob", "materials", "1003", {"segments": [{"start": 0, "end": 1, "text": "private"}]})
    headers = {"X-BUAA-Desktop": "a" * 64}
    with TestClient(app, base_url="http://127.0.0.1:12345") as client:
        route = "/api/lessons/1001/transcript.txt"
        assert client.get(route).status_code == 403
        response = client.get(route + "?search=矩阵&start=10&end=20", headers=headers)
        assert response.status_code == 200
        assert response.headers["content-type"] == "text/plain; charset=utf-8"
        assert response.headers["content-disposition"].startswith("attachment;")
        assert "%E7%BA%BF%E6%80%A7%E4%BB%A3%E6%95%B0" in response.headers["content-disposition"]
        assert response.content.decode("utf-8-sig") == (
            "[00:00:10–00:00:20] 矩阵的第一段\r\n"
            "[00:00:30–00:00:40] 最后一段 English\r\n"
        )
        for lesson_id in ("1002", "1003"):
            assert client.get(f"/api/lessons/{lesson_id}/transcript.txt", headers=headers).status_code == 404
