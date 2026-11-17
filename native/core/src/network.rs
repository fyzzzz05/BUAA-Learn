//! Platform-independent school request policy. The host supplies the HTTPS transport.
//! Requests must never follow redirects: a transport must reject all 3xx responses.
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use thiserror::Error;

use crate::{
    materials::{self, Segment, Slide},
    Course, Lesson,
};

#[derive(Debug, Error, PartialEq, Eq)]
pub enum NetworkError {
    #[error("学校网络暂时无法连接，请稍后重试")]
    Transport,
    #[error("学校登录已过期，请重新登录")]
    LoginRequired,
    #[error("录播授权未完成或已过期，请重新登录")]
    RecordingAuth,
    #[error("学校接口返回异常")]
    InvalidResponse,
    #[error("课堂材料尚不可用")]
    MissingMaterials,
}

/// Contains sensitive session headers: never format, persist, or log this type.
pub struct Request {
    pub url: String,
    pub method: Method,
    pub headers: Vec<(String, String)>,
    pub body: Option<Value>,
}
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Method {
    Get,
    Post,
}
pub struct Response {
    pub status: u16,
    pub body: String,
}

/// Transports must use TLS verification, reject redirects, and never log headers,
/// bodies or signed URLs. Host methods are synchronous; invoke off the UI thread.
pub trait Transport {
    fn send(&self, request: &Request) -> Result<Response, NetworkError>;
}

fn encoded(value: &str) -> String {
    let mut output = String::new();
    for byte in value.as_bytes() {
        if byte.is_ascii_alphanumeric() || matches!(*byte, b'-' | b'.' | b'_' | b'~') {
            output.push(*byte as char);
        } else {
            output.push_str(&format!("%{byte:02X}"));
        }
    }
    output
}
fn endpoint(host: &str, path: &str, params: &[(&str, String)]) -> String {
    let mut result = format!("https://{host}{path}");
    if !params.is_empty() {
        result.push('?');
        result.push_str(
            &params
                .iter()
                .map(|(key, value)| format!("{}={}", encoded(key), encoded(value)))
                .collect::<Vec<_>>()
                .join("&"),
        );
    }
    result
}
fn json_response(response: Response) -> Result<Value, NetworkError> {
    if response.status != 200 {
        return Err(NetworkError::Transport);
    }
    serde_json::from_str(&response.body).map_err(|_| NetworkError::InvalidResponse)
}

/// Credentials are held in memory only. Do not derive Debug, Clone or Serialize.
pub struct SchoolSession<T: Transport> {
    transport: T,
    portal_token: String,
    refresh_token: String,
    role: String,
    recording_token: Option<String>,
}

impl<T: Transport> SchoolSession<T> {
    /// Sensitive: hosts may save this only in the OS credential store, never logs or SQLite.
    pub fn credentials(&self) -> Value {
        json!({"token":self.portal_token,"refresh":self.refresh_token,"role":self.role,"recording":self.recording_token})
    }
    pub fn new(
        transport: T,
        portal_token: String,
        refresh_token: String,
        role: String,
    ) -> Result<Self, NetworkError> {
        if portal_token.is_empty() || role != "01" {
            return Err(NetworkError::LoginRequired);
        }
        Ok(Self {
            transport,
            portal_token,
            refresh_token,
            role,
            recording_token: None,
        })
    }
    pub fn set_recording_token(&mut self, token: String) -> Result<(), NetworkError> {
        if token.trim().is_empty() {
            return Err(NetworkError::RecordingAuth);
        }
        self.recording_token = Some(token);
        Ok(())
    }
    fn portal_headers(&self) -> Vec<(String, String)> {
        vec![
            ("Token".into(), format!("Inco-{}", self.portal_token)),
            ("RoleCode".into(), self.role.clone()),
            ("X-Requested-With".into(), "XMLHttpRequest".into()),
            ("Referer".into(), "https://spoc.buaa.edu.cn/spocnew/".into()),
        ]
    }
    fn refresh(&mut self) -> Result<(), NetworkError> {
        if self.refresh_token.is_empty() {
            return Err(NetworkError::LoginRequired);
        }
        let request = Request {
            url: endpoint("spoc.buaa.edu.cn", "/spocnewht/sys/refreshToken", &[]),
            method: Method::Post,
            headers: self.portal_headers(),
            body: Some(json!({"refreshToken":self.refresh_token})),
        };
        let raw = self.transport.send(&request)?;
        if [401, 403].contains(&raw.status) {
            return Err(NetworkError::LoginRequired);
        }
        let response = json_response(raw)?;
        let data = response
            .get("content")
            .and_then(Value::as_object)
            .ok_or(NetworkError::LoginRequired)?;
        let token = data
            .get("token")
            .and_then(Value::as_str)
            .filter(|token| !token.is_empty())
            .ok_or(NetworkError::LoginRequired)?;
        if response.get("code").and_then(Value::as_i64) != Some(200) {
            return Err(NetworkError::LoginRequired);
        }
        self.portal_token = token.into();
        if let Some(refresh) = data
            .get("refreshToken")
            .and_then(Value::as_str)
            .filter(|v| !v.is_empty())
        {
            self.refresh_token = refresh.into();
        }
        Ok(())
    }
    fn portal(
        &mut self,
        path: &'static str,
        params: &[(&str, String)],
        method: Method,
    ) -> Result<String, NetworkError> {
        for retry in [true, false] {
            let request = Request {
                url: endpoint("spoc.buaa.edu.cn", path, params),
                method,
                headers: self.portal_headers(),
                body: if method == Method::Post {
                    Some(json!({}))
                } else {
                    None
                },
            };
            let response = self.transport.send(&request)?;
            let status = response.status;
            if [401, 403].contains(&status) {
                if retry && !self.refresh_token.is_empty() {
                    self.refresh()?;
                    continue;
                }
                return Err(NetworkError::LoginRequired);
            }
            let body: Value =
                serde_json::from_str(&response.body).map_err(|_| NetworkError::InvalidResponse)?;
            let code = body.get("code").and_then(Value::as_i64);
            if matches!(code, Some(1 | 401 | 403)) {
                if retry && !self.refresh_token.is_empty() {
                    self.refresh()?;
                    continue;
                }
                return Err(NetworkError::LoginRequired);
            }
            if status != 200 || code != Some(200) {
                return Err(NetworkError::InvalidResponse);
            }
            return Ok(response.body);
        }
        Err(NetworkError::LoginRequired)
    }
    pub fn semesters(&mut self) -> Result<Vec<String>, NetworkError> {
        let body = self.portal("/spocnewht/component/queryXnxq", &[], Method::Post)?;
        crate::parse_semesters(&body).map_err(|_| NetworkError::InvalidResponse)
    }
    pub fn courses(&mut self, semester: &str) -> Result<Vec<Course>, NetworkError> {
        if semester.trim().is_empty() {
            return Err(NetworkError::InvalidResponse);
        }
        let body = self.portal(
            "/spocnewht/jxkj/queryKclb",
            &[("xnxq", semester.into()), ("lx", "1".into())],
            Method::Get,
        )?;
        crate::parse_courses(&body, semester).map_err(|_| NetworkError::InvalidResponse)
    }
    /// Obtain the short-lived signed entry using the same refreshable portal session.
    /// The result is sensitive and must not be logged or persisted.
    pub fn recording_signature(&mut self, course: &str, task: &str) -> Result<Value, NetworkError> {
        if course.trim().is_empty() {
            return Err(NetworkError::InvalidResponse);
        }
        let parse = |body: &str| -> Result<Value, NetworkError> {
            serde_json::from_str::<Value>(body)
                .ok()
                .and_then(|v| v.get("content").cloned())
                .ok_or(NetworkError::InvalidResponse)
        };
        let role = parse(&self.portal(
            "/spocnewht/kc/queryYhSflx",
            &[("kcid", course.into())],
            Method::Get,
        )?)?;
        let role = match role {
            Value::String(s) => s,
            Value::Number(n) => n.to_string(),
            _ => return Err(NetworkError::InvalidResponse),
        };
        let student = !["1", "3", "6"].contains(&role.as_str());
        let mut params = vec![
            ("kcid", course.to_string()),
            ("type", String::new()),
            ("isxs", if student { "1" } else { "0" }.into()),
        ];
        if !student {
            let rows = parse(&self.portal(
                "/spocnewht/lbsp/queryRwhList",
                &[("kcid", course.into())],
                Method::Get,
            )?)?;
            let rows = rows.as_array().ok_or(NetworkError::InvalidResponse)?;
            let value = |row: &Value| match row.get("RWH") {
                Some(Value::String(s)) => Some(s.clone()),
                Some(Value::Number(n)) => Some(n.to_string()),
                _ => None,
            };
            let selected = rows
                .iter()
                .filter_map(value)
                .find(|v| v == task)
                .or_else(|| rows.first().and_then(value))
                .filter(|s| !s.is_empty())
                .ok_or(NetworkError::InvalidResponse)?;
            params.push(("rwh", selected));
        }
        let signature = parse(&self.portal(
            "/spocnewht/yun/getHxyTimestampAndSignature2",
            &params,
            Method::Get,
        )?)?;
        let present = |key: &str| match signature.get(key) {
            Some(Value::String(s)) => !s.is_empty(),
            Some(Value::Number(_)) => true,
            _ => false,
        };
        if !present("bind_id") || !present("ktsl_url") {
            return Err(NetworkError::InvalidResponse);
        }
        Ok(signature)
    }
    pub fn schedule(&mut self, start: &str, end: &str) -> Result<Vec<ScheduleRow>, NetworkError> {
        let start_date = chrono::NaiveDate::parse_from_str(start, "%Y-%m-%d")
            .map_err(|_| NetworkError::InvalidResponse)?;
        let end_date = chrono::NaiveDate::parse_from_str(end, "%Y-%m-%d")
            .map_err(|_| NetworkError::InvalidResponse)?;
        if end_date < start_date {
            return Err(NetworkError::InvalidResponse);
        }
        let body = self.portal(
            "/spocnewht/jxkj/queryRlData",
            &[
                ("zksrq", start.into()),
                ("zjsrq", end.into()),
                ("rllx", "1".into()),
            ],
            Method::Get,
        )?;
        let root: Value = serde_json::from_str(&body).map_err(|_| NetworkError::InvalidResponse)?;
        let rows = root
            .get("content")
            .and_then(Value::as_array)
            .ok_or(NetworkError::InvalidResponse)?;
        let value = |row: &Value, key: &str| -> Result<String, NetworkError> {
            match row.get(key) {
                None | Some(Value::Null) => Ok(String::new()),
                Some(Value::String(text)) => Ok(text.clone()),
                Some(Value::Number(number)) => Ok(number.to_string()),
                _ => Err(NetworkError::InvalidResponse),
            }
        };
        rows.iter()
            .map(|row| {
                Ok(ScheduleRow {
                    course_id: value(row, "kcid")?,
                    name: value(row, "kcmc")?,
                    date: value(row, "rq")?,
                    time: value(row, "strkcsj")?,
                    room: value(row, "skdd")?,
                    teachers: value(row, "jsxm")?,
                    binding: value(row, "zjdm")?,
                })
            })
            .collect()
    }
    fn recording(
        &self,
        host: &'static str,
        path: &'static str,
        params: &[(&str, String)],
    ) -> Result<String, NetworkError> {
        let token = self
            .recording_token
            .as_ref()
            .ok_or(NetworkError::RecordingAuth)?;
        let request = Request {
            url: endpoint(host, path, params),
            method: Method::Get,
            headers: vec![("Authorization".into(), format!("Bearer {token}"))],
            body: None,
        };
        let response = self.transport.send(&request)?;
        if [401, 403].contains(&response.status) {
            return Err(NetworkError::RecordingAuth);
        }
        let status = response.status;
        let body: Value =
            serde_json::from_str(&response.body).map_err(|_| NetworkError::InvalidResponse)?;
        if matches!(
            body.get("code").and_then(Value::as_i64),
            Some(401 | 403 | 5002)
        ) {
            return Err(NetworkError::RecordingAuth);
        }
        if status != 200 {
            return Err(NetworkError::Transport);
        }
        Ok(response.body)
    }
    pub fn catalogue(&self, course_id: &str) -> Result<Vec<Lesson>, NetworkError> {
        let body = self.recording(
            "yjapi.msa.buaa.edu.cn",
            "/courseapi/v2/course/catalogue",
            &[("course_id", course_id.into())],
        )?;
        crate::parse_catalogue(&body).map_err(|_| NetworkError::InvalidResponse)
    }
    pub fn materials(
        &self,
        course_id: &str,
        lesson_id: &str,
    ) -> Result<MaterialData, NetworkError> {
        self.materials_with_duration(course_id, lesson_id, 0.0)
    }
    pub fn materials_with_duration(
        &self,
        course_id: &str,
        lesson_id: &str,
        lesson_duration: f64,
    ) -> Result<MaterialData, NetworkError> {
        if !lesson_duration.is_finite() || lesson_duration < 0.0 {
            return Err(NetworkError::InvalidResponse);
        }
        let params = [
            ("course_id", course_id.into()),
            ("sub_id", lesson_id.into()),
        ];
        let info: Value = serde_json::from_str(&self.recording(
            "classroom.msa.buaa.edu.cn",
            "/courseapi/v3/portal-home-setting/get-sub-info",
            &params,
        )?)
        .map_err(|_| NetworkError::InvalidResponse)?;
        if info.get("code").and_then(Value::as_i64) != Some(0) {
            return Err(NetworkError::MissingMaterials);
        }
        let info = info
            .get("data")
            .and_then(Value::as_object)
            .ok_or(NetworkError::MissingMaterials)?;
        let guid = match info.get("resource_guid") {
            Some(Value::String(s)) if !s.is_empty() => s.clone(),
            Some(Value::Number(n)) => n.to_string(),
            _ => return Err(NetworkError::MissingMaterials),
        };
        let transcript = self.recording(
            "yjapi.msa.buaa.edu.cn",
            "/courseapi/v3/web-socket/search-trans-result",
            &[
                ("sub_id", lesson_id.into()),
                ("format", "json".into()),
                ("resource_guid", guid.clone()),
            ],
        )?;
        let segments =
            materials::parse_segments(&transcript).map_err(|_| NetworkError::InvalidResponse)?;
        let mut rows = Vec::new();
        for page in 1..=100 {
            let body = self.recording(
                "classroom.msa.buaa.edu.cn",
                "/pptnote/v1/schedule/search-ppt",
                &[
                    ("course_id", course_id.into()),
                    ("sub_id", lesson_id.into()),
                    ("resource_guid", guid.clone()),
                    ("page", page.to_string()),
                    ("per_page", "100".into()),
                ],
            )?;
            let json: Value =
                serde_json::from_str(&body).map_err(|_| NetworkError::InvalidResponse)?;
            if json.get("code").and_then(Value::as_i64) != Some(0) {
                return Err(NetworkError::InvalidResponse);
            }
            let page_rows = match json.get("list") {
                None | Some(Value::Null) => Vec::new(),
                Some(Value::Array(values)) => values.clone(),
                _ => return Err(NetworkError::InvalidResponse),
            };
            let done = page_rows.is_empty();
            rows.extend(page_rows);
            let total = json
                .get("total")
                .and_then(Value::as_u64)
                .map(|v| v as usize)
                .unwrap_or(rows.len());
            if done || rows.len() >= total {
                break;
            }
            if page == 100 {
                return Err(NetworkError::InvalidResponse);
            }
        }
        let slides = materials::parse_slides(&rows).map_err(|_| NetworkError::InvalidResponse)?;
        let duration = info
            .get("duration")
            .and_then(|value| match value {
                Value::Number(value) => value.as_f64(),
                Value::String(value) => value.parse().ok(),
                _ => None,
            })
            .filter(|value| value.is_finite() && *value >= 0.0)
            .filter(|duration| *duration > 0.0)
            .unwrap_or(lesson_duration);
        let video_rows: Vec<&Value> = match info.get("video_list") {
            Some(Value::Array(rows)) => rows.iter().collect(),
            Some(Value::Object(rows)) => rows.values().collect(),
            _ => vec![],
        };
        let videos = video_rows
            .into_iter()
            .filter_map(|row| {
                let url = row
                    .get("preview_url")
                    .and_then(Value::as_str)
                    .and_then(materials::clean_school_url)?;
                let duration = row
                    .get("duration")
                    .and_then(|v| v.as_f64().or_else(|| v.as_str()?.parse().ok()))
                    .filter(|value| value.is_finite() && *value >= 0.0)
                    .unwrap_or(0.0);
                let kind = if row.get("type").map(|v| v == "2" || v == 2).unwrap_or(false) {
                    "PPT"
                } else {
                    "教师"
                };
                Some(Video {
                    kind: kind.into(),
                    url,
                    duration,
                })
            })
            .collect();
        let coverage =
            materials::coverage(&segments, duration).map_err(|_| NetworkError::InvalidResponse)?;
        use sha2::{Digest, Sha256};
        let fingerprint =
            serde_json::to_vec(&serde_json::json!({"segments":segments,"slides":slides}))
                .map_err(|_| NetworkError::InvalidResponse)?;
        let digest = format!("{:x}", Sha256::digest(fingerprint));
        Ok(MaterialData {
            lesson_id: lesson_id.into(),
            segments,
            slides,
            videos,
            duration,
            coverage,
            digest,
            fetched_at: chrono::DateTime::<chrono::Utc>::from(std::time::SystemTime::now())
                .to_rfc3339(),
            limitations: vec![
                "自动识别存在错词与漏字。".into(),
                "当前未对 PPT 图像做 OCR，也未自动核听原声。".into(),
            ],
        })
    }
}

#[derive(Serialize)]
pub struct MaterialData {
    pub lesson_id: String,
    pub segments: Vec<Segment>,
    pub slides: Vec<Slide>,
    pub videos: Vec<Video>,
    pub duration: f64,
    pub coverage: materials::Coverage,
    pub digest: String,
    pub fetched_at: String,
    pub limitations: Vec<String>,
}

#[derive(Serialize)]
pub struct Video {
    #[serde(rename = "type")]
    pub kind: String,
    pub url: String,
    pub duration: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct ScheduleRow {
    pub course_id: String,
    pub name: String,
    pub date: String,
    pub time: String,
    pub room: String,
    pub teachers: String,
    pub binding: String,
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    struct Stub {
        requests: RefCell<Vec<(String, bool, bool)>>,
        responses: RefCell<Vec<Response>>,
    }
    #[test]
    fn recording_signature_uses_refreshable_portal_and_selects_matching_class() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![
                response(r#"{"code":200,"content":"1"}"#),
                response(r#"{"code":200,"content":[{"RWH":"first"},{"RWH":"chosen"}]}"#),
                response(
                    r#"{"code":200,"content":{"bind_id":"id","ktsl_url":"https://classroom.msa.buaa.edu.cn/entry"}}"#,
                ),
            ]),
        };
        let mut session = SchoolSession::new(stub, "token".into(), "".into(), "01".into()).unwrap();
        let result = session.recording_signature("course", "chosen").unwrap();
        assert_eq!(result["bind_id"], "id");
        let requests = session.transport.requests.borrow();
        assert!(requests[2].0.contains("rwh=chosen"));
        assert!(requests
            .iter()
            .all(|(_, portal, recording)| *portal && !recording));
    }
    impl Transport for Stub {
        fn send(&self, req: &Request) -> Result<Response, NetworkError> {
            self.requests.borrow_mut().push((
                req.url.clone(),
                req.headers.iter().any(|h| h.0 == "Token"),
                req.headers.iter().any(|h| h.0 == "Authorization"),
            ));
            if self.responses.borrow().is_empty() {
                return Err(NetworkError::Transport);
            }
            Ok(self.responses.borrow_mut().remove(0))
        }
    }
    fn response(body: &str) -> Response {
        Response {
            status: 200,
            body: body.into(),
        }
    }
    #[test]
    fn portal_and_recording_credentials_never_cross_hosts() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![
                response(r#"{"code":200,"content":[{"xnxq":"term"}]}"#),
                response(r#"{"code":0,"result":{"data":[]}}"#),
            ]),
        };
        let mut session =
            SchoolSession::new(stub, "portal".into(), "".into(), "01".into()).unwrap();
        assert_eq!(session.semesters().unwrap(), vec!["term"]);
        session.set_recording_token("recording".into()).unwrap();
        assert!(session.catalogue("id").unwrap().is_empty());
        let requests = session.transport.requests.borrow();
        assert!(requests[0].0.starts_with("https://spoc.buaa.edu.cn/"));
        assert_eq!((requests[0].1, requests[0].2), (true, false));
        assert!(requests[1].0.starts_with("https://yjapi.msa.buaa.edu.cn/"));
        assert_eq!((requests[1].1, requests[1].2), (false, true));
    }
    #[test]
    fn expired_portal_token_refreshes_once_then_retries() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![
                response(r#"{"code":401}"#),
                response(r#"{"code":200,"content":{"token":"new"}}"#),
                response(r#"{"code":200,"content":[{"xnxq":"term"}]}"#),
            ]),
        };
        let mut session =
            SchoolSession::new(stub, "old".into(), "refresh".into(), "01".into()).unwrap();
        assert_eq!(session.semesters().unwrap(), vec!["term"]);
        assert_eq!(session.transport.requests.borrow().len(), 3);
        assert_eq!(session.portal_token, "new");
    }
    #[test]
    fn temporary_refresh_failure_does_not_become_login_expiry() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![
                response(r#"{"code":401}"#),
                Response {
                    status: 503,
                    body: String::new(),
                },
            ]),
        };
        let mut session =
            SchoolSession::new(stub, "old".into(), "refresh".into(), "01".into()).unwrap();
        assert_eq!(session.semesters().unwrap_err(), NetworkError::Transport);
        assert_eq!(session.portal_token, "old");
    }
    #[test]
    fn recording_requires_own_authorization_and_rejects_failure() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![response(r#"{"code":5002}"#)]),
        };
        let mut session =
            SchoolSession::new(stub, "portal".into(), "".into(), "01".into()).unwrap();
        assert!(matches!(
            session.catalogue("1"),
            Err(NetworkError::RecordingAuth)
        ));
        session.set_recording_token("recording".into()).unwrap();
        assert!(matches!(
            session.catalogue("1"),
            Err(NetworkError::RecordingAuth)
        ));
    }
    #[test]
    fn query_values_are_percent_encoded() {
        assert_eq!(
            endpoint("spoc.buaa.edu.cn", "/x", &[("id", "a&b 中文".into())]),
            "https://spoc.buaa.edu.cn/x?id=a%26b%20%E4%B8%AD%E6%96%87"
        );
    }

    #[test]
    fn timetable_reads_real_date_fields_and_rejects_invalid_range() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![response(
                r#"{"code":200,"content":[{"kcid":"c","kcmc":"课程","rq":"2026-10-21","strkcsj":"08:00-09:35","skdd":"教室"}]}"#,
            )]),
        };
        let mut session =
            SchoolSession::new(stub, "portal".into(), "".into(), "01".into()).unwrap();
        assert!(matches!(
            session.schedule("2026-10-22", "2026-10-21"),
            Err(NetworkError::InvalidResponse)
        ));
        let rows = session.schedule("2026-10-21", "2026-10-27").unwrap();
        assert_eq!(rows[0].course_id, "c");
        assert_eq!(rows[0].date, "2026-10-21");
        assert_eq!(rows[0].room, "教室");
        assert!(session.transport.requests.borrow()[0]
            .0
            .contains("zksrq=2026-10-21"));
    }

    #[test]
    fn materials_fetches_transcript_and_all_slide_pages() {
        let responses = vec![
            response(
                r#"{"code":0,"data":{"resource_guid":"g","duration":120,"video_list":[{"type":"2","preview_url":"https://resource.msa.buaa.edu.cn/video?token=secret","duration":120},{"preview_url":"https://evil.example/video"}]}}"#,
            ),
            response(
                r#"{"code":0,"list":[{"all_content":[{"Text":"课堂","BeginSec":1,"EndSec":2}]}]}"#,
            ),
            response(
                r#"{"code":0,"total":2,"list":[{"content":{"pptimgurl":"https://resource.msa.buaa.edu.cn/a.png?token=x"},"created_sec":1}]}"#,
            ),
            response(
                r#"{"code":0,"total":2,"list":[{"content":{"pptimgurl":"https://resource.msa.buaa.edu.cn/b.png"},"created_sec":2}]}"#,
            ),
        ];
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(responses),
        };
        let mut session =
            SchoolSession::new(stub, "portal".into(), "".into(), "01".into()).unwrap();
        session.set_recording_token("recording".into()).unwrap();
        let material = session.materials("course", "lesson").unwrap();
        assert_eq!(material.segments.len(), 1);
        assert_eq!(material.slides.len(), 2);
        assert_eq!(material.lesson_id, "lesson");
        assert_eq!(material.duration, 120.0);
        assert_eq!(material.videos.len(), 1);
        assert_eq!(
            material.videos[0].url,
            "https://resource.msa.buaa.edu.cn/video"
        );
        assert_eq!(material.videos[0].kind, "PPT");
        assert_eq!(material.digest.len(), 64);
        assert!(material.coverage.ratio.is_some());
        assert_eq!(material.limitations.len(), 2);
        assert_eq!(
            material.slides[0].url,
            "https://resource.msa.buaa.edu.cn/a.png"
        );
        assert_eq!(session.transport.requests.borrow().len(), 4);
        assert!(session
            .transport
            .requests
            .borrow()
            .iter()
            .all(|(_, portal, recording)| !portal && *recording));
    }
    #[test]
    fn material_coverage_uses_lesson_duration_when_school_omits_it() {
        let stub = Stub {
            requests: RefCell::new(Vec::new()),
            responses: RefCell::new(vec![
                response(r#"{"code":0,"data":{"resource_guid":"g"}}"#),
                response(
                    r#"{"code":0,"list":[{"all_content":[{"Text":"课","BeginSec":1,"EndSec":2}]}]}"#,
                ),
                response(r#"{"code":0,"total":0,"list":[]}"#),
            ]),
        };
        let mut session = SchoolSession::new(stub, "p".into(), "".into(), "01".into()).unwrap();
        session.set_recording_token("r".into()).unwrap();
        let result = session.materials_with_duration("c", "l", 100.0).unwrap();
        assert_eq!(result.duration, 100.0);
        assert_eq!(result.coverage.ratio, Some(0.01));
        assert!(session.materials_with_duration("c", "l", -1.0).is_err());
    }
}
