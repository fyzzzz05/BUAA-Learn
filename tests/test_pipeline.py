"""A synthetic lesson exercises API -> job worker -> analysis -> tasks -> export."""
import time

from fastapi.testclient import TestClient

from buaa_agent.app import create_app
from buaa_agent.learning import LessonPriorities


def test_full_analysis_task_completion_and_markdown_export(tmp_path):
    app = create_app(tmp_path)
    store, model = app.state.store, app.state.model
    app.state.auth.profile = {"owner": "fixture", "name": "示例同学"}
    store.put("fixture", "course", "demo", {
        "id": "demo", "name": "示例课程", "semester": "2026-20271", "code": "DEMO", "teachers": "示例教师"})
    store.put("fixture", "lesson", "1001", {
        "id": "1001", "course_id": "demo", "course_name": "示例课程", "title": "第一课",
        "date": "2026-10-19T08:00:00+08:00", "has_recording": True,
        "source_url": "https://classroom.msa.buaa.edu.cn/livingroom?course_id=0&sub_id=1001"})
    store.put("fixture", "materials", "1001", {
        "lesson_id": "1001", "segments": [{"id": "s0", "start": 10, "end": 20,
                                         "text": "Read chapter two. Submit by Friday."}],
        "digest": "synthetic", "coverage": {}, "limitations": []})
    model.configure("http://localhost:12345/v1", "synthetic")
    def complete(*_, output_model=None, **kwargs):
        if output_model is LessonPriorities:
            return {"notice_indices": [], "review_suggestions": ["回顾第二章概念"]}
        return {
        "summary": "本节课的合成测试材料。", "knowledge_points": [],
        "topics": [{"title": "阅读任务", "summary": "本节课的合成测试材料。", "start_id": "s0"}],
        "covered_until_id": "s0",
        "assignments": [{"text": "阅读第二章", "evidence_ids": ["s0"],
                         "deadline": "Friday", "deadline_quote": "Submit by Friday"}],
        "notices": [], "review_suggestions": ["回顾第二章概念"]}
    model.complete = complete
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        headers = {"X-BUAA-Local": "1"}
        response = client.post("/api/lessons/1001/analyze", headers=headers)
        assert response.status_code == 202
        job_id = response.json()["id"]
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            job = store.get("fixture", "job", job_id)
            if job["status"] not in ("queued", "running"):
                break
            time.sleep(0.02)
        assert job["status"] == "completed", job
        result = client.get("/api/lessons/1001").json()
        assert result["analysis"]["assignments"][0]["verification"] == "待核验"
        assert result["analysis"]["assignments"][0]["deadline"] == "Friday"
        assert result["analysis"]["priorities_version"] == 1
        assert result["analysis"]["review_suggestions"] == ["回顾第二章概念"]
        task = store.list("fixture", "task")[0]
        assert client.patch(f"/api/tasks/{task['id']}", headers=headers,
                            json={"done": True}).json()["done"] is True
        export = client.get("/api/lessons/1001/export")
        assert "阅读第二章" in export.text
        assert "00:00:10" in export.text
        assert "合成测试" in export.text
        assert "阅读任务" in export.text
        assert "00:00:10–00:00:20" in export.text


def test_model_failure_keeps_previous_successful_analysis(tmp_path):
    from types import SimpleNamespace

    from buaa_agent.learning import LearningError
    from buaa_agent.service import Service
    from buaa_agent.store import Store

    store = Store(tmp_path)
    auth = SimpleNamespace(owner="fixture")
    def fail(*_, **kwargs): raise LearningError("synthetic provider outage")
    model = SimpleNamespace(ready=True, public=lambda: {"model": "fixture"}, complete=fail)
    service = Service(store, auth, model)
    store.put("fixture", "course", "demo", {"id": "demo"})
    store.put("fixture", "lesson", "1001", {
        "id": "1001", "course_id": "demo", "course_name": "示例", "title": "第一课"})
    store.put("fixture", "materials", "1001", {
        "segments": [{"id": "s0", "start": 1, "end": 3, "text": "Synthetic source"}],
        "digest": "x", "coverage": {}, "limitations": []})
    store.put("fixture", "analysis", "1001", {"summary": "previous result"})
    import pytest
    with pytest.raises(LearningError):
        service.analyze_lesson("fixture", "1001", lambda _: None)
    assert store.get("fixture", "analysis", "1001") == {"summary": "previous result"}


def test_unresolved_topics_still_save_valid_notes_tasks_and_export(tmp_path):
    from buaa_agent.learning import Extraction

    app = create_app(tmp_path)
    app.state.auth.profile = {"owner": "fixture", "name": "示例同学"}
    store, model = app.state.store, app.state.model
    store.put("fixture", "course", "demo", {"id": "demo"})
    store.put("fixture", "lesson", "1001", {
        "id": "1001", "course_id": "demo", "course_name": "示例", "title": "第一课",
        "source_url": "https://example.invalid/synthetic"})
    store.put("fixture", "materials", "1001", {
        "segments": [{"id": "s0", "start": 0, "end": 30, "text": "Read chapter two."}],
        "digest": "fixture", "coverage": {}, "limitations": []})
    model.configure("http://localhost:12345/v1", "synthetic")
    calls = []
    def complete(*_, output_model):
        calls.append(output_model)
        payload = {"topics": [{"title": "主题", "summary": "摘要", "start_id": "s999"}],
                   "covered_until_id": "s0"}
        if output_model is Extraction:
            payload.update(summary="有效的文本摘要", knowledge_points=[
                {"text": "有效知识点", "evidence_ids": ["s0"]}], assignments=[
                {"text": "阅读第二章", "evidence_ids": ["s0"]}])
        return payload
    model.complete = complete
    app.state.service.analyze_lesson("fixture", "1001", lambda _: None)
    saved = store.get("fixture", "analysis", "1001")
    assert saved["timeline_status"] == "partial"
    assert len(store.list("fixture", "task")) == 1
    assert calls.count(Extraction) == 1
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        export = client.get("/api/lessons/1001/export")
        assert export.status_code == 200
        assert all(text in export.text for text in (
            "待归类原文", "有效知识点", "阅读第二章", "有效的文本摘要", "主题时间轴仍有待整理内容"))
