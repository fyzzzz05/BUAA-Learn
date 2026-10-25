from types import SimpleNamespace

import pytest

from buaa_agent.learning import Extraction, LearningError, Point, analyze, grounded_points
from buaa_agent.timeline import (
    TimelineError,
    TimelinePlan,
    Topic,
    build_timeline,
    ground_topics,
)


def topic(start, title="主题"):
    return Topic(title=title, summary=title + "概括", start_id=start)


def plan(starts, covered):
    return TimelinePlan(topics=[topic(start) for start in starts], covered_until_id=covered)


def fixture(rows=None):
    rows = rows or [{"id": f"s{i}", "start": i * 60, "end": (i + 1) * 60, "text": "课堂原文"}
                    for i in range(4)]
    lesson = {"id": "1001", "course_id": "demo", "course_name": "示例", "title": "第一课"}
    material = {"segments": rows, "digest": "fixture", "limitations": [], "coverage": {}}
    return lesson, material


def notes(topics, covered, **kwargs):
    return {"summary": "已完成的文本摘要", "topics": topics, "covered_until_id": covered,
            "knowledge_points": [{"text": "有效知识点", "evidence_ids": ["s0"]}], **kwargs}


def test_ends_are_derived_from_topic_starts_instead_of_model_supplied_ends():
    evidence = {row["id"]: row for row in fixture()[1]["segments"]}
    result = ground_topics(plan(["s0", "s2"], "s3"), evidence)
    assert [(row["start"], row["end"]) for row in result] == [(0, 120), (120, 240)]
    assert [(row["start_id"], row["end_id"]) for row in result] == [("s0", "s1"), ("s2", "s3")]


def test_unordered_and_repeated_starts_are_normalized_without_regeneration():
    evidence = {row["id"]: row for row in fixture()[1]["segments"]}
    result = ground_topics(plan(["s2", "s0", "s2"], "s3"), evidence)
    assert [(row["start_id"], row["end_id"]) for row in result] == [("s0", "s1"), ("s2", "s3")]


@pytest.mark.parametrize(("starts", "covered"), [
    (["s0"], "s99"), (["s99"], "s3"), (["s0", "s2"], "s1"),
])
def test_invented_ids_and_coverage_before_last_topic_are_rejected(starts, covered):
    evidence = {row["id"]: row for row in fixture()[1]["segments"]}
    with pytest.raises(TimelineError):
        ground_topics(plan(starts, covered), evidence)


def test_omitted_prefix_and_suffix_are_explicitly_unclassified():
    evidence = {row["id"]: row for row in fixture()[1]["segments"]}
    result = ground_topics(plan(["s1"], "s2"), evidence)
    assert [(row["kind"], row["start_id"], row["end_id"]) for row in result] == [
        ("unclassified", "s0", "s0"), ("chapter", "s1", "s2"), ("unclassified", "s3", "s3")]
    assert result[0]["title"] == result[-1]["title"] == "待归类原文"


def test_empty_plan_preserves_all_transcript_as_unclassified():
    evidence = {row["id"]: row for row in fixture()[1]["segments"]}
    result = ground_topics(plan([], ""), evidence)
    assert len(result) == 1
    assert (result[0]["kind"], result[0]["start_id"], result[0]["end_id"]) == ("unclassified", "s0", "s3")


def test_more_than_twelve_citations_are_allowed_but_invented_ids_still_fail():
    evidence = {f"s{i}": {"start": i, "end": i + 1, "text": "source"} for i in range(30)}
    finding = Point(text="跨较长讲解的知识点", evidence_ids=list(evidence))
    result = grounded_points([finding], evidence)[0]
    assert len(result["evidence_ids"]) == 30
    assert result["source_range"] == {"start_id": "s0", "end_id": "s29", "start": 0, "end": 30}
    with pytest.raises(LearningError):
        grounded_points([Point(text="概念", evidence_ids=["s0", "s999"])], evidence)


def test_transcript_gaps_remain_distinct_from_unclassified_available_text():
    segments = [{"id": "s0", "start": 60, "end": 120},
                {"id": "s1", "start": 240, "end": 300}]
    evidence = {row["id"]: row for row in segments}
    chapters = ground_topics(plan([], ""), evidence)
    findings = {"knowledge_points": [{"text": "概念", "evidence_ids": ["s1"]}],
                "assignments": [], "notices": []}
    result = build_timeline(chapters, segments, findings, duration=420)
    assert [row["kind"] for row in result] == ["gap", "unclassified", "gap", "unclassified", "gap"]
    assert [(row["start"], row["end"]) for row in result] == [
        (0, 60), (60, 120), (120, 240), (240, 300), (300, 420)]
    assert result[3]["knowledge_points"][0]["text"] == "概念"
    assert all("无法判断" in row["summary"] for row in result if row["kind"] == "gap")


def test_omitted_tail_does_not_retry_or_discard_any_learning_content():
    calls = []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        return notes([topic("s0").model_dump()], "s2")
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture())
    assert calls == [Extraction]
    assert result["timeline_status"] == "partial"
    assert result["sections"][-1]["kind"] == "unclassified"
    assert result["sections"][-1]["start_id"] == "s3"
    assert result["knowledge_points"][0]["text"] == "有效知识点"
    assert result["text_summaries"][0]["summary"] == "已完成的文本摘要"


def test_only_topic_plan_is_regenerated_when_source_id_is_invalid():
    calls, prompts = [], []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        prompts.append(prompt)
        if output_model is Extraction:
            return notes([topic("s999").model_dump()], "s3")
        return {"topics": [topic("s0").model_dump()], "covered_until_id": "s3"}
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture())
    assert calls == [Extraction, TimelinePlan]
    assert "上次主题校验失败" in prompts[1]
    assert result["timeline_status"] == "complete"
    assert len(result["knowledge_points"]) == 1


def test_repeated_topic_failure_preserves_notes_assignments_and_source_links():
    calls = []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        if output_model is Extraction:
            return notes([topic("s999").model_dump()], "s3", assignments=[
                {"text": "课后任务", "evidence_ids": ["s0"]}])
        return {"topics": [topic("s999").model_dump()], "covered_until_id": "s3"}
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture())
    assert calls == [Extraction, TimelinePlan, TimelinePlan]
    assert result["timeline_status"] == "partial"
    assert result["sections"][0]["kind"] == "unclassified"
    assert result["sections"][0]["knowledge_points"][0]["text"] == "有效知识点"
    assert result["sections"][0]["assignments"][0]["verification"] == "待核验"
    assert result["timeline_warnings"]


def test_malformed_topic_fields_do_not_force_a_notes_retry():
    calls = []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        if output_model is Extraction:
            return notes("malformed topics", None)
        return {"topics": [topic("s0").model_dump()], "covered_until_id": "s3"}
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture())
    assert calls == [Extraction, TimelinePlan]
    assert result["timeline_status"] == "complete"


def test_topic_transport_failure_falls_back_without_repeating_notes_or_transport():
    calls = []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        if output_model is Extraction:
            return notes([topic("s999").model_dump()], "s3")
        raise LearningError("模型请求失败（HTTP 503）")
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture())
    assert calls == [Extraction, TimelinePlan]
    assert result["timeline_status"] == "partial"
    assert "HTTP 503" in result["timeline_warnings"][0]


def test_same_topic_can_merge_across_technical_chunks_without_losing_citations():
    rows = [{"id": f"s{i}", "start": i * 300, "end": (i + 1) * 300,
             "text": "x" * 5000} for i in range(4)]
    calls = []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        if output_model is TimelinePlan:
            return {"topics": [topic("t0", "回顾").model_dump(),
                               topic("t1", "核心概念").model_dump(),
                               topic("t3", "应用").model_dump()], "covered_until_id": "t3"}
        index = 0 if len(calls) == 1 else 2
        return notes([topic(f"s{index}").model_dump(), topic(f"s{index + 1}").model_dump()],
                     f"s{index + 1}", knowledge_points=[
                         {"text": "核心定义", "evidence_ids": ["s1" if index == 0 else "s2"]}])
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture(rows))
    assert calls == [Extraction, Extraction, TimelinePlan]
    assert result["timeline_version"] == 2
    assert result["timeline_status"] == "complete"
    middle = result["sections"][1]
    assert (middle["start"], middle["end"]) == (300, 900)
    assert (middle["start_id"], middle["end_id"]) == ("s1", "s2")
    assert middle["knowledge_points"][0]["evidence_ids"] == ["s1", "s2"]


def test_invalid_final_merge_preserves_previously_resolved_themes_and_all_notes():
    rows = [{"id": f"s{i}", "start": i * 10, "end": (i + 1) * 10,
             "text": "x" * 6000} for i in range(2)]
    calls = []
    def complete(system, prompt, *, output_model):
        calls.append(output_model)
        if output_model is TimelinePlan:
            return {"topics": [topic("t99").model_dump()], "covered_until_id": "t1"}
        index = len(calls) - 1
        return notes([topic(f"s{index}", f"原主题{index}").model_dump()], f"s{index}",
                     knowledge_points=[{"text": f"知识点{index}", "evidence_ids": [f"s{index}"]}])
    model = SimpleNamespace(complete=complete, public=lambda: {"model": "synthetic"})
    result = analyze(model, *fixture(rows))
    assert calls.count(Extraction) == 2
    assert calls.count(TimelinePlan) == 3
    assert result["timeline_status"] == "partial"
    assert [row["title"] for row in result["sections"]] == ["原主题0", "原主题1"]
    assert len(result["knowledge_points"]) == 2
