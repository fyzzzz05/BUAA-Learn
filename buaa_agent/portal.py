"""Read-only adapters for the BUAA teaching portal and recording platform."""
import re
import threading
from dataclasses import dataclass, field
from urllib.parse import parse_qs, unquote, urlencode, urlparse

import httpx

PORTAL = "https://spoc.buaa.edu.cn/spocnewht"
CLASSROOM = "https://classroom.msa.buaa.edu.cn"
YJ = "https://yjapi.msa.buaa.edu.cn"
ALLOWED_HOSTS = {"spoc.buaa.edu.cn", "classroom.msa.buaa.edu.cn", "yjapi.msa.buaa.edu.cn",
                 "resource.msa.buaa.edu.cn", "portal.msa.buaa.edu.cn", "www.msa.buaa.edu.cn"}


class PortalError(Exception):
    pass


class LoginRequired(PortalError):
    pass


@dataclass(repr=False)
class Session:
    token: str
    refresh_token: str
    role: str
    owner: str
    name: str
    client: httpx.Client = field(default_factory=lambda: httpx.Client(timeout=30))
    lock: threading.RLock = field(default_factory=threading.RLock)

    def headers(self):
        return {"Token": "Inco-" + self.token, "RoleCode": self.role,
                "X-Requested-With": "XMLHttpRequest", "Referer": "https://spoc.buaa.edu.cn/spocnew/"}

    def request(self, path, params=None, method="GET", retry=True):
        with self.lock:
            try:
                response = self.client.request(method, PORTAL + path, params=params,
                                               headers=self.headers(),
                                               json={} if method == "POST" else None)
                body = response.json()
            except (httpx.HTTPError, ValueError):
                raise PortalError("教学空间暂时无法连接，请稍后重试") from None
            if body.get("code") in (1, 401, 403) or response.status_code in (401, 403):
                if retry and self.refresh_token:
                    self.refresh()
                    return self.request(path, params, method, retry=False)
                raise LoginRequired("学校登录已过期，请重新登录")
            if response.status_code != 200 or body.get("code") != 200:
                raise PortalError(f"教学空间接口返回异常（{path}）")
            return body.get("content")

    def refresh(self):
        try:
            response = self.client.post(PORTAL + "/sys/refreshToken", headers=self.headers(),
                                        json={"refreshToken": self.refresh_token})
            body = response.json()
            data = body.get("content") or {}
            if body.get("code") != 200 or not data.get("token"):
                raise LoginRequired("学校登录已过期，请重新登录")
            self.token = data["token"]
            self.refresh_token = data.get("refreshToken", self.refresh_token)
        except (httpx.HTTPError, ValueError):
            raise LoginRequired("学校登录已过期，请重新登录") from None

    def terms(self):
        return self.request("/component/queryXnxq", method="POST") or []

    def courses(self, semester):
        rows = self.request("/jxkj/queryKclb", {"xnxq": semester, "lx": "1"}) or []
        return [{"id": row["kcid"], "name": row["kcmc"], "code": row.get("jw_kcdm", ""),
                 "teachers": row.get("skjs", ""), "semester": semester,
                 "task_id": row.get("rwh", "")} for row in rows if str(row.get("juese")) == "3"]

    def schedule(self, start, end):
        rows = self.request("/jxkj/queryRlData", {"zksrq": start, "zjsrq": end, "rllx": "1"}) or []
        return [{"course_id": row.get("kcid"), "name": row.get("kcmc"),
                 "date": row.get("rq"), "time": row.get("strkcsj"),
                 "room": row.get("skdd", ""), "teachers": row.get("jsxm", ""),
                 "binding": row.get("zjdm", "")} for row in rows]

    def follow_school_url(self, url, max_redirects=8):
        """Do not send teaching-portal token headers to another host."""
        for _ in range(max_redirects):
            parsed = urlparse(url)
            if parsed.scheme != "https" or parsed.hostname not in ALLOWED_HOSTS:
                raise PortalError("录播系统返回了无法识别的跳转地址")
            response = self.client.get(url, follow_redirects=False)
            if response.is_redirect:
                from urllib.parse import urljoin
                url = urljoin(url, response.headers["location"])
                continue
            return response
        raise PortalError("录播系统跳转次数过多")

    def recording_entry(self, course):
        role = str(self.request("/kc/queryYhSflx", {"kcid": course["id"]}))
        if role == "6":
            role = "1"
        is_student = role not in ("1", "3")
        params = {"kcid": course["id"], "type": "", "isxs": int(is_student)}
        if not is_student:
            tasks = self.request("/lbsp/queryRwhList", {"kcid": course["id"]}) or []
            if not tasks:
                raise PortalError("这门课程尚未关联录播教学班")
            selected = next((x for x in tasks if x.get("RWH") == course.get("task_id")), tasks[0])
            params["rwh"] = selected["RWH"]
        signature = self.request("/yun/getHxyTimestampAndSignature2", params) or {}
        if not signature.get("bind_id") or not signature.get("ktsl_url"):
            raise PortalError("这门课程尚未提供录播入口")
        keys = ("bind_id", "username", "realname", "tenant_code", "timestamp", "signature", "allow_live")
        base = signature["ktsl_url"]
        separator = "&" if "?" in base and not base.endswith(("?", "&")) else ""
        if "?" not in base:
            separator = "?"
        return base + separator + urlencode({k: signature[k] for k in keys if k in signature})

    def establish_recording(self, course):
        with self.lock:
            try:
                response = self.follow_school_url(self.recording_entry(course))
                # Some installations redirect to a portal route; IDs live in its query.
                query = parse_qs(urlparse(str(response.url)).query)
                if query.get("course_id"):
                    return query["course_id"][0]
                try:
                    data = response.json()
                except ValueError:
                    data = None
                if data is not None:
                    found = find_course_id(data)
                    if found:
                        return str(found)
                raise PortalError("学校未返回可识别的录播课程 ID，暂时无法确认这门课的回放情况")
            except httpx.HTTPError:
                raise PortalError("录播系统连接失败，请稍后重试") from None

    def classroom(self, path, params=None, host=CLASSROOM):
        with self.lock:
            try:
                token = self.recording_token()
                response = self.client.get(host + path, params=params,
                                           headers={"Authorization": "Bearer " + token} if token else {})
                body = response.json()
            except (httpx.HTTPError, ValueError):
                raise PortalError("课堂材料接口暂时不可用") from None
            if response.status_code in (401, 403) or body.get("code") in (401, 403, 5002):
                raise LoginRequired("录播系统登录已过期，请重新同步课程")
            return body

    def recording_token(self):
        for cookie in self.client.cookies.jar:
            if cookie.name == "_token" and cookie.domain.lstrip(".") in ALLOWED_HOSTS | {"msa.buaa.edu.cn", "buaa.edu.cn"}:
                value = unquote(cookie.value)
                match = re.search(r's:\d+:"_token";i:\d+;s:\d+:"(.+?)";', value)
                if match:
                    return match.group(1)
        return ""


def find_course_id(value):
    if isinstance(value, dict):
        if value.get("course_id"):
            return value["course_id"]
        for child in value.values():
            result = find_course_id(child)
            if result:
                return result
    if isinstance(value, list):
        for child in value:
            result = find_course_id(child)
            if result:
                return result
    return None
