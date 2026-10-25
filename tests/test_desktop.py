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
