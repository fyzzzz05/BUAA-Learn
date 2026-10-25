import hashlib
import queue
import threading
import uuid
from datetime import timedelta

from . import learning, materials, replay_schedule, slide_export
from .portal import LoginRequired, PortalError
from .store import now


class Service:
    def __init__(self, store, auth, model):
        self.store, self.auth, self.model = store, auth, model
        self.queue = queue.Queue()
        self.stop = threading.Event()
        self.lock = threading.RLock()
        self.workers = []

    def start(self):
        # Pending work cannot safely resume without the user's in-memory school session.
        for job in self.store.list(self.auth.owner, "job"):
            if job["status"] in ("queued", "running"):
                job.update(status="interrupted", message="服务已重启，请重新运行此任务", finished_at=now())
                self.store.put(self.auth.owner, "job", job["id"], job)
        for target in (self._worker, self._scheduler):
            thread = threading.Thread(target=target, daemon=True)
            thread.start()
            self.workers.append(thread)

    def close(self):
        self.stop.set()
        self.auth.cancel.set()
        for thread in self.workers:
            thread.join(timeout=3)

    def enqueue(self, kind, target="", owner=None):
        owner = owner or self.auth.owner
        if not owner:
            raise LoginRequired("请先登录并同步课程")
        with self.lock:
            for row in self.store.list(owner, "job"):
                if row["kind"] == kind and row["target"] == target and row["status"] in ("queued", "running"):
                    return row
            job = {"id": uuid.uuid4().hex, "kind": kind, "target": target,
                   "status": "queued", "message": "等待处理", "created_at": now()}
            if kind in ("analyze", "materials", "slides-pdf"):
                lesson = self.store.get(owner, "lesson", target, {})
                job.update(course_name=lesson.get("course_name", ""), lesson_title=lesson.get("title", ""))
            elif kind in ("course", "automation"):
                job["course_name"] = self.store.get(owner, "course", target, {}).get("name", "")
            self.store.put(owner, "job", job["id"], job)
            self.queue.put((owner, job["id"]))
            return job

    def _worker(self):
        while not self.stop.is_set():
            try:
                owner, identifier = self.queue.get(timeout=0.5)
            except queue.Empty:
                continue
            job = self.store.get(owner, "job", identifier)
            try:
                self._update(owner, job, status="running", message="正在处理", started_at=now())
                self._execute(owner, job)
                self._update(owner, job, status="completed", message="处理完成", finished_at=now())
            except LoginRequired as exc:
                self._update(owner, job, status="needs_login", message=str(exc), finished_at=now())
            except (PortalError, learning.LearningError) as exc:
                self._update(owner, job, status="failed", message=str(exc), finished_at=now())
            except Exception:
                self._update(owner, job, status="failed", message="任务未完成，原始记录已保留，请重试",
                             finished_at=now())
            finally:
                self.queue.task_done()

    def _update(self, owner, job, **changes):
        job.update(changes)
        self.store.put(owner, "job", job["id"], job)

    def owned_course(self, owner, identifier):
        row = self.store.get(owner, "course", identifier)
        if not row:
            raise PortalError("未找到本人课程，请先同步本学期课程")
        return row

    def owned_lesson(self, owner, identifier):
        row = self.store.get(owner, "lesson", identifier)
        if not row:
            raise PortalError("未找到该课次，请先同步课程目录")
        self.owned_course(owner, row["course_id"])
        return row

    def _session(self, owner):
        session = self.auth.require()
        if session.owner != owner:
            raise LoginRequired("登录账号已改变，请在当前账号中重新发起任务")
        return session

    def sync_semester(self, owner):
        session = self._session(owner)
        terms = session.terms()
        if not terms or not terms[0].get("xnxq"):
            raise PortalError("未找到默认学期，请稍后重试")
        semester = terms[0]["xnxq"]
        for row in session.courses(semester):
            old = self.store.get(owner, "course", row["id"], {})
            self.store.put(owner, "course", row["id"], {**old, **row, "synced_at": now()})
        self.store.put(owner, "meta", "semester", {"id": semester, "synced_at": now()})
        self.sync_timetable(owner)

    def sync_course(self, owner, identifier):
        course = self.owned_course(owner, identifier)
        session = self._session(owner)
        try:
            updated, lessons = materials.sync_lessons(session, course)
        except PortalError as exc:
            course.update(sync_error=str(exc), checked_at=now())
            self.store.put(owner, "course", identifier, course)
            raise
        updated.update(checked_at=now(), sync_error="", lesson_count=len(lessons),
                       recording_count=sum(row["has_recording"] for row in lessons))
        self.store.put(owner, "course", identifier, updated)
        for lesson in lessons:
            old = self.store.get(owner, "lesson", lesson["id"], {})
            self.store.put(owner, "lesson", lesson["id"], {**old, **lesson})
        return lessons

    def fetch_materials(self, owner, identifier):
        lesson = self.owned_lesson(owner, identifier)
        if not lesson["has_recording"]:
            raise PortalError("这节课尚未生成回放")
        session = self._session(owner)
        # Refresh the signed cross-system session before authenticated material reads.
        session.establish_recording(self.owned_course(owner, lesson["course_id"]))
        data = materials.load_materials(session, lesson)
        self.store.put(owner, "materials", identifier, data)
        lesson.update(materials_at=now(), segment_count=len(data["segments"]),
                      slide_count=len(data["slides"]))
        self.store.put(owner, "lesson", identifier, lesson)
        return data

    def analyze_lesson(self, owner, identifier, progress):
        lesson = self.owned_lesson(owner, identifier)
        if not self.model.ready:
            raise learning.LearningError("请先在设置中配置模型服务")
        data = self.store.get(owner, "materials", identifier)
        if not data:
            progress("正在获取课堂文本和 PPT 索引")
            data = self.fetch_materials(owner, identifier)
        result = learning.analyze(self.model, lesson, data, progress)
        self.store.put(owner, "analysis", identifier, result)
        previous = {row["id"]: row for row in self.store.list(owner, "task")}
        new_tasks = learning.task_records(result, previous)
        active = {row["id"] for row in new_tasks}
        # Keep removed/reworded tasks as superseded rather than silently retaining them as current.
        for row in previous.values():
            if row["lesson_id"] == identifier and row["id"] not in active:
                self.store.put(owner, "task", row["id"], {**row, "superseded": True})
        for row in new_tasks:
            self.store.put(owner, "task", row["id"], {**row, "superseded": False})
        lesson.update(analysis_at=now())
        self.store.put(owner, "lesson", identifier, lesson)

    def _execute(self, owner, job):
        kind, target = job["kind"], job["target"]
        def progress(text):
            self._update(owner, job, **(text if isinstance(text, dict) else {"message": text}))
        if kind == "sync":
            self.sync_semester(owner)
        elif kind == "schedule":
            self.sync_timetable(owner)
        elif kind == "course":
            self.sync_course(owner, target)
        elif kind == "materials":
            self.fetch_materials(owner, target)
        elif kind == "analyze":
            self.analyze_lesson(owner, target, progress)
        elif kind == "slides-pdf":
            self.export_slides_pdf(owner, target, progress)
        elif kind == "automation":
            self.run_automation(owner, target, progress)
        else:
            raise PortalError("无法识别的任务")

    def slides_pdf_path(self, owner, lesson_id):
        account = hashlib.sha256(owner.encode()).hexdigest()[:24]
        identifier = hashlib.sha256(lesson_id.encode()).hexdigest()[:24]
        return self.store.directory / "exports" / account / f"slides-{identifier}.pdf"

    def slides_pdf_status(self, owner, lesson_id):
        record = self.store.get(owner, "slides_pdf", lesson_id)
        if not record or not self.slides_pdf_path(owner, lesson_id).is_file():
            return None
        material = self.store.get(owner, "materials", lesson_id, {})
        return {**record, "current": record["digest"] == slide_export.slides_digest(material)}

    def export_slides_pdf(self, owner, lesson_id, progress):
        lesson = self.owned_lesson(owner, lesson_id)
        material = self.store.get(owner, "materials", lesson_id, {})
        if not material.get("slides"):
            raise PortalError("这节课暂无 PPT 截图，请先获取或刷新课堂材料")
        path = self.slides_pdf_path(owner, lesson_id)
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        path.parent.chmod(0o700)
        progress("正在下载课堂截图并按画面内容去重")
        result = slide_export.export_slides(material, lesson, path, progress)
        self.store.put(owner, "slides_pdf", lesson_id, {**result, "created_at": now()})

    def sync_timetable(self, owner):
        session = self._session(owner)
        today = replay_schedule.local_now().date()
        # Fetch real dates rather than guessing recurring weekdays across term breaks.
        start, end = today - timedelta(days=7), today + timedelta(days=21)
        rows = session.schedule(start.isoformat(), end.isoformat())
        self.store.put(owner, "meta", "automation_schedule", {
            "rows": rows, "start": start.isoformat(), "end": end.isoformat(), "synced_at": now()})
        monday = today - timedelta(days=today.weekday())
        self.store.put(owner, "meta", "schedule", {"rows": [row for row in rows
            if monday.isoformat() <= (row.get("date") or "") <= (monday + timedelta(days=6)).isoformat()],
            "synced_at": now()})
        for rule in self.store.list(owner, "automation"):
            self._plan_automation(owner, rule["course_id"])

    def _schedule_rows(self, owner):
        rolling = self.store.get(owner, "meta", "automation_schedule", {})
        weekly = self.store.get(owner, "meta", "schedule", {})
        return rolling.get("rows", weekly.get("rows", []))

    def _plan_automation(self, owner, course_id, **changes):
        with self.lock:
            fresh = self.store.get(owner, "automation", course_id)
            if not fresh:
                return None
            fresh.update(changes)
            fresh = replay_schedule.plan(fresh, self._schedule_rows(owner))
            self.store.put(owner, "automation", course_id, fresh)
            return fresh

    def configure_automation(self, owner, course_id, enabled, interval_minutes=120, auto_analyze=False):
        self.owned_course(owner, course_id)
        if enabled and auto_analyze and not self.model.ready:
            raise learning.LearningError("请先配置模型，再开启自动分析")
        with self.lock:
            old = self.store.get(owner, "automation", course_id, {})
            record = {**old, "course_id": course_id, "enabled": enabled,
                      "interval_minutes": 120, "auto_analyze": auto_analyze,
                      "known": old.get("known", []), "updated_at": now()}
            self.store.put(owner, "automation", course_id, record)
            return self._plan_automation(owner, course_id)

    def scheduler_tick(self):
        owner = self.auth.owner
        if not owner:
            return
        rules = [r for r in self.store.list(owner, "automation") if r.get("enabled")]
        if not rules:
            return
        current = replay_schedule.local_now()
        calendar = self.store.get(owner, "meta", "automation_schedule", {})
        synced = replay_schedule.parse_date(calendar.get("synced_at"))
        requested = replay_schedule.parse_date(calendar.get("requested_at"))
        if (getattr(self.auth, "session", None) and
                (not synced or synced.date() < current.date()) and
                (not requested or current - requested >= replay_schedule.CHECK_INTERVAL)):
            # Throttle failures too; keep the last successful timetable on errors.
            self.store.put(owner, "meta", "automation_schedule", {
                **calendar, "requested_at": current.isoformat()})
            self.enqueue("schedule", owner=owner)
        for rule in rules:
            rule = self._plan_automation(owner, rule["course_id"])
            if rule["next_run"] and replay_schedule.parse_date(rule["next_run"]) <= current:
                self.enqueue("automation", rule["course_id"], owner=owner)

    def _scheduler(self):
        while not self.stop.wait(10):
            try:
                self.scheduler_tick()
            except Exception:
                # One failed tick must not kill future scheduled work.
                continue

    def run_automation(self, owner, course_id, progress):
        config = self._plan_automation(owner, course_id)
        if not config or not config["enabled"]:
            return
        current = replay_schedule.local_now()
        due = {key: watch for key, watch in config["watches"].items()
               if watch["status"] == "waiting" and replay_schedule.parse_date(watch["due_at"]) <= current}
        if not due:
            progress("等待上课次日开始检查" if config["next_run"] else "等待同步该课程的后续课表")
            return
        # Persist the retry time before any request, including expired-login failures.
        watches = config["watches"]
        for watch in due.values():
            watch["next_check"] = (current + replay_schedule.CHECK_INTERVAL).isoformat()
        self._plan_automation(owner, course_id, watches=watches, last_run=now())
        lessons = self.sync_course(owner, course_id)
        known = set(config["known"])
        for key, watch in due.items():
            candidates = [row for row in lessons if replay_schedule.matches(watch, row)]
            for lesson in candidates:
                if not lesson["has_recording"] or lesson["id"] in known:
                    continue
                fresh = self.store.get(owner, "automation", course_id)
                if not fresh or not fresh["enabled"]:
                    return
                progress(f"发现新回放：{lesson['title']}")
                data = self.fetch_materials(owner, lesson["id"])
                if not data.get("segments") and not data.get("slides"):
                    progress(f"{lesson['title']} 的回放已生成，课堂材料仍在准备，2小时后重查")
                    continue
                if fresh["auto_analyze"]:
                    self.analyze_lesson(owner, lesson["id"], progress)
                known.add(lesson["id"])
                self._plan_automation(owner, course_id, known=sorted(known))
            if candidates and all(row["has_recording"] and row["id"] in known for row in candidates):
                # Use the fresh watch map; another job may have added future dates.
                fresh = self.store.get(owner, "automation", course_id)
                watches = fresh["watches"]
                watches[key].update(status="completed", completed_at=now())
                self._plan_automation(owner, course_id, watches=watches)
        remaining = self.store.get(owner, "automation", course_id)["pending_count"]
        progress(f"本次检查完成，{remaining} 个课次等待回放；未就绪的课次每2小时重查")
