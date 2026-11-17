from types import SimpleNamespace

import httpx

from buaa_agent.app import create_app
from buaa_agent.portal import Session


def fake_session(owner="alice", recording=False):
    client = SimpleNamespace(closed=False)
    client.close = lambda: setattr(client, "closed", True)
    return SimpleNamespace(owner=owner, client=client,
                           recording_token=lambda: "token" if recording else "")


def test_login_queues_sync_even_without_recording_authorization(tmp_path):
    app = create_app(tmp_path)
    session = fake_session()
    app.state.auth._connected(session, "alice", "同学")
    status = app.state.auth.status()
    assert status["logged_in"] and not status["recording_authorized"]
    assert "录播授权未完成" in status["message"]
    jobs = app.state.store.list("alice", "job")
    assert len(jobs) == 1 and jobs[0]["kind"] == "sync" and jobs[0]["status"] == "queued"


def test_cancelled_login_does_not_connect_or_queue_sync(tmp_path):
    app = create_app(tmp_path)
    session = fake_session(recording=True)
    app.state.auth.cancel.set()
    app.state.auth._connected(session, "alice", "同学")
    assert session.client.closed
    assert not app.state.auth.status()["logged_in"]
    assert app.state.store.list("alice", "job") == []


def test_login_hands_school_domain_cookie_to_recording_session(tmp_path, monkeypatch):
    app = create_app(tmp_path)
    session = Session("portal", "", "01", "owner", "同学", httpx.Client())
    session.terms = lambda: [{"xnxq": "term"}]
    session.courses = lambda _: [{"id": "course"}]
    session.recording_entry = lambda _: "https://classroom.msa.buaa.edu.cn/entry"
    monkeypatch.setattr("buaa_agent.auth.Session", lambda *args: session)

    class Page:
        def goto(self, *args, **kwargs): pass
        def is_closed(self): return False
        def evaluate(self, *args):
            return {"token": "portal", "refresh_token": "", "role": "01", "id": "student", "name": "同学"}
        def wait_for_timeout(self, *args): pass

    class Context:
        pages = [Page()]
        def cookies(self):
            return [{"domain": ".buaa.edu.cn", "name": "_token", "path": "/",
                     "value": '%7Bi%3A0%3Bs%3A6%3A%22_token%22%3Bi%3A1%3Bs%3A6%3A%22secret%22%3B%7D'}]
        def close(self): pass

    class Playwright:
        chromium = SimpleNamespace(launch_persistent_context=lambda *args, **kwargs: Context())
        def __enter__(self): return self
        def __exit__(self, *args): pass

    monkeypatch.setattr("playwright.sync_api.sync_playwright", lambda: Playwright())
    app.state.auth._login()
    status = app.state.auth.status()
    assert status["logged_in"] and status["recording_authorized"]
    assert session.recording_token() == "secret"
    assert app.state.store.list(status["profile"]["owner"], "job")[0]["kind"] == "sync"
