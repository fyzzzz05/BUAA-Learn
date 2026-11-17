//! Replay checking orchestration. Hosts provide account-scoped persistence and IO.
use std::collections::BTreeSet;

use chrono::{DateTime, Duration, FixedOffset};
use serde_json::json;
use thiserror::Error;

use crate::{
    automation::{self, Rule, TimetableRow},
    Lesson,
};

#[derive(Debug, Error, PartialEq, Eq)]
pub enum RunError {
    #[error("课程自动化数据格式异常")]
    InvalidRule,
    #[error("课程自动化访问失败，请重试")]
    Service,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MaterialAvailability {
    pub text_segments: usize,
    pub slides: usize,
}

/// Implementations must scope all operations to the same authenticated account.
pub trait AutomationServices {
    fn load_rule(&mut self, course_id: &str) -> Result<Option<Rule>, RunError>;
    fn save_rule(&mut self, rule: &Rule) -> Result<(), RunError>;
    fn sync_lessons(&mut self, course_id: &str) -> Result<Vec<Lesson>, RunError>;
    fn fetch_materials(&mut self, lesson_id: &str) -> Result<MaterialAvailability, RunError>;
    fn analyze_lesson(&mut self, lesson_id: &str) -> Result<(), RunError>;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RunOutcome {
    pub processed: usize,
    pub pending_count: usize,
}

fn known(rule: &Rule) -> Result<BTreeSet<String>, RunError> {
    match rule.extra.get("known") {
        None => Ok(BTreeSet::new()),
        Some(value) => serde_json::from_value(value.clone()).map_err(|_| RunError::InvalidRule),
    }
}
fn plan_and_save<S: AutomationServices>(
    services: &mut S,
    rule: &Rule,
    rows: &[TimetableRow],
    now: DateTime<FixedOffset>,
) -> Result<Rule, RunError> {
    let rule = automation::plan(rule, rows, now).map_err(|_| RunError::InvalidRule)?;
    services.save_rule(&rule)?;
    Ok(rule)
}
fn due_time(value: &str) -> Result<DateTime<FixedOffset>, RunError> {
    DateTime::parse_from_rfc3339(value).map_err(|_| RunError::InvalidRule)
}

/// Persist the next retry before network IO; only mark a lesson known after all
/// required material and optional analysis operations have succeeded.
pub fn run<S: AutomationServices>(
    services: &mut S,
    course_id: &str,
    rows: &[TimetableRow],
    now: DateTime<FixedOffset>,
) -> Result<RunOutcome, RunError> {
    let Some(initial) = services.load_rule(course_id)? else {
        return Ok(RunOutcome {
            processed: 0,
            pending_count: 0,
        });
    };
    if !initial.enabled {
        return Ok(RunOutcome {
            processed: 0,
            pending_count: initial.pending_count,
        });
    }
    let planned = plan_and_save(services, &initial, rows, now)?;
    let mut due = Vec::new();
    for (key, watch) in &planned.watches {
        if watch.status == "waiting"
            && due_time(&watch.due_at)? <= now
            && due_time(watch.next_check.as_deref().unwrap_or(&watch.due_at))? <= now
        {
            due.push((key.clone(), watch.clone()));
        }
    }
    if due.is_empty() {
        return Ok(RunOutcome {
            processed: 0,
            pending_count: planned.pending_count,
        });
    }
    let mut fresh = services
        .load_rule(course_id)?
        .ok_or(RunError::InvalidRule)?;
    if !fresh.enabled {
        return Ok(RunOutcome {
            processed: 0,
            pending_count: fresh.pending_count,
        });
    }
    let retry = now
        .checked_add_signed(Duration::hours(2))
        .ok_or(RunError::InvalidRule)?
        .to_rfc3339();
    for (key, _) in &due {
        if let Some(watch) = fresh.watches.get_mut(key) {
            watch.next_check = Some(retry.clone());
        }
    }
    fresh
        .extra
        .insert("last_run".into(), json!(now.to_rfc3339()));
    plan_and_save(services, &fresh, rows, now)?;
    let lessons = services.sync_lessons(course_id)?;
    let mut processed = 0;
    for (key, watch) in due {
        let candidates: Vec<&Lesson> = lessons
            .iter()
            .filter(|lesson| {
                DateTime::from_timestamp(lesson.start_at, 0)
                    .is_some_and(|start| automation::matches(&watch, &start.to_rfc3339()))
            })
            .collect();
        for lesson in &candidates {
            if !lesson.has_recording {
                continue;
            }
            let Some(rule) = services.load_rule(course_id)? else {
                return Ok(RunOutcome {
                    processed,
                    pending_count: 0,
                });
            };
            if !rule.enabled {
                return Ok(RunOutcome {
                    processed,
                    pending_count: rule.pending_count,
                });
            }
            if known(&rule)?.contains(&lesson.id) {
                continue;
            }
            let material = services.fetch_materials(&lesson.id)?;
            if material.text_segments == 0 && material.slides == 0 {
                continue;
            }
            let Some(rule) = services.load_rule(course_id)? else {
                return Ok(RunOutcome {
                    processed,
                    pending_count: 0,
                });
            };
            if !rule.enabled {
                return Ok(RunOutcome {
                    processed,
                    pending_count: rule.pending_count,
                });
            }
            if rule
                .extra
                .get("auto_analyze")
                .and_then(|v| v.as_bool())
                .unwrap_or(false)
            {
                services.analyze_lesson(&lesson.id)?;
            }
            let Some(mut rule) = services.load_rule(course_id)? else {
                return Ok(RunOutcome {
                    processed,
                    pending_count: 0,
                });
            };
            if !rule.enabled {
                return Ok(RunOutcome {
                    processed,
                    pending_count: rule.pending_count,
                });
            }
            let mut ids = known(&rule)?;
            ids.insert(lesson.id.clone());
            rule.extra.insert("known".into(), json!(ids));
            plan_and_save(services, &rule, rows, now)?;
            processed += 1;
        }
        let Some(mut rule) = services.load_rule(course_id)? else {
            return Ok(RunOutcome {
                processed,
                pending_count: 0,
            });
        };
        if !rule.enabled {
            return Ok(RunOutcome {
                processed,
                pending_count: rule.pending_count,
            });
        }
        let ids = known(&rule)?;
        if !candidates.is_empty()
            && candidates
                .iter()
                .all(|lesson| lesson.has_recording && ids.contains(&lesson.id))
        {
            if let Some(watch) = rule.watches.get_mut(&key) {
                watch.status = "completed".into();
                watch
                    .extra
                    .insert("completed_at".into(), json!(now.to_rfc3339()));
            }
            plan_and_save(services, &rule, rows, now)?;
        }
    }
    let pending_count = services
        .load_rule(course_id)?
        .map_or(0, |rule| rule.pending_count);
    Ok(RunOutcome {
        processed,
        pending_count,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;
    struct Fake {
        rule: Rule,
        lessons: Vec<Lesson>,
        material: MaterialAvailability,
        fail_analysis: bool,
        pause_on_fetch: bool,
        saw_retry: bool,
    }
    impl AutomationServices for Fake {
        fn load_rule(&mut self, _: &str) -> Result<Option<Rule>, RunError> {
            Ok(Some(self.rule.clone()))
        }
        fn save_rule(&mut self, rule: &Rule) -> Result<(), RunError> {
            self.rule = rule.clone();
            Ok(())
        }
        fn sync_lessons(&mut self, _: &str) -> Result<Vec<Lesson>, RunError> {
            self.saw_retry = self
                .rule
                .watches
                .values()
                .all(|watch| watch.next_check.is_some());
            Ok(self.lessons.clone())
        }
        fn fetch_materials(&mut self, _: &str) -> Result<MaterialAvailability, RunError> {
            if self.pause_on_fetch {
                self.rule.enabled = false;
            }
            Ok(self.material)
        }
        fn analyze_lesson(&mut self, _: &str) -> Result<(), RunError> {
            if self.fail_analysis {
                Err(RunError::Service)
            } else {
                Ok(())
            }
        }
    }
    fn fixture() -> (Fake, Vec<TimetableRow>, DateTime<FixedOffset>) {
        let now = DateTime::parse_from_rfc3339("2026-10-22T10:00:00+08:00").unwrap();
        let start = DateTime::parse_from_rfc3339("2026-10-21T08:00:00+08:00").unwrap();
        let rule = Rule {
            course_id: "c".into(),
            enabled: true,
            watches: BTreeMap::new(),
            tracking_from: None,
            schedule_policy: String::new(),
            interval_minutes: 120,
            pending_count: 0,
            next_run: None,
            extra: BTreeMap::new(),
        };
        let fake = Fake {
            rule,
            lessons: vec![Lesson {
                id: "l".into(),
                title: "课次".into(),
                teachers: String::new(),
                recording_course_id: String::new(),
                start_at: start.timestamp(),
                has_recording: true,
                duration_seconds: 0,
            }],
            material: MaterialAvailability {
                text_segments: 1,
                slides: 0,
            },
            fail_analysis: false,
            pause_on_fetch: false,
            saw_retry: false,
        };
        (
            fake,
            vec![TimetableRow {
                course_id: "c".into(),
                date: "2026-10-21".into(),
                time: "08:00-09:35".into(),
            }],
            now,
        )
    }
    #[test]
    fn ready_material_is_processed_after_retry_is_persisted() {
        let (mut fake, rows, now) = fixture();
        let outcome = run(&mut fake, "c", &rows, now).unwrap();
        assert!(fake.saw_retry);
        assert_eq!(
            outcome,
            RunOutcome {
                processed: 1,
                pending_count: 0
            }
        );
        assert!(known(&fake.rule).unwrap().contains("l"));
    }
    #[test]
    fn failed_analysis_is_retried_and_never_marked_known() {
        let (mut fake, rows, now) = fixture();
        fake.rule.extra.insert("auto_analyze".into(), json!(true));
        fake.fail_analysis = true;
        assert_eq!(run(&mut fake, "c", &rows, now), Err(RunError::Service));
        assert!(fake.saw_retry);
        assert!(known(&fake.rule).unwrap().is_empty());
        assert_eq!(fake.rule.pending_count, 1);
        assert_eq!(
            run(&mut fake, "c", &rows, now + Duration::minutes(1))
                .unwrap()
                .processed,
            0
        );
        assert_eq!(
            run(&mut fake, "c", &rows, now + Duration::hours(2)),
            Err(RunError::Service)
        );
    }
    #[test]
    fn pause_during_fetch_is_preserved() {
        let (mut fake, rows, now) = fixture();
        fake.pause_on_fetch = true;
        let outcome = run(&mut fake, "c", &rows, now).unwrap();
        assert!(!fake.rule.enabled);
        assert_eq!(outcome.processed, 0);
        assert!(known(&fake.rule).unwrap().is_empty());
    }
    #[test]
    fn empty_materials_are_not_marked_as_complete() {
        let (mut fake, rows, now) = fixture();
        fake.material = MaterialAvailability {
            text_segments: 0,
            slides: 0,
        };
        let outcome = run(&mut fake, "c", &rows, now).unwrap();
        assert_eq!(
            outcome,
            RunOutcome {
                processed: 0,
                pending_count: 1
            }
        );
        assert!(known(&fake.rule).unwrap().is_empty());
    }
}
