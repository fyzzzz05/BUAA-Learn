"""Resolve model topic boundaries against real, ordered transcript segments."""
from pydantic import BaseModel, ConfigDict, Field


class TimelineError(ValueError):
    pass


class Topic(BaseModel):
    model_config = ConfigDict(extra="forbid")
    title: str = Field(min_length=1, max_length=200)
    summary: str = Field(min_length=1, max_length=4000)
    start_id: str


class TimelinePlan(BaseModel):
    model_config = ConfigDict(extra="forbid")
    topics: list[Topic]
    covered_until_id: str


def ground_topics(plan, evidence):
    """Derive ends from ordered topic starts; keep unclassified text explicit."""
    identifiers = list(evidence)
    if not identifiers:
        return []
    positions = {identifier: i for i, identifier in enumerate(identifiers)}
    if not plan.topics:
        return [unclassified(identifiers[0], identifiers[-1], evidence)]
    if plan.covered_until_id not in positions:
        raise TimelineError("covered_until_id 必须使用本段实际提供的材料编号")
    by_position = {}
    for topic in plan.topics:
        if topic.start_id not in positions:
            raise TimelineError("topics.start_id 必须使用本段实际提供的材料编号")
        # Reordering and duplicate anchors need no model regeneration.
        by_position.setdefault(positions[topic.start_id], topic)
    ordered = sorted(by_position)
    last = positions[plan.covered_until_id]
    if last < ordered[-1]:
        raise TimelineError("covered_until_id 不能早于最后一个主题的 start_id")
    resolved = []
    if ordered[0] > 0:
        resolved.append(unclassified(identifiers[0], identifiers[ordered[0] - 1], evidence))
    for i, first in enumerate(ordered):
        end = ordered[i + 1] - 1 if i + 1 < len(ordered) else last
        rows = [evidence[key] for key in identifiers[first:end + 1]]
        resolved.append({**by_position[first].model_dump(), "kind": "chapter",
                         "end_id": identifiers[end], "start": rows[0]["start"],
                         "end": max(row["end"] for row in rows)})
    if last + 1 < len(identifiers):
        resolved.append(unclassified(identifiers[last + 1], identifiers[-1], evidence))
    return resolved


def unclassified(first, last, evidence):
    identifiers = list(evidence)
    rows = [evidence[key] for key in identifiers[identifiers.index(first):identifiers.index(last) + 1]]
    return {"kind": "unclassified", "title": "待归类原文", "start_id": first, "end_id": last,
            "start": rows[0]["start"], "end": max(row["end"] for row in rows),
            "summary": "这一范围有识别文本，尚未归入已确认的主题。可查看原文；已提取的知识点与任务仍保留。"}


def build_timeline(chapters, segments, findings, duration=0):
    """Split genuine transcript gaps; derive all times and citations locally."""
    positions = {row["id"]: i for i, row in enumerate(segments)}
    timeline = []
    for chapter in chapters:
        rows = segments[positions[chapter["start_id"]]:positions[chapter["end_id"]] + 1]
        runs, run, end = [], [], 0
        for row in rows:
            if run and row["start"] - end > 45:
                runs.append(run)
                run = []
            run.append(row)
            end = max(end, row["end"])
        if run:
            runs.append(run)
        for index, run in enumerate(runs):
            ids = {row["id"] for row in run}
            section = {**chapter, "title": chapter["title"] + ("（续）" if index else ""),
                       "kind": chapter.get("kind", "chapter"), "start": run[0]["start"],
                       "end": max(row["end"] for row in run),
                       "start_id": run[0]["id"], "end_id": run[-1]["id"]}
            for key in ("knowledge_points", "assignments", "notices"):
                section[key] = [point for point in findings[key]
                                if ids.intersection(point["evidence_ids"])]
            timeline.append(section)

    result, covered_until = [], 0
    for section in timeline:
        if section["start"] - covered_until > 45:
            result.append(gap(covered_until, section["start"]))
        result.append(section)
        covered_until = max(covered_until, section["end"])
    if duration - covered_until > 45:
        result.append(gap(covered_until, duration))
    return result


def gap(start, end):
    return {"kind": "gap", "title": "识别文本空白", "start": start, "end": end,
            "summary": "这一时段没有可用的识别文本，无法判断讲授内容，请核对原始回放。"}
