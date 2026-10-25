"""Timetable-backed replay watches. All day boundaries use Asia/Shanghai."""
import hashlib
from datetime import datetime, time, timedelta
from zoneinfo import ZoneInfo

SHANGHAI = ZoneInfo("Asia/Shanghai")
CHECK_INTERVAL = timedelta(hours=2)


def local_now():
    return datetime.now(SHANGHAI)


def parse_date(value):
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return parsed.replace(tzinfo=SHANGHAI) if parsed.tzinfo is None else parsed.astimezone(SHANGHAI)
    except (ValueError, TypeError, AttributeError):
        return None


def plan(record, rows, current=None):
    current = current or local_now()
    record = {**record, "watches": {key: dict(value) for key, value in record.get("watches", {}).items()}}
    record.setdefault("tracking_from", (current.date() - timedelta(days=1)).isoformat())
    record.update(schedule_policy="next_day", interval_minutes=120)
    for row in rows:
        if row.get("course_id") != record["course_id"]:
            continue
        try:
            day = datetime.fromisoformat(row["date"]).date()
            start, end = row["time"].replace("—", "-").replace("–", "-").split("-")
            begins = datetime.combine(day, time.fromisoformat(start.strip()), SHANGHAI)
            ends = datetime.combine(day, time.fromisoformat(end.strip()), SHANGHAI)
            if ends <= begins:
                continue
        except (ValueError, KeyError, TypeError, AttributeError):
            continue
        if day.isoformat() < record["tracking_from"]:
            continue
        key = hashlib.sha256(f"{row['course_id']}|{day}|{row['time']}".encode()).hexdigest()[:24]
        record["watches"].setdefault(key, {
            "date": day.isoformat(), "time": row["time"], "start": begins.isoformat(),
            "end": ends.isoformat(), "due_at": datetime.combine(
                day + timedelta(days=1), time.min, SHANGHAI).isoformat(), "status": "waiting"})
    pending = [watch for watch in record["watches"].values() if watch["status"] == "waiting"]
    checks = [max(parse_date(watch["due_at"]), parse_date(watch.get("next_check")) or parse_date(watch["due_at"]))
              for watch in pending]
    record["next_run"] = min(checks).isoformat() if checks and record.get("enabled") else None
    record["pending_count"] = len(pending)
    return record


def matches(watch, lesson):
    start = parse_date(lesson.get("date"))
    return bool(start and parse_date(watch["start"]) - timedelta(minutes=15)
                <= start < parse_date(watch["end"]))
