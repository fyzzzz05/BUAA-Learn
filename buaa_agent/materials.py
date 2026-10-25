import hashlib
import json
import math
from datetime import datetime
from urllib.parse import urlparse
from zoneinfo import ZoneInfo

from .portal import YJ, PortalError
from .store import now


def clean_url(url):
    """Only retain school HTTPS source URLs; drop temporary authentication query strings."""
    from .portal import ALLOWED_HOSTS
    p = urlparse(url or "")
    if p.scheme != "https" or p.hostname not in ALLOWED_HOSTS:
        return ""
    return p._replace(query="", fragment="").geturl()


def time_label(seconds):
    seconds = max(0, int(seconds or 0))
    return f"{seconds // 3600:02d}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"


def unpack_catalogue(body):
    result = body.get("result", {})
    rows = result.get("data") if isinstance(result, dict) else None
    if rows is None:
        raise PortalError("录播目录返回异常，不能判断课程是否没有回放")
    return rows


def lesson_from_row(row, course):
    try:
        content = json.loads(row.get("content") or "{}")
    except (TypeError, ValueError):
        content = {}
    playback = content.get("save_playback") or {}
    date = datetime.fromtimestamp(int(row.get("start_at") or 0), ZoneInfo("Asia/Shanghai"))
    return {"id": str(row["sub_id"]), "course_id": course["id"],
            "recording_course_id": str(row.get("course_id") or course["recording_id"]),
            "title": row.get("title", "未命名课次"), "course_name": course["name"],
            "date": date.isoformat(), "teachers": row.get("lecturer_name", ""),
            "duration": int(playback.get("contents_duration") or 0),
            "has_recording": bool(playback.get("contents") or content.get("playback", {}).get("url")),
            "source_url": "https://classroom.msa.buaa.edu.cn/livingroom?" +
              f"tenant_code=21&course_id={course['recording_id']}&sub_id={row['sub_id']}&sub_public=1"}


def sync_lessons(session, course):
    recording_id = session.establish_recording(course)
    course = {**course, "recording_id": recording_id}
    rows = unpack_catalogue(session.classroom("/courseapi/v2/course/catalogue",
                                             {"course_id": recording_id}, host=YJ))
    return course, [lesson_from_row(row, course) for row in rows]


def parse_segments(body):
    if body.get("code") != 0:
        raise PortalError("识别文本读取失败")
    result = []
    for group in body.get("list") or []:
        rows = group.get("all_content")
        if rows is None:
            try:
                rows = json.loads(group.get("trans_content") or "[]")
            except ValueError:
                raise PortalError("识别文本格式异常") from None
        for row in rows:
            text = str(row.get("Text") or "").strip()
            start = float(row.get("BeginSec") or 0)
            end = float(row.get("EndSec") or start)
            if text not in ("", "-") and math.isfinite(start) and math.isfinite(end) and end >= start >= 0:
                result.append({"start": start, "end": end, "text": text})
    result.sort(key=lambda row: (row["start"], row["end"]))
    return [{"id": f"s{i}", **row} for i, row in enumerate(result)]


def coverage(segments, duration):
    intervals = []
    for row in segments:
        start, end = row["start"], row["end"]
        if duration > 0:
            start, end = min(start, duration), min(end, duration)
        if intervals and start <= intervals[-1][1]:
            intervals[-1][1] = max(intervals[-1][1], end)
        else:
            intervals.append([start, end])
    gaps, last = [], 0
    for start, end in intervals:
        if start - last >= 45:
            gaps.append({"start": last, "end": start})
        last = end
    if duration - last >= 45:
        gaps.append({"start": last, "end": duration})
    seconds = sum(end - start for start, end in intervals)
    return {"seconds": seconds, "ratio": round(seconds / duration, 3) if duration else None,
            "gaps": gaps, "note": "时间区间覆盖率不代表识别准确率；空白可能是静音、练习或漏识别。"}


def load_materials(session, lesson):
    params = {"course_id": lesson["recording_course_id"], "sub_id": lesson["id"]}
    body = session.classroom("/courseapi/v3/portal-home-setting/get-sub-info", params)
    if body.get("code") != 0 or not body.get("data"):
        raise PortalError("这节课的材料尚不可用")
    info = body["data"]
    guid = info.get("resource_guid")
    if not guid:
        raise PortalError("尚未找到这节课的录播资源")
    resource_params = {"sub_id": lesson["id"], "format": "json", "resource_guid": guid}
    transcript = session.classroom("/courseapi/v3/web-socket/search-trans-result",
                                    resource_params, host=YJ)
    segments = parse_segments(transcript)
    ppt_rows, page = [], 1
    while True:
        ppt = session.classroom("/pptnote/v1/schedule/search-ppt", {
            **params, "resource_guid": guid, "page": page, "per_page": 100})
        if ppt.get("code") != 0:
            raise PortalError("PPT 截图索引读取失败，请稍后重试")
        rows = ppt.get("list") or []
        ppt_rows.extend(rows)
        if not rows or len(ppt_rows) >= int(ppt.get("total", len(ppt_rows))) or page >= 100:
            break
        page += 1
    slides, seen = [], set()
    for row in ppt_rows:
        try:
            item = json.loads(row["content"]) if isinstance(row.get("content"), str) else row["content"]
            url = clean_url(item.get("pptimgurl", ""))
            if url and url not in seen:
                slides.append({"id": f"p{len(slides)}", "start": float(row.get("created_sec", 0)),
                               "url": url})
                seen.add(url)
        except (ValueError, TypeError, KeyError):
            continue
    video_list = info.get("video_list") or {}
    video_rows = video_list.values() if isinstance(video_list, dict) else video_list
    videos = [{"type": "PPT" if str(row.get("type")) == "2" else "教师",
               "url": clean_url(row.get("preview_url", "")),
               "duration": int(row.get("duration") or 0)}
              for row in video_rows if isinstance(row, dict)]
    duration = int(info.get("duration") or lesson.get("duration") or 0)
    digest = hashlib.sha256(json.dumps({"segments": segments, "slides": slides},
                                       sort_keys=True).encode()).hexdigest()
    return {"lesson_id": lesson["id"], "segments": segments, "slides": slides,
            "videos": videos, "duration": duration, "coverage": coverage(segments, duration),
            "digest": digest, "fetched_at": now(),
            "limitations": ["自动识别存在错词与漏字。", "当前未对 PPT 图像做 OCR，也未自动核听原声。"]}
