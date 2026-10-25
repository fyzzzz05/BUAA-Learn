import os
import secrets
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Annotated
from urllib.parse import urlparse

from fastapi import FastAPI, HTTPException, Request
from fastapi import Path as ApiPath
from fastapi.responses import FileResponse, JSONResponse, Response
from pydantic import BaseModel, Field, SecretStr
from starlette.middleware.trustedhost import TrustedHostMiddleware

from .auth import Auth
from .learning import LearningError, Model, ask
from .materials import time_label
from .portal import LoginRequired, PortalError
from .service import Service
from .store import Store


class AutomationInput(BaseModel):
    enabled: bool
    interval_minutes: int = Field(default=120, ge=120, le=120)
    auto_analyze: bool = False


class ModelInput(BaseModel):
    base_url: str = Field(max_length=1000)
    model: str = Field(min_length=1, max_length=200)
    api_key: SecretStr | None = None


class QuestionInput(BaseModel):
    question: str = Field(min_length=2, max_length=3000)
    lesson_id: str | None = None


class TaskInput(BaseModel):
    done: bool


def create_app(directory=None, *, desktop_token=None):
    directory = Path(directory or os.getenv("BUAA_DATA_DIR", "./data")).resolve()
    store = Store(directory)
    auth = Auth(store)
    model = Model(store)
    service = Service(store, auth, model)

    @asynccontextmanager
    async def lifespan(app):
        service.start()
        yield
        service.close()
        auth.logout()
        # Workers blocked on an external provider may still finish during process shutdown.
        # SQLite connections are released by process exit after daemon workers terminate.

    app = FastAPI(title="BUAA Learning Agent", version="0.2.0", lifespan=lifespan,
                  docs_url=None, redoc_url=None, openapi_url=None)
    app.state.store, app.state.auth, app.state.service, app.state.model = store, auth, service, model
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=["127.0.0.1", "localhost", "[::1]"])

    @app.middleware("http")
    async def local_boundary(request: Request, call_next):
        if desktop_token and not secrets.compare_digest(
                request.headers.get("x-buaa-desktop", "").encode(), desktop_token.encode()):
            return JSONResponse({"detail": "请通过 BUAA Learn 桌面应用访问"}, status_code=403)
        if request.url.path.startswith("/api/") and request.method not in ("GET", "HEAD", "OPTIONS"):
            origin = request.headers.get("origin")
            if (request.headers.get("x-buaa-local") != "1" or
                    (origin and urlparse(origin).netloc != request.headers.get("host"))):
                return JSONResponse({"detail": "请通过本地学习页面发起操作"}, status_code=403)
        response = await call_next(request)
        response.headers["Cache-Control"] = "no-store"
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["Referrer-Policy"] = "no-referrer"
        response.headers["Content-Security-Policy"] = (
            "default-src 'self'; script-src 'self'; style-src 'self'; "
            "img-src 'self' https://resource.msa.buaa.edu.cn; "
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"
        )
        return response

    if desktop_token:
        @app.get("/api/desktop/health")
        def desktop_health() -> dict:
            return {"status": "ready", "version": "0.2.0"}

        @app.post("/api/desktop/quit")
        def desktop_quit() -> dict:
            shutdown = getattr(app.state, "desktop_shutdown", None)
            if shutdown:
                shutdown()
            return {"status": "stopping"}

    @app.exception_handler(PortalError)
    async def portal_error(request, exc):
        return JSONResponse({"detail": str(exc)}, status_code=401 if isinstance(exc, LoginRequired) else 502)

    @app.exception_handler(LearningError)
    async def model_error(request, exc):
        return JSONResponse({"detail": str(exc)}, status_code=400)

    def owner():
        if not auth.owner:
            raise LoginRequired("请先登录北航统一身份认证")
        return auth.owner

    @app.get("/api/state")
    def state() -> dict:
        account = auth.owner
        semester = store.get(account, "meta", "semester", {})
        courses = [row for row in store.list(account, "course") if row["semester"] == semester.get("id")]
        courses.sort(key=lambda row: (-row.get("recording_count", 0), row["code"]))
        notices = [{**point, "lesson_id": record["lesson_id"], "course_id": record["course_id"],
                    "course_name": record["course_name"], "lesson_title": record["lesson_title"]}
                   for record in store.list(account, "analysis") for point in record["notices"]]
        jobs = store.list(account, "job")
        active = [row for row in jobs if row["status"] in ("queued", "running")]
        recent = [row for row in jobs if row["status"] not in ("queued", "running")][:30]
        return {"auth": auth.status(), "semester": semester, "courses": courses,
                "schedule": store.get(account, "meta", "schedule", {"rows": []}),
                "jobs": active + recent,
                "automations": store.list(account, "automation"),
                "tasks": [row for row in store.list(account, "task") if not row.get("superseded")],
                "notices": notices,
                "model": model.public()}

    @app.post("/api/auth/login", status_code=202)
    def login() -> dict:
        auth.start()
        return auth.status()

    @app.post("/api/auth/logout")
    def logout() -> dict:
        auth.logout()
        return auth.status()

    @app.post("/api/sync", status_code=202)
    def sync() -> dict:
        auth.require()
        return service.enqueue("sync")

    @app.post("/api/schedule/sync", status_code=202)
    def sync_schedule() -> dict:
        auth.require()
        return service.enqueue("schedule")

    @app.get("/api/courses/{course_id}")
    def course(course_id: str) -> dict:
        account = owner()
        row = service.owned_course(account, course_id)
        lessons = [lesson for lesson in store.list(account, "lesson") if lesson["course_id"] == course_id]
        lessons.sort(key=lambda lesson: lesson["date"], reverse=True)
        return {"course": row, "lessons": lessons,
                "automation": store.get(account, "automation", course_id),
                "chat": [chat for chat in store.list(account, "chat") if chat["course_id"] == course_id][:20]}

    @app.post("/api/courses/{course_id}/sync", status_code=202)
    def sync_course(course_id: str) -> dict:
        service.owned_course(owner(), course_id)
        auth.require()
        return service.enqueue("course", course_id)

    @app.put("/api/courses/{course_id}/automation")
    def automation(course_id: str, body: AutomationInput) -> dict:
        return service.configure_automation(owner(), course_id, **body.model_dump())

    @app.post("/api/courses/{course_id}/automation/run", status_code=202)
    def run_automation(course_id: str) -> dict:
        service.owned_course(owner(), course_id)
        if not store.get(owner(), "automation", course_id, {}).get("enabled"):
            raise HTTPException(400, "请先开启这门课的自动化")
        return service.enqueue("automation", course_id)

    @app.get("/api/lessons/{lesson_id}")
    def lesson(lesson_id: Annotated[str, ApiPath(pattern=r"^\d+$")]) -> dict:
        account = owner()
        return {"lesson": service.owned_lesson(account, lesson_id),
                "materials": store.get(account, "materials", lesson_id),
                "analysis": store.get(account, "analysis", lesson_id),
                "slides_pdf": service.slides_pdf_status(account, lesson_id)}

    @app.post("/api/lessons/{lesson_id}/slides/export", status_code=202)
    def export_slides_pdf(lesson_id: Annotated[str, ApiPath(pattern=r"^\d+$")]) -> dict:
        account = owner()
        service.owned_lesson(account, lesson_id)
        if not store.get(account, "materials", lesson_id, {}).get("slides"):
            raise HTTPException(400, "这节课暂无 PPT 截图，请先获取或刷新课堂材料")
        return service.enqueue("slides-pdf", lesson_id)

    @app.get("/api/lessons/{lesson_id}/slides/pdf")
    def download_slides_pdf(lesson_id: Annotated[str, ApiPath(pattern=r"^\d+$")]) -> FileResponse:
        account = owner()
        lesson = service.owned_lesson(account, lesson_id)
        if not service.slides_pdf_status(account, lesson_id):
            raise HTTPException(404, "尚未生成去重 PDF，请先导出")
        return FileResponse(service.slides_pdf_path(account, lesson_id), media_type="application/pdf",
                            filename=f"{lesson['course_name']}-{lesson['title']}-去重课件.pdf")

    @app.post("/api/lessons/{lesson_id}/materials", status_code=202)
    def fetch_lesson(lesson_id: str) -> dict:
        service.owned_lesson(owner(), lesson_id)
        auth.require()
        return service.enqueue("materials", lesson_id)

    @app.post("/api/lessons/{lesson_id}/analyze", status_code=202)
    def analyze_lesson(lesson_id: str) -> dict:
        service.owned_lesson(owner(), lesson_id)
        if not model.ready:
            raise LearningError("请先在设置中配置模型服务")
        return service.enqueue("analyze", lesson_id)

    @app.post("/api/courses/{course_id}/ask")
    def question(course_id: str, body: QuestionInput) -> dict:
        account = owner()
        service.owned_course(account, course_id)
        lessons = [row for row in store.list(account, "lesson") if row["course_id"] == course_id]
        if body.lesson_id:
            lesson = service.owned_lesson(account, body.lesson_id)
            if lesson["course_id"] != course_id:
                raise HTTPException(400, "课次不属于当前课程")
            lessons = [lesson]
        identifiers = {row["id"] for row in lessons}
        sources = [row for row in store.list(account, "materials") if row["lesson_id"] in identifiers]
        answer = ask(model, body.question, sources, lessons)
        from uuid import uuid4
        answer.update(id=uuid4().hex, course_id=course_id, question=body.question)
        store.put(account, "chat", answer["id"], answer)
        return answer

    @app.patch("/api/tasks/{task_id}")
    def task(task_id: str, body: TaskInput) -> dict:
        account = owner()
        row = store.get(account, "task", task_id)
        if not row:
            raise HTTPException(404, "未找到这项任务")
        row["done"] = body.done
        store.put(account, "task", task_id, row)
        return row

    @app.put("/api/model")
    def configure_model(body: ModelInput) -> dict:
        model.configure(body.base_url, body.model,
                        body.api_key.get_secret_value() if body.api_key is not None else None)
        store.set_setting("model", {"base_url": model.base_url, "model": model.model})
        return model.public()

    @app.get("/api/lessons/{lesson_id}/export")
    def export(lesson_id: str) -> Response:
        account = owner()
        lesson = service.owned_lesson(account, lesson_id)
        material = store.get(account, "materials", lesson_id)
        analysis = store.get(account, "analysis", lesson_id)
        lines = [f"# {lesson['course_name']} · {lesson['title']}", "", lesson["source_url"], "",
                 "自动识别与模型分析可能错误；作业和重要事项需核对原话。", ""]
        if analysis:
            if analysis.get("timeline_status") == "partial":
                lines += ["主题时间轴仍有待整理内容，已完成的学习内容保留。", ""]
                lines += [f"- {warning}" for warning in analysis.get("timeline_warnings", [])] + [""]
            for section in analysis["sections"]:
                title = " · " + section["title"] if section.get("title") else ""
                lines += [f"## {time_label(section['start'])}–{time_label(section['end'])}{title}",
                          "", section["summary"], ""]
                if section.get("knowledge_points"):
                    lines += ["本段知识点：", ""] + [f"- {row['text']}" for row in section["knowledge_points"]] + [""]
            if analysis.get("timeline_status") == "partial" and analysis.get("text_summaries"):
                lines += ["## 已完成的文本摘要", ""]
                for section in analysis["text_summaries"]:
                    lines += [f"### {time_label(section['start'])}–{time_label(section['end'])}",
                              "", section["summary"], ""]
            for key, title in [("knowledge_points", "知识点"), ("assignments", "作业（待核验）"), ("notices", "重要事项")]:
                lines += [f"## {title}", ""]
                for row in analysis[key]:
                    source_range = row.get("source_range")
                    refs = (f"{time_label(source_range['start'])}–{time_label(source_range['end'])}"
                            if source_range else ", ".join(time_label(analysis["evidence"][x]["start"])
                                                          for x in row["evidence_ids"]))
                    lines += [f"- {row['text']}（{refs}）"]
                    if "deadline" in row:
                        lines += [f"  截止：{row['deadline']}；对象：{row['audience']}；提交：{row['submission']}"]
                if not analysis[key]:
                    lines += ["本次材料未提取到此类内容；识别文本可能不完整。"]
                lines += [""]
            lines += ["## 建议复习（非老师布置）", ""] + [f"- {x}" for x in analysis["review_suggestions"]] + [""]
        if material:
            lines += ["## 原始识别文本", ""]
            lines += [f"[{time_label(row['start'])}–{time_label(row['end'])}] {row['text']}\n"
                      for row in material["segments"]]
        return Response("\n".join(lines), media_type="text/markdown; charset=utf-8",
                        headers={"Content-Disposition": f'attachment; filename="lesson-{lesson_id}.md"'})

    app.frontend("/", directory=Path(__file__).parent / "static")
    return app


app = create_app(desktop_token=os.getenv("BUAA_DESKTOP_TOKEN"))
