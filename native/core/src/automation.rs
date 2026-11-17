//! Pure timetable-based replay planning. The host supplies the current time.
use std::collections::BTreeMap;

use chrono::{DateTime, Duration, FixedOffset, NaiveDate, NaiveDateTime, NaiveTime, TimeZone};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum AutomationError {
    #[error("课程自动化数据格式异常")]
    InvalidRule,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct TimetableRow {
    pub course_id: String,
    pub date: String,
    pub time: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Watch {
    pub date: String,
    pub time: String,
    pub start: String,
    pub end: String,
    pub due_at: String,
    pub status: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next_check: Option<String>,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Rule {
    pub course_id: String,
    pub enabled: bool,
    #[serde(default)]
    pub watches: BTreeMap<String, Watch>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tracking_from: Option<String>,
    #[serde(default)]
    pub schedule_policy: String,
    #[serde(default)]
    pub interval_minutes: u32,
    #[serde(default)]
    pub pending_count: usize,
    #[serde(default)]
    pub next_run: Option<String>,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

fn shanghai() -> FixedOffset {
    FixedOffset::east_opt(8 * 3600).expect("valid fixed offset")
}
fn parse_datetime(value: &str) -> Option<DateTime<FixedOffset>> {
    DateTime::parse_from_rfc3339(value)
        .ok()
        .map(|dt| dt.with_timezone(&shanghai()))
        .or_else(|| {
            NaiveDateTime::parse_from_str(value, "%Y-%m-%dT%H:%M:%S%.f")
                .ok()
                .and_then(|v| shanghai().from_local_datetime(&v).single())
        })
}
fn local_time(date: NaiveDate, time: NaiveTime) -> Option<DateTime<FixedOffset>> {
    shanghai()
        .from_local_datetime(&date.and_time(time))
        .single()
}
fn clock(text: &str) -> Option<NaiveTime> {
    NaiveTime::parse_from_str(text.trim(), "%H:%M")
        .or_else(|_| NaiveTime::parse_from_str(text.trim(), "%H:%M:%S"))
        .ok()
}
fn key(course: &str, date: NaiveDate, time: &str) -> String {
    let hash = Sha256::digest(format!("{course}|{date}|{time}").as_bytes());
    hash[..12]
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

/// Preserve existing watches and metadata; never initialize historical watches before yesterday.
pub fn plan(
    rule: &Rule,
    rows: &[TimetableRow],
    current: DateTime<FixedOffset>,
) -> Result<Rule, AutomationError> {
    let now = current.with_timezone(&shanghai());
    let mut result = rule.clone();
    let tracking = match result.tracking_from.as_deref() {
        Some(value) => NaiveDate::parse_from_str(value, "%Y-%m-%d")
            .map_err(|_| AutomationError::InvalidRule)?,
        None => now
            .date_naive()
            .checked_sub_signed(Duration::days(1))
            .ok_or(AutomationError::InvalidRule)?,
    };
    result.tracking_from = Some(tracking.to_string());
    result.schedule_policy = "next_day".into();
    result.interval_minutes = 120;
    for row in rows {
        if row.course_id != result.course_id {
            continue;
        }
        let Ok(date) = NaiveDate::parse_from_str(&row.date, "%Y-%m-%d") else {
            continue;
        };
        if date < tracking {
            continue;
        }
        let normalized = row.time.replace(['—', '–'], "-");
        let Some((start, end)) = normalized.split_once('-') else {
            continue;
        };
        let (Some(begin), Some(end)) = (clock(start), clock(end)) else {
            continue;
        };
        let (Some(begin), Some(end)) = (local_time(date, begin), local_time(date, end)) else {
            continue;
        };
        if end <= begin {
            continue;
        }
        let Some(next_date) = date.succ_opt() else {
            continue;
        };
        let Some(due) = local_time(next_date, NaiveTime::MIN) else {
            continue;
        };
        result
            .watches
            .entry(key(&row.course_id, date, &row.time))
            .or_insert_with(|| Watch {
                date: date.to_string(),
                time: row.time.clone(),
                start: begin.to_rfc3339(),
                end: end.to_rfc3339(),
                due_at: due.to_rfc3339(),
                status: "waiting".into(),
                next_check: None,
                extra: BTreeMap::new(),
            });
    }
    let mut next: Option<DateTime<FixedOffset>> = None;
    let mut count = 0;
    for watch in result.watches.values().filter(|v| v.status == "waiting") {
        count += 1;
        let due = parse_datetime(&watch.due_at).ok_or(AutomationError::InvalidRule)?;
        let check = match watch.next_check.as_deref() {
            Some(v) => parse_datetime(v).ok_or(AutomationError::InvalidRule)?,
            None => due,
        };
        let candidate = due.max(check);
        next = Some(next.map_or(candidate, |value| value.min(candidate)));
    }
    result.pending_count = count;
    result.next_run = if result.enabled {
        next.map(|dt| dt.to_rfc3339())
    } else {
        None
    };
    Ok(result)
}

pub fn matches(watch: &Watch, lesson_start: &str) -> bool {
    match (
        parse_datetime(&watch.start),
        parse_datetime(&watch.end),
        parse_datetime(lesson_start),
    ) {
        (Some(start), Some(end), Some(lesson)) => start
            .checked_sub_signed(Duration::minutes(15))
            .is_some_and(|minimum| minimum <= lesson && lesson < end),
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn at(value: &str) -> DateTime<FixedOffset> {
        DateTime::parse_from_rfc3339(value).unwrap()
    }
    fn rule() -> Rule {
        Rule {
            course_id: "course".into(),
            enabled: true,
            watches: BTreeMap::new(),
            tracking_from: None,
            schedule_policy: String::new(),
            interval_minutes: 0,
            pending_count: 0,
            next_run: None,
            extra: BTreeMap::new(),
        }
    }
    fn row(date: &str, course: &str) -> TimetableRow {
        TimetableRow {
            course_id: course.into(),
            date: date.into(),
            time: "08:00-09:35".into(),
        }
    }
    #[test]
    fn watches_start_yesterday_and_are_due_after_class_day() {
        let result = plan(
            &rule(),
            &[
                row("2026-10-19", "course"),
                row("2026-10-21", "course"),
                row("2026-10-23", "other"),
            ],
            at("2026-10-22T10:00:00+08:00"),
        )
        .unwrap();
        assert_eq!(result.pending_count, 1);
        assert!(result.watches.contains_key("4205e204a70250b8f90efd06"));
        assert_eq!(result.tracking_from.as_deref(), Some("2026-10-21"));
        assert_eq!(
            result.next_run.as_deref(),
            Some("2026-10-22T00:00:00+08:00")
        );
        let watch = result.watches.values().next().unwrap();
        assert!(matches(watch, "2026-10-21T07:45:00+08:00"));
        assert!(!matches(watch, "2026-10-21T09:35:00+08:00"));
    }
    #[test]
    fn retries_preserve_metadata_and_disabled_rules_do_not_run() {
        let mut result = plan(
            &rule(),
            &[row("2026-10-21", "course")],
            at("2026-10-22T10:00:00+08:00"),
        )
        .unwrap();
        result.watches.values_mut().next().unwrap().next_check =
            Some("2026-10-22T12:00:00+08:00".into());
        result
            .extra
            .insert("known".into(), serde_json::json!(["x"]));
        let next = plan(&result, &[], at("2026-10-22T10:00:00+08:00")).unwrap();
        assert_eq!(next.next_run.as_deref(), Some("2026-10-22T12:00:00+08:00"));
        assert_eq!(next.extra["known"], serde_json::json!(["x"]));
        result.enabled = false;
        assert_eq!(
            plan(&result, &[], at("2026-10-22T10:00:00+08:00"))
                .unwrap()
                .next_run,
            None
        );
    }
}
