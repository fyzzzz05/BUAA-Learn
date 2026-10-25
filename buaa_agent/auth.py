import hashlib
import os
import tempfile
import threading
import time

from .portal import Session


class Auth:
    def __init__(self, store):
        self.store = store
        self.session = None
        self.profile = store.setting("profile", {})
        self.state = "offline"
        self.message = "登录北航统一身份认证，获取本学期课程"
        self.lock = threading.RLock()
        self.cancel = threading.Event()
        self.thread = None

    @property
    def owner(self):
        return self.profile.get("owner", "")

    def status(self):
        with self.lock:
            return {"state": self.state, "message": self.message,
                    "logged_in": self.session is not None, "profile": self.profile}

    def start(self):
        with self.lock:
            if self.thread and self.thread.is_alive():
                return
            self.cancel.clear()
            self.state = "waiting"
            self.message = "请在新开的 Chrome 窗口中完成学校登录"
            self.thread = threading.Thread(target=self._login, daemon=True)
            self.thread.start()

    def _login(self):
        from playwright.sync_api import sync_playwright
        try:
            with tempfile.TemporaryDirectory(prefix="buaa-login-") as directory:
                with sync_playwright() as pw:
                    channel = os.getenv("BUAA_BROWSER_CHANNEL", "chrome")
                    options = {"headless": False}
                    if channel != "chromium":
                        options["channel"] = channel
                    context = pw.chromium.launch_persistent_context(directory, **options)
                    try:
                        page = context.pages[0] if context.pages else context.new_page()
                        page.goto("https://spoc.buaa.edu.cn/spocnewht/cas",
                                  wait_until="domcontentloaded", timeout=60000)
                        deadline = time.monotonic() + 600
                        data = None
                        while time.monotonic() < deadline and not self.cancel.is_set():
                            if page.is_closed():
                                break
                            try:
                                data = page.evaluate("""() => {
                                  if(location.origin !== 'https://spoc.buaa.edu.cn')return null;
                                  const token=localStorage.getItem('token');
                                  const user=JSON.parse(localStorage.getItem('userinfo')||'null');
                                  if(!token||!user?.jsdm||!user?.yhdm)return null;
                                  return {token,refresh_token:localStorage.getItem('refreshToken')||'',
                                    role:String(user.jsdm),id:String(user.yhdm),
                                    name:user.xm||user.yhmc||user.realname||'北航同学'};
                                }""")
                            except Exception:
                                data = None
                            if data:
                                break
                            page.wait_for_timeout(1000)
                        if self.cancel.is_set():
                            return
                        if not data:
                            raise ValueError("登录窗口已关闭或等待超时，请重试")
                        if data["role"] != "01":
                            raise ValueError("请在教学空间切换为学生角色后重试")
                        owner = hashlib.sha256(data["id"].encode()).hexdigest()[:24]
                        session = Session(data["token"], data["refresh_token"], data["role"],
                                          owner, data["name"])
                        terms = session.terms()  # Verify independently of the browser.
                        # Complete the recording platform's own CAS hop in the SAME browser,
                        # while the school SSO cookie is still available. Afterwards use HTTP only.
                        with self.lock:
                            self.message = "学校登录成功，正在连接课堂录播系统"
                        courses = session.courses(terms[0]["xnxq"]) if terms else []
                        for course in courses[:3]:
                            try:
                                entry = session.recording_entry(course)
                                page.goto(entry, wait_until="domcontentloaded", timeout=45000)
                                record_deadline = time.monotonic() + 35
                                while time.monotonic() < record_deadline and not self.cancel.is_set():
                                    cookies = context.cookies()
                                    for cookie in cookies:
                                        domain = cookie["domain"].lstrip(".")
                                        if domain == "msa.buaa.edu.cn" or domain.endswith(".msa.buaa.edu.cn"):
                                            session.client.cookies.set(cookie["name"], cookie["value"],
                                                                       domain=cookie["domain"], path=cookie["path"])
                                    if session.recording_token():
                                        break
                                    page.wait_for_timeout(500)
                                if session.recording_token():
                                    break
                            except Exception:
                                # A course without recordings must not invalidate portal login.
                                continue
                        with self.lock:
                            if self.cancel.is_set():
                                session.client.close()
                                return
                            if self.session:
                                self.session.client.close()
                            self.session = session
                            self.profile = {"owner": owner, "name": data["name"]}
                            self.store.set_setting("profile", self.profile)
                            self.state = "connected"
                            self.message = ("已连接教学空间与录播系统" if session.recording_token()
                                            else "已连接教学空间；录播授权未完成，可重新登录重试")
                    finally:
                        context.close()
        except Exception as exc:
            with self.lock:
                self.state = "error"
                # Never forward Playwright exception text: it may contain callback query tokens.
                self.message = str(exc) if isinstance(exc, ValueError) else (
                    "登录未完成。请检查 Chrome 是否安装及学校网络是否可用，然后重试。"
                )

    def logout(self):
        self.cancel.set()
        with self.lock:
            if self.session:
                self.session.client.close()
            self.session = None
            self.state = "offline"
            self.message = "已断开学校连接；本地课程记录仍可查看"

    def require(self):
        from .portal import LoginRequired
        with self.lock:
            if not self.session:
                raise LoginRequired("请先登录北航统一身份认证")
            return self.session

