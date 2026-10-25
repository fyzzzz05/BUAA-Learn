import httpx
import pytest

from buaa_agent.portal import PortalError, Session


def test_portal_token_is_never_forwarded_across_school_hosts():
    requests = []
    def handler(request):
        requests.append(request)
        if request.url.host == "yjapi.msa.buaa.edu.cn":
            return httpx.Response(302, headers={"location": "https://classroom.msa.buaa.edu.cn/coursedetail?course_id=123"})
        return httpx.Response(200, text="OK")
    session = Session("portal-secret", "", "01", "owner", "同学",
                      httpx.Client(transport=httpx.MockTransport(handler)))
    session.follow_school_url("https://yjapi.msa.buaa.edu.cn/entry")
    assert len(requests) == 2
    assert all("token" not in request.headers for request in requests)


def test_recording_bearer_is_read_from_school_cookie():
    captured = []
    client = httpx.Client(transport=httpx.MockTransport(
        lambda request: captured.append(request) or httpx.Response(200, json={"code": 0})))
    client.cookies.set("_token", '%7Bi%3A0%3Bs%3A6%3A%22_token%22%3Bi%3A1%3Bs%3A6%3A%22secret%22%3B%7D',
                       domain=".msa.buaa.edu.cn")
    session = Session("portal-secret", "", "01", "owner", "同学", client)
    session.classroom("/example")
    assert captured[0].headers["authorization"] == "Bearer secret"
    assert "token" not in captured[0].headers


def test_redirect_to_unrelated_host_is_rejected():
    client = httpx.Client(transport=httpx.MockTransport(lambda _: httpx.Response(
        302, headers={"location": "https://unrelated.example/collect"})))
    session = Session("secret", "", "01", "owner", "同学", client)
    with pytest.raises(PortalError):
        session.follow_school_url("https://yjapi.msa.buaa.edu.cn/entry")


def test_expired_session_without_refresh_requires_login():
    from buaa_agent.portal import LoginRequired
    client = httpx.Client(transport=httpx.MockTransport(lambda _: httpx.Response(200, json={"code": 403})))
    session = Session("expired", "", "01", "owner", "同学", client)
    with pytest.raises(LoginRequired):
        session.terms()
