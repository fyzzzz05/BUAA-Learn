//! Shared, platform-independent school data contracts.
//! Credentials and platform web sessions stay in platform-owned memory.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use thiserror::Error;

pub mod analysis_pipeline;
pub mod automation;
pub mod automation_execution;
pub mod extraction;
pub mod extraction_ffi;
pub mod ffi;
pub mod learning;
pub mod legacy_windows;
pub mod materials;
pub mod network;
pub mod school_ffi;
pub mod slide_image;
pub mod slide_pdf;
pub mod store;
pub mod timeline;
pub mod topic_resolution;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum PortalDataError {
    #[error("学校接口返回的数据格式异常")]
    InvalidResponse,
    #[error("学校接口返回错误")]
    PortalRejected,
    #[error("学校返回的学生课程缺少必要信息")]
    InvalidCourse,
    #[error("请先选择学期")]
    MissingSemester,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Course {
    pub id: String,
    pub name: String,
    pub code: String,
    pub teachers: String,
    pub semester: String,
    pub task_id: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Lesson {
    pub id: String,
    pub title: String,
    #[serde(default)]
    pub teachers: String,
    #[serde(default)]
    pub recording_course_id: String,
    pub start_at: i64,
    pub has_recording: bool,
    pub duration_seconds: u64,
}

/// Independent status for the two school systems; recording authorization is not
/// inferred from the presence of a teaching-portal session.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum PortalStatus {
    Offline,
    Connecting,
    Connected,
    Failed,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum RecordingStatus {
    NotStarted,
    Connecting,
    Authorized,
    Failed,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ConnectionStatus {
    pub portal: PortalStatus,
    pub recording: RecordingStatus,
}

impl Default for ConnectionStatus {
    fn default() -> Self {
        Self {
            portal: PortalStatus::Offline,
            recording: RecordingStatus::NotStarted,
        }
    }
}

impl ConnectionStatus {
    pub fn start_login(&mut self) {
        self.portal = PortalStatus::Connecting;
        self.recording = RecordingStatus::NotStarted;
    }
    pub fn portal_connected(&mut self) -> bool {
        if self.portal != PortalStatus::Connecting {
            return false;
        }
        self.portal = PortalStatus::Connected;
        true
    }
    pub fn portal_failed(&mut self) {
        self.portal = PortalStatus::Failed;
        self.recording = RecordingStatus::NotStarted;
    }
    pub fn start_recording(&mut self) -> bool {
        if self.portal != PortalStatus::Connected {
            return false;
        }
        self.recording = RecordingStatus::Connecting;
        true
    }
    /// Only call after an authenticated catalogue response succeeds.
    pub fn recording_verified(&mut self) -> bool {
        if self.portal != PortalStatus::Connected || self.recording != RecordingStatus::Connecting {
            return false;
        }
        self.recording = RecordingStatus::Authorized;
        true
    }
    pub fn recording_failed(&mut self) {
        self.recording = if self.portal == PortalStatus::Connected {
            RecordingStatus::Failed
        } else {
            RecordingStatus::NotStarted
        };
    }
    pub fn logout(&mut self) {
        *self = Self::default();
    }
}

fn scalar(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::String(s) => Some(s.clone()),
        Value::Number(n) => Some(n.to_string()),
        Value::Null => None,
        _ => None,
    }
}

fn content(body: &str) -> Result<Vec<Value>, PortalDataError> {
    let value: Value = serde_json::from_str(body).map_err(|_| PortalDataError::InvalidResponse)?;
    let object = value.as_object().ok_or(PortalDataError::InvalidResponse)?;
    if object.get("code").and_then(Value::as_i64) != Some(200) {
        return Err(PortalDataError::PortalRejected);
    }
    object
        .get("content")
        .and_then(Value::as_array)
        .cloned()
        .ok_or(PortalDataError::InvalidResponse)
}

/// Return the school's semesters without assuming their ordering denotes the current term.
pub fn parse_semesters(body: &str) -> Result<Vec<String>, PortalDataError> {
    content(body)?
        .iter()
        .map(|item| {
            scalar(item.get("xnxq"))
                .filter(|id| !id.trim().is_empty())
                .ok_or(PortalDataError::InvalidResponse)
        })
        .collect()
}

/// Parse student courses for an explicitly selected semester; malformed results are not treated as empty.
pub fn parse_courses(body: &str, semester: &str) -> Result<Vec<Course>, PortalDataError> {
    if semester.trim().is_empty() {
        return Err(PortalDataError::MissingSemester);
    }
    let mut result = Vec::new();
    for row in content(body)? {
        let role = scalar(row.get("juese")).ok_or(PortalDataError::InvalidCourse)?;
        if role != "3" {
            continue;
        }
        let id = scalar(row.get("kcid"))
            .filter(|v| !v.trim().is_empty())
            .ok_or(PortalDataError::InvalidCourse)?;
        let name = scalar(row.get("kcmc"))
            .filter(|v| !v.trim().is_empty())
            .ok_or(PortalDataError::InvalidCourse)?;
        result.push(Course {
            id,
            name,
            semester: semester.to_string(),
            code: scalar(row.get("jw_kcdm")).unwrap_or_default(),
            teachers: scalar(row.get("skjs")).unwrap_or_default(),
            task_id: scalar(row.get("rwh")).unwrap_or_default(),
        });
    }
    Ok(result)
}

/// Decode the recording catalogue without interpreting a failed response as no lessons.
pub fn parse_catalogue(body: &str) -> Result<Vec<Lesson>, PortalDataError> {
    let root: Value = serde_json::from_str(body).map_err(|_| PortalDataError::InvalidResponse)?;
    if let Some(code) = root.get("code").and_then(Value::as_i64) {
        if code != 0 && code != 200 {
            return Err(PortalDataError::PortalRejected);
        }
    }
    let rows = root
        .get("result")
        .and_then(|v| v.get("data"))
        .and_then(Value::as_array)
        .ok_or(PortalDataError::InvalidResponse)?;
    rows.iter()
        .map(|row| {
            let id = scalar(row.get("sub_id"))
                .filter(|s| !s.is_empty())
                .ok_or(PortalDataError::InvalidResponse)?;
            let title = scalar(row.get("title")).unwrap_or_else(|| "未命名课次".into());
            let start_at = match row.get("start_at") {
                Some(Value::Number(n)) => n.as_i64().ok_or(PortalDataError::InvalidResponse)?,
                Some(Value::String(s)) => {
                    s.parse().map_err(|_| PortalDataError::InvalidResponse)?
                }
                None | Some(Value::Null) => 0,
                _ => return Err(PortalDataError::InvalidResponse),
            };
            let content: Value = match row.get("content") {
                None | Some(Value::Null) => serde_json::json!({}),
                Some(Value::String(s)) if s.is_empty() => serde_json::json!({}),
                Some(Value::String(s)) => {
                    serde_json::from_str(s).map_err(|_| PortalDataError::InvalidResponse)?
                }
                _ => return Err(PortalDataError::InvalidResponse),
            };
            if !content.is_object() {
                return Err(PortalDataError::InvalidResponse);
            }
            let playback = content.get("save_playback");
            let has_contents = playback
                .and_then(|v| v.get("contents"))
                .is_some_and(|v| match v {
                    Value::Null | Value::Bool(false) => false,
                    Value::String(s) => !s.is_empty(),
                    Value::Array(a) => !a.is_empty(),
                    Value::Object(o) => !o.is_empty(),
                    Value::Number(n) => n.as_f64().is_some_and(|v| v != 0.0),
                    _ => true,
                });
            let has_url = content
                .get("playback")
                .and_then(|v| v.get("url"))
                .and_then(Value::as_str)
                .is_some_and(|s| !s.is_empty());
            let duration = playback.and_then(|v| v.get("contents_duration"));
            let duration_seconds = match duration {
                None | Some(Value::Null) => 0,
                Some(Value::Number(n)) => n.as_u64().ok_or(PortalDataError::InvalidResponse)?,
                Some(Value::String(s)) if s.is_empty() => 0,
                Some(Value::String(s)) => {
                    s.parse().map_err(|_| PortalDataError::InvalidResponse)?
                }
                _ => return Err(PortalDataError::InvalidResponse),
            };
            Ok(Lesson {
                id,
                title,
                teachers: scalar(row.get("lecturer_name")).unwrap_or_default(),
                recording_course_id: scalar(row.get("course_id")).unwrap_or_default(),
                start_at,
                has_recording: has_contents || has_url,
                duration_seconds,
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_school_term_order_without_guessing_current_term() {
        let result =
            parse_semesters(r#"{"code":200,"content":[{"xnxq":"older"},{"xnxq":"current"}]}"#)
                .unwrap();
        assert_eq!(result, vec!["older", "current"]);
    }
    #[test]
    fn rejects_failed_or_malformed_school_responses() {
        assert_eq!(
            parse_semesters(r#"{"code":403,"content":[]}"#),
            Err(PortalDataError::PortalRejected)
        );
        assert_eq!(
            parse_semesters(r#"{"code":200,"content":{}}"#),
            Err(PortalDataError::InvalidResponse)
        );
        assert_eq!(
            parse_semesters("not json"),
            Err(PortalDataError::InvalidResponse)
        );
    }
    #[test]
    fn parses_only_student_courses_and_preserves_semester() {
        let rows = r#"{"code":200,"content":[{"juese":3,"kcid":42,"kcmc":"课程"},{"juese":"1","kcid":"2","kcmc":"教师"}]}"#;
        let result = parse_courses(rows, "term").unwrap();
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].id, "42");
        assert_eq!(result[0].semester, "term");
    }
    #[test]
    fn never_turns_missing_student_data_into_empty_course_list() {
        let rows = r#"{"code":200,"content":[{"juese":"3","kcmc":"课程"}]}"#;
        assert_eq!(
            parse_courses(rows, "term"),
            Err(PortalDataError::InvalidCourse)
        );
        assert_eq!(
            parse_courses(rows, ""),
            Err(PortalDataError::MissingSemester)
        );
    }
    #[test]
    fn recording_authentication_is_independent_and_requires_verified_catalogue() {
        let mut state = ConnectionStatus::default();
        assert!(!state.start_recording());
        assert!(!state.recording_verified());
        state.start_login();
        assert!(state.portal_connected());
        assert_eq!(state.recording, RecordingStatus::NotStarted);
        assert!(state.start_recording());
        state.recording_failed();
        assert!(!state.recording_verified());
        assert!(state.start_recording());
        assert!(state.recording_verified());
        state.logout();
        assert_eq!(state, ConnectionStatus::default());
    }
    #[test]
    fn catalogue_distinguishes_recordings_and_future_lessons() {
        let body = r#"{"code":0,"result":{"data":[{"sub_id":4,"title":"第一课","start_at":123,"content":"{\"save_playback\":{\"contents\":[1],\"contents_duration\":60}}"},{"sub_id":"5","content":"{}"}]}}"#;
        let lessons = parse_catalogue(body).unwrap();
        assert_eq!(lessons.len(), 2);
        assert!(lessons[0].has_recording);
        assert_eq!(lessons[0].duration_seconds, 60);
        assert!(!lessons[1].has_recording);
    }
    #[test]
    fn catalogue_does_not_silently_accept_failures_or_corrupt_lesson_content() {
        assert_eq!(
            parse_catalogue(r#"{"code":5002,"result":{"data":[]}}"#),
            Err(PortalDataError::PortalRejected)
        );
        assert_eq!(
            parse_catalogue(r#"{"code":0,"result":{"data":[{"sub_id":1,"content":"bad"}]}}"#),
            Err(PortalDataError::InvalidResponse)
        );
        assert_eq!(
            parse_catalogue(r#"{"code":0}"#),
            Err(PortalDataError::InvalidResponse)
        );
    }
}
