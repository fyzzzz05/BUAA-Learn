import pytest

from buaa_agent.learning import (
    Assignment,
    LearningError,
    Point,
    analyze,
    batches,
    grounded_points,
    task_records,
)
from buaa_agent.materials import coverage, parse_segments


def chapter_payload(summary="摘要", **kwargs):
    return {"summary": summary, "topics": [{"title": "概念", "summary": summary,
                                            "start_id": "s0"}], "covered_until_id": "s0", **kwargs}


def test_coverage_merges_overlaps_and_reports_real_gaps():
    segments = [{"start": 0, "end": 30}, {"start": 20, "end": 50}, {"start": 110, "end": 120}]
    result = coverage(segments, 180)
    assert result["seconds"] == 60
    assert result["gaps"] == [{"start": 50, "end": 110}, {"start": 120, "end": 180}]


def test_missing_or_invalid_asr_does_not_become_a_fake_transcript():
    assert parse_segments({"code": 0, "list": []}) == []
    result = parse_segments({"code": 0, "list": [{"all_content": [
        {"BeginSec": 10, "EndSec": 4, "Text": "bad"},
        {"BeginSec": 0, "EndSec": 3, "Text": "-"},
        {"BeginSec": 4, "EndSec": 10, "Text": "A valid sentence"},
    ]}]})
    assert len(result) == 1
    assert result[0]["text"] == "A valid sentence"


def test_all_segments_are_analyzed_without_truncating_the_lesson():
    segments = [{"id": f"s{i}", "start": i, "end": i+1, "text": "word "*500}
                for i in range(20)]
    chunks = batches(segments, 4000)
    assert [row["id"] for chunk in chunks for row in chunk] == [f"s{i}" for i in range(20)]


def test_hallucinated_source_rejects_entire_finding():
    with pytest.raises(LearningError):
        grounded_points([Point(text="invented", evidence_ids=["s0", "s999"])],
                        {"s0": {"text": "Actual source"}})


def test_unsubstantiated_deadline_is_removed():
    finding = Assignment(text="Prepare a report", evidence_ids=["s0"],
                         deadline="Friday", deadline_quote="Submit by Friday")
    rows = grounded_points([finding], {"s0": {"text": "Prepare a report for discussion."}})
    assert rows[0]["deadline"] == "未说明"
    assert rows[0]["verification"] == "待核验"


def test_verbatim_deadline_is_retained_but_still_needs_verification():
    finding = Assignment(text="Prepare a report", evidence_ids=["s0"],
                         deadline="Friday", deadline_quote="Submit by Friday")
    rows = grounded_points([finding], {"s0": {"text": "Submit by Friday in the portal."}})
    assert rows[0]["deadline"] == "Friday"
    assert rows[0]["verification"] == "待核验"


def test_real_but_unrelated_quote_cannot_support_an_invented_deadline():
    finding = Assignment(text="Prepare a report", evidence_ids=["s0"],
                         deadline="Friday", deadline_quote="Prepare a report")
    rows = grounded_points([finding], {"s0": {"text": "Prepare a report for discussion."}})
    assert rows[0]["deadline"] == "未说明"


def test_bilingual_retrieval_and_answer_citations():
    from buaa_agent.learning import ask
    class FakeModel:
        def complete(self, system, prompt, *, output_model=None):
            if "关键词" in system:
                return {"keywords": ["冠词", "article"]}
            return {"answer": "这节课讨论了冠词。 [c0]", "evidence_ids": ["c0"]}
    lesson = {"id": "1001", "title": "示例", "date": "2026-10-19", "source_url": "https://example.invalid"}
    materials = [{"lesson_id": "1001", "segments": [
        {"id": "s0", "start": 1, "end": 10, "text": "Use the definite article in this case."}]}]
    result = ask(FakeModel(), "老师如何解释冠词？", materials, [lesson])
    assert result["sources"][0]["lesson_id"] == "1001"
    assert "[c0]" in result["answer"]


def test_completed_task_survives_reanalysis():
    result = {"lesson_id": "1001", "course_id": "demo", "course_name": "示例课程",
              "lesson_title": "第一课", "assignments": [{"text": "Read chapter 2"}]}
    task = task_records(result, {})[0]
    task["done"] = True
    assert task_records(result, {task["id"]: task})[0]["done"] is True


def test_analysis_does_not_publish_partial_output_when_late_chunk_fails():
    class FakeModel:
        count = 0
        def public(self): return {"model": "fixture"}
        def complete(self, *_, **kwargs):
            self.count += 1
            if self.count == 2:
                raise LearningError("provider failed")
            return chapter_payload(knowledge_points=[{"text": "概念", "evidence_ids": ["s0"]}])

    rows = [{"id": f"s{i}", "start": i, "end": i+1, "text": "x"*6000} for i in range(3)]
    lesson = {"id": "1001", "course_id": "demo", "course_name": "示例", "title": "第一课"}
    materials = {"segments": rows, "digest": "abc", "limitations": [], "coverage": {}}
    with pytest.raises(LearningError, match="provider failed"):
        analyze(FakeModel(), lesson, materials)


def analysis_fixture():
    lesson = {"id": "1001", "course_id": "demo", "course_name": "示例", "title": "第一课"}
    material = {"segments": [{"id": "s0", "start": 0, "end": 5, "text": "概念与例子"}],
                "digest": "fixture", "limitations": [], "coverage": {}}
    return lesson, material


def test_bad_structure_is_retried_with_schema_without_leaking_values():
    class FakeModel:
        prompts = []
        def public(self): return {"model": "fixture"}
        def complete(self, system, prompt, *, output_model=None):
            self.prompts.append(prompt)
            assert '"$defs"' in system
            if len(self.prompts) == 1:
                return {"summary": "摘要", "knowledge_points": [{"text": "private value"}]}
            return chapter_payload(knowledge_points=[{"text": "概念", "evidence_ids": ["s0"]}])
    model = FakeModel()
    progress = []
    result = analyze(model, *analysis_fixture(), progress.append)
    assert len(model.prompts) == 2
    assert "knowledge_points.0.evidence_ids" in model.prompts[1]
    assert "private value" not in model.prompts[1]
    assert result["knowledge_points"][0]["evidence_ids"] == ["s0"]
    assert progress[-1]["progress"] == {"completed": 1, "total": 1, "stage": "课堂文本分析"}


def test_repeated_invalid_output_reports_chunk_and_field():
    class FakeModel:
        count = 0
        def public(self): return {"model": "fixture"}
        def complete(self, *_, **kwargs):
            self.count += 1
            return {"summary": {"private": "value"}}
    model = FakeModel()
    with pytest.raises(LearningError, match=r"第 1/1 段.*重试2次.*summary.*string_type") as error:
        analyze(model, *analysis_fixture())
    assert model.count == 3
    assert "private" not in str(error.value)


def test_invented_citations_are_retried_and_never_published():
    class FakeModel:
        count = 0
        def public(self): return {"model": "fixture"}
        def complete(self, *_, **kwargs):
            self.count += 1
            return {"summary": "摘要", "notices": [{"text": "事项", "evidence_ids": ["s999"]}]}
    model = FakeModel()
    with pytest.raises(LearningError, match="evidence_ids"):
        analyze(model, *analysis_fixture())
    assert model.count == 3


def test_invalid_json_can_be_retried_but_transport_errors_are_not():
    class FakeModel:
        count = 0
        def public(self): return {"model": "fixture"}
        def complete(self, *_, **kwargs):
            self.count += 1
            if self.count == 1:
                raise LearningError("模型返回的内容不是有效 JSON")
            return chapter_payload()
    model = FakeModel()
    assert analyze(model, *analysis_fixture())["sections"][0]["summary"] == "摘要"
    assert model.count == 2
