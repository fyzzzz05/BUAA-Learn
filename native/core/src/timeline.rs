//! Ground model topic anchors in transcript order and preserve missing ranges.
use crate::materials::Segment;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashMap, HashSet};
use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum TimelineError {
    #[error("主题引用了不存在的课堂原文")]
    InvalidAnchor,
    #[error("主题覆盖范围无效")]
    InvalidRange,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Topic {
    pub title: String,
    pub summary: String,
    pub start_id: String,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TopicPlan {
    pub topics: Vec<Topic>,
    pub covered_until_id: String,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Chapter {
    pub kind: String,
    pub title: String,
    pub summary: String,
    pub start_id: String,
    pub end_id: String,
    pub start: f64,
    pub end: f64,
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Findings {
    #[serde(default)]
    pub knowledge_points: Vec<Value>,
    #[serde(default)]
    pub assignments: Vec<Value>,
    #[serde(default)]
    pub notices: Vec<Value>,
}
fn positions(segments: &[Segment]) -> Result<HashMap<&str, usize>, TimelineError> {
    let mut result = HashMap::new();
    for (index, row) in segments.iter().enumerate() {
        if !row.start.is_finite()
            || !row.end.is_finite()
            || row.start < 0.0
            || row.end < row.start
            || result.insert(row.id.as_str(), index).is_some()
        {
            return Err(TimelineError::InvalidRange);
        }
    }
    Ok(result)
}
fn make_chapter(kind: &str, title: &str, summary: &str, rows: &[Segment]) -> Chapter {
    Chapter {
        kind: kind.into(),
        title: title.into(),
        summary: summary.into(),
        start_id: rows[0].id.clone(),
        end_id: rows.last().unwrap().id.clone(),
        start: rows[0].start,
        end: rows.iter().map(|v| v.end).fold(0.0, f64::max),
    }
}
fn unclassified(rows: &[Segment]) -> Chapter {
    make_chapter(
        "unclassified",
        "待归类原文",
        "这一范围有识别文本，尚未归入已确认的主题。可查看原文；已提取的知识点与任务仍保留。",
        rows,
    )
}

pub fn ground_topics(
    plan: &TopicPlan,
    segments: &[Segment],
) -> Result<Vec<Chapter>, TimelineError> {
    let positions = positions(segments)?;
    if segments.is_empty() {
        return Ok(Vec::new());
    }
    if plan.topics.is_empty() {
        return Ok(vec![unclassified(segments)]);
    }
    let last = *positions
        .get(plan.covered_until_id.as_str())
        .ok_or(TimelineError::InvalidAnchor)?;
    let mut starts: BTreeMap<usize, &Topic> = BTreeMap::new();
    for topic in &plan.topics {
        if topic.title.is_empty()
            || topic.title.chars().count() > 200
            || topic.summary.is_empty()
            || topic.summary.chars().count() > 4000
        {
            return Err(TimelineError::InvalidRange);
        }
        let index = *positions
            .get(topic.start_id.as_str())
            .ok_or(TimelineError::InvalidAnchor)?;
        starts.entry(index).or_insert(topic);
    }
    if *starts.keys().last().unwrap() > last {
        return Err(TimelineError::InvalidRange);
    }
    let keys: Vec<usize> = starts.keys().copied().collect();
    let mut result = Vec::new();
    if keys[0] > 0 {
        result.push(unclassified(&segments[..keys[0]]));
    }
    for (i, first) in keys.iter().enumerate() {
        let end = keys.get(i + 1).map_or(last, |next| next - 1);
        let topic = starts[first];
        result.push(make_chapter(
            "chapter",
            &topic.title,
            &topic.summary,
            &segments[*first..=end],
        ));
    }
    if last + 1 < segments.len() {
        result.push(unclassified(&segments[last + 1..]));
    }
    Ok(result)
}

fn selected(points: &[Value], ids: &HashSet<&str>) -> Vec<Value> {
    points
        .iter()
        .filter(|point| {
            point
                .get("evidence_ids")
                .and_then(Value::as_array)
                .is_some_and(|refs| {
                    refs.iter()
                        .filter_map(Value::as_str)
                        .any(|id| ids.contains(id))
                })
        })
        .cloned()
        .collect()
}
fn gap(start: f64, end: f64) -> Value {
    json!({"kind":"gap","title":"识别文本空白","start":start,"end":end,"summary":"这一时段没有可用的识别文本，无法判断讲授内容，请核对原始回放。"})
}
pub fn build_timeline(
    chapters: &[Chapter],
    segments: &[Segment],
    findings: &Findings,
    duration: f64,
) -> Result<Vec<Value>, TimelineError> {
    if !duration.is_finite() || duration < 0.0 {
        return Err(TimelineError::InvalidRange);
    }
    let positions = positions(segments)?;
    let mut sections = Vec::new();
    for chapter in chapters {
        let first = *positions
            .get(chapter.start_id.as_str())
            .ok_or(TimelineError::InvalidAnchor)?;
        let last = *positions
            .get(chapter.end_id.as_str())
            .ok_or(TimelineError::InvalidAnchor)?;
        if last < first {
            return Err(TimelineError::InvalidRange);
        }
        let rows = &segments[first..=last];
        let mut starts = vec![0];
        let mut end = rows[0].end;
        for (i, row) in rows.iter().enumerate().skip(1) {
            if row.start - end > 45.0 {
                starts.push(i);
            }
            end = end.max(row.end);
        }
        for (index, begin) in starts.iter().enumerate() {
            let finish = starts.get(index + 1).copied().unwrap_or(rows.len());
            let run = &rows[*begin..finish];
            let ids: HashSet<&str> = run.iter().map(|r| r.id.as_str()).collect();
            let suffix = if index > 0 { "（续）" } else { "" };
            let part = make_chapter(
                &chapter.kind,
                &format!("{}{suffix}", chapter.title),
                &chapter.summary,
                run,
            );
            sections.push(json!({"kind":part.kind,"title":part.title,"summary":part.summary,"start_id":part.start_id,"end_id":part.end_id,"start":part.start,"end":part.end,
                "knowledge_points":selected(&findings.knowledge_points,&ids),"assignments":selected(&findings.assignments,&ids),"notices":selected(&findings.notices,&ids)}));
        }
    }
    let mut result = Vec::new();
    let mut covered = 0.0_f64;
    for section in sections {
        let start = section["start"]
            .as_f64()
            .ok_or(TimelineError::InvalidRange)?;
        let end = section["end"].as_f64().ok_or(TimelineError::InvalidRange)?;
        if start - covered > 45.0 {
            result.push(gap(covered, start));
        }
        covered = covered.max(end);
        result.push(section);
    }
    if duration - covered > 45.0 {
        result.push(gap(covered, duration));
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn rows() -> Vec<Segment> {
        (0..4)
            .map(|i| Segment {
                id: format!("s{i}"),
                start: i as f64 * 60.0,
                end: (i + 1) as f64 * 60.0,
                text: "课堂".into(),
            })
            .collect()
    }
    fn topic(id: &str) -> Topic {
        Topic {
            title: "主题".into(),
            summary: "内容".into(),
            start_id: id.into(),
        }
    }
    #[test]
    fn anchors_derive_ends_and_preserve_unclassified_edges() {
        let plan = TopicPlan {
            topics: vec![topic("s1")],
            covered_until_id: "s2".into(),
        };
        let result = ground_topics(&plan, &rows()).unwrap();
        assert_eq!(
            result.iter().map(|v| v.kind.as_str()).collect::<Vec<_>>(),
            vec!["unclassified", "chapter", "unclassified"]
        );
        assert_eq!(
            (&result[1].start_id, &result[1].end_id),
            (&"s1".into(), &"s2".into())
        );
    }
    #[test]
    fn invalid_anchors_are_never_accepted() {
        assert_eq!(
            ground_topics(
                &TopicPlan {
                    topics: vec![topic("s9")],
                    covered_until_id: "s3".into()
                },
                &rows()
            )
            .unwrap_err(),
            TimelineError::InvalidAnchor
        );
        assert_eq!(
            ground_topics(
                &TopicPlan {
                    topics: vec![topic("s2")],
                    covered_until_id: "s1".into()
                },
                &rows()
            )
            .unwrap_err(),
            TimelineError::InvalidRange
        );
    }
    #[test]
    fn real_gaps_stay_separate_from_unclassified_transcript() {
        let segments = vec![
            Segment {
                id: "s0".into(),
                start: 60.0,
                end: 120.0,
                text: "a".into(),
            },
            Segment {
                id: "s1".into(),
                start: 240.0,
                end: 300.0,
                text: "b".into(),
            },
        ];
        let chapters = ground_topics(
            &TopicPlan {
                topics: vec![],
                covered_until_id: "".into(),
            },
            &segments,
        )
        .unwrap();
        let findings = Findings {
            knowledge_points: vec![json!({"text":"概念","evidence_ids":["s1"]})],
            ..Default::default()
        };
        let result = build_timeline(&chapters, &segments, &findings, 420.0).unwrap();
        assert_eq!(
            result
                .iter()
                .map(|v| v["kind"].as_str().unwrap())
                .collect::<Vec<_>>(),
            vec!["gap", "unclassified", "gap", "unclassified", "gap"]
        );
        assert_eq!(result[3]["knowledge_points"][0]["text"], "概念");
    }
}
