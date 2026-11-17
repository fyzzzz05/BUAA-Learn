//! Parse recording transcripts and measure time coverage without inventing content.
use serde::{Deserialize, Serialize};
use serde_json::Value;
use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum MaterialError {
    #[error("识别文本读取失败")]
    RequestFailed,
    #[error("识别文本格式异常")]
    InvalidTranscript,
    #[error("PPT 截图索引读取失败")]
    InvalidSlides,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Segment {
    pub id: String,
    pub start: f64,
    pub end: f64,
    pub text: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Gap {
    pub start: f64,
    pub end: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Slide {
    pub id: String,
    pub start: f64,
    pub url: String,
}

/// Keep only known school HTTPS sources and strip temporary query parameters.
pub(crate) fn clean_school_url(value: &str) -> Option<String> {
    const HOSTS: &[&str] = &[
        "spoc.buaa.edu.cn",
        "classroom.msa.buaa.edu.cn",
        "yjapi.msa.buaa.edu.cn",
        "resource.msa.buaa.edu.cn",
        "portal.msa.buaa.edu.cn",
        "www.msa.buaa.edu.cn",
    ];
    let (scheme, rest) = value.split_once("://")?;
    if !scheme.eq_ignore_ascii_case("https") {
        return None;
    }
    let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
    let authority = &rest[..end];
    if !HOSTS
        .iter()
        .any(|host| authority.eq_ignore_ascii_case(host))
    {
        return None;
    }
    let path = &rest[end..];
    let path_end = path.find(['?', '#']).unwrap_or(path.len());
    Some(format!(
        "https://{}{}",
        authority.to_ascii_lowercase(),
        &path[..path_end]
    ))
}

/// Parse PPT rows from all pages, keeping only unique trusted image URLs.
pub fn parse_slides(rows: &[Value]) -> Result<Vec<Slide>, MaterialError> {
    let mut result = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for row in rows {
        let item: Value = match row.get("content") {
            Some(Value::String(body)) => match serde_json::from_str(body) {
                Ok(v) => v,
                Err(_) => continue,
            },
            Some(value @ Value::Object(_)) => value.clone(),
            _ => continue,
        };
        let Some(url) = item
            .get("pptimgurl")
            .and_then(Value::as_str)
            .and_then(clean_school_url)
        else {
            continue;
        };
        let Ok(start) = number(row.get("created_sec"), 0.0) else {
            continue;
        };
        if !start.is_finite() || start < 0.0 || !seen.insert(url.clone()) {
            continue;
        }
        result.push(Slide {
            id: format!("p{}", result.len()),
            start,
            url,
        });
    }
    Ok(result)
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Coverage {
    pub seconds: f64,
    pub ratio: Option<f64>,
    pub gaps: Vec<Gap>,
    pub note: String,
}

fn number(value: Option<&Value>, default: f64) -> Result<f64, MaterialError> {
    match value {
        None | Some(Value::Null) => Ok(default),
        Some(Value::Number(n)) => n.as_f64().ok_or(MaterialError::InvalidTranscript),
        Some(Value::String(s)) if s.is_empty() => Ok(default),
        Some(Value::String(s)) => s.parse().map_err(|_| MaterialError::InvalidTranscript),
        _ => Err(MaterialError::InvalidTranscript),
    }
}

pub fn parse_segments(body: &str) -> Result<Vec<Segment>, MaterialError> {
    let body: Value = serde_json::from_str(body).map_err(|_| MaterialError::InvalidTranscript)?;
    if body.get("code").and_then(Value::as_i64) != Some(0) {
        return Err(MaterialError::RequestFailed);
    }
    let groups = match body.get("list") {
        None | Some(Value::Null) => Vec::new(),
        Some(Value::Array(rows)) => rows.clone(),
        _ => return Err(MaterialError::InvalidTranscript),
    };
    let mut segments = Vec::new();
    for group in groups {
        let rows = match group.get("all_content") {
            Some(Value::Array(rows)) => rows.clone(),
            None | Some(Value::Null) => match group.get("trans_content") {
                Some(Value::String(text)) if !text.is_empty() => {
                    serde_json::from_str::<Vec<Value>>(text)
                        .map_err(|_| MaterialError::InvalidTranscript)?
                }
                None | Some(Value::Null) | Some(Value::String(_)) => Vec::new(),
                _ => return Err(MaterialError::InvalidTranscript),
            },
            _ => return Err(MaterialError::InvalidTranscript),
        };
        for row in rows {
            let text = row.get("Text").and_then(Value::as_str).unwrap_or("").trim();
            let start = number(row.get("BeginSec"), 0.0)?;
            let end = number(row.get("EndSec"), start)?;
            if !text.is_empty()
                && text != "-"
                && start.is_finite()
                && end.is_finite()
                && start >= 0.0
                && end >= start
            {
                segments.push(Segment {
                    id: String::new(),
                    start,
                    end,
                    text: text.to_string(),
                });
            }
        }
    }
    segments.sort_by(|a, b| a.start.total_cmp(&b.start).then(a.end.total_cmp(&b.end)));
    for (i, segment) in segments.iter_mut().enumerate() {
        segment.id = format!("s{i}");
    }
    Ok(segments)
}

/// Merge overlapping intervals and report gaps of 45 seconds or longer.
pub fn coverage(segments: &[Segment], duration: f64) -> Result<Coverage, MaterialError> {
    if !duration.is_finite() || duration < 0.0 {
        return Err(MaterialError::InvalidTranscript);
    }
    let mut intervals: Vec<(f64, f64)> = Vec::new();
    for row in segments {
        if !row.start.is_finite() || !row.end.is_finite() || row.start < 0.0 || row.end < row.start
        {
            return Err(MaterialError::InvalidTranscript);
        }
        let (start, end) = if duration > 0.0 {
            (row.start.min(duration), row.end.min(duration))
        } else {
            (row.start, row.end)
        };
        intervals.push((start, end));
    }
    intervals.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
    let mut merged: Vec<(f64, f64)> = Vec::new();
    for (start, end) in intervals {
        if let Some(last) = merged.last_mut() {
            if start <= last.1 {
                last.1 = last.1.max(end);
                continue;
            }
        }
        merged.push((start, end));
    }
    let mut gaps = Vec::new();
    let mut last = 0.0;
    let mut seconds = 0.0;
    for (start, end) in merged {
        if start - last >= 45.0 {
            gaps.push(Gap {
                start: last,
                end: start,
            });
        }
        seconds += end - start;
        last = end;
    }
    if duration - last >= 45.0 {
        gaps.push(Gap {
            start: last,
            end: duration,
        });
    }
    Ok(Coverage {
        seconds,
        ratio: if duration > 0.0 {
            Some((seconds / duration * 1000.0).round() / 1000.0)
        } else {
            None
        },
        gaps,
        note: "时间区间覆盖率不代表识别准确率；空白可能是静音、练习或漏识别。".into(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn transcript_is_sorted_and_discards_invalid_segments() {
        let body = r#"{"code":0,"list":[{"all_content":[{"Text":"后","BeginSec":20,"EndSec":30},{"Text":"  前  ","BeginSec":"0","EndSec":10},{"Text":"-","BeginSec":4},{"Text":"不合法","BeginSec":20,"EndSec":10}]}]}"#;
        let result = parse_segments(body).unwrap();
        assert_eq!(result.len(), 2);
        assert_eq!(result[0].text, "前");
        assert_eq!(result[1].id, "s1");
    }
    #[test]
    fn transcript_handles_json_groups_and_rejects_bad_json() {
        let body =
            r#"{"code":0,"list":[{"trans_content":"[{\"Text\":\"内容\",\"BeginSec\":1}]"}]}"#;
        assert_eq!(parse_segments(body).unwrap()[0].text, "内容");
        assert_eq!(
            parse_segments(r#"{"code":0,"list":[{"trans_content":"bad"}]}"#),
            Err(MaterialError::InvalidTranscript)
        );
        assert_eq!(
            parse_segments(r#"{"code":5002}"#),
            Err(MaterialError::RequestFailed)
        );
    }
    #[test]
    fn coverage_merges_overlap_and_reports_real_gaps() {
        let segments = vec![
            Segment {
                id: "a".into(),
                start: 10.0,
                end: 30.0,
                text: "a".into(),
            },
            Segment {
                id: "b".into(),
                start: 20.0,
                end: 40.0,
                text: "b".into(),
            },
            Segment {
                id: "c".into(),
                start: 90.0,
                end: 110.0,
                text: "c".into(),
            },
        ];
        let result = coverage(&segments, 150.0).unwrap();
        assert_eq!(result.seconds, 50.0);
        assert_eq!(result.ratio, Some(0.333));
        assert_eq!(
            result.gaps,
            vec![Gap {
                start: 40.0,
                end: 90.0
            }]
        );
        assert_eq!(coverage(&[], 0.0).unwrap().ratio, None);
    }

    #[test]
    fn slides_reject_untrusted_hosts_and_strip_temporary_query_values() {
        let rows = vec![
            serde_json::json!({"content":{"pptimgurl":"https://resource.msa.buaa.edu.cn/a.png?secret=1"},"created_sec":10}),
            serde_json::json!({"content":{"pptimgurl":"https://resource.msa.buaa.edu.cn/a.png?secret=2"},"created_sec":11}),
            serde_json::json!({"content":{"pptimgurl":"https://resource.msa.buaa.edu.cn.evil.test/a.png"}}),
            serde_json::json!({"content":{"pptimgurl":"http://resource.msa.buaa.edu.cn/a.png"}}),
        ];
        let result = parse_slides(&rows).unwrap();
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].url, "https://resource.msa.buaa.edu.cn/a.png");
        assert_eq!(result[0].start, 10.0);
    }
}
