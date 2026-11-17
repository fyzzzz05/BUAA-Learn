//! Chunk extraction validation; model and transport are supplied by the host.
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::{
    learning::{self, LearningError},
    materials::Segment,
};

pub const SYSTEM: &str = include_str!("../resources/analysis-system.txt");
pub fn schema() -> Value {
    serde_json::from_str(include_str!("../resources/extraction-schema.json"))
        .expect("checked-in schema is valid")
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Point {
    text: String,
    evidence_ids: Vec<String>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Assignment {
    text: String,
    evidence_ids: Vec<String>,
    #[serde(default = "unspecified")]
    deadline: String,
    #[serde(default)]
    deadline_quote: String,
    #[serde(default = "unspecified")]
    audience: String,
    #[serde(default = "unspecified")]
    submission: String,
}
fn unspecified() -> String {
    "未说明".into()
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Notes {
    summary: String,
    #[serde(default)]
    knowledge_points: Vec<Point>,
    #[serde(default)]
    assignments: Vec<Assignment>,
    #[serde(default)]
    notices: Vec<Point>,
    #[serde(default)]
    review_suggestions: Vec<String>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExtractedChunk {
    pub summary: String,
    pub knowledge_points: Vec<Value>,
    pub assignments: Vec<Value>,
    pub notices: Vec<Value>,
    pub review_suggestions: Vec<String>,
    pub topic_payload: Value,
}

/// Providers must treat transcript material as data, use the provided output
/// contract, and not persist a response before validation succeeds.
pub trait Model {
    fn complete(
        &mut self,
        system: &str,
        prompt: &str,
        schema_name: &str,
        schema: &Value,
    ) -> Result<Value, LearningError>;
}
fn validate(payload: Value, evidence: &[Segment]) -> Result<ExtractedChunk, LearningError> {
    let mut object = payload
        .as_object()
        .cloned()
        .ok_or(LearningError::InvalidResponse)?;
    let topic_payload = json!({"topics":object.remove("topics"),"covered_until_id":object.remove("covered_until_id")});
    let notes: Notes = serde_json::from_value(Value::Object(object))
        .map_err(|_| LearningError::InvalidResponse)?;
    if notes.summary.chars().count() > 4000
        || notes.knowledge_points.len() > 60
        || notes.assignments.len() > 30
        || notes.notices.len() > 30
        || notes.review_suggestions.len() > 15
    {
        return Err(LearningError::InvalidResponse);
    }
    let convert = |data: Value| -> Result<Vec<Value>, LearningError> {
        let values = data.as_array().ok_or(LearningError::InvalidResponse)?;
        learning::ground_points(values, evidence)
    };
    Ok(ExtractedChunk {
        summary: notes.summary,
        knowledge_points: convert(json!(notes.knowledge_points))?,
        assignments: convert(json!(notes.assignments))?,
        notices: convert(json!(notes.notices))?,
        review_suggestions: notes.review_suggestions,
        topic_payload,
    })
}

/// Retry malformed or ungrounded model output up to twice, keeping material intact.
pub fn extract_chunk<M: Model>(
    model: &mut M,
    prompt: &str,
    evidence: &[Segment],
    mut progress: impl FnMut(&str),
) -> Result<ExtractedChunk, LearningError> {
    let (Some(first), Some(last)) = (evidence.first(), evidence.last()) else {
        return Err(LearningError::InvalidEvidence);
    };
    let instruction = SYSTEM
        .replace(
            "\"covered_until_id\":\"本段实际编号\"",
            &format!("\"covered_until_id\":\"{}\"", last.id),
        )
        .replace("本段实际编号", &first.id)
        + &format!(
            "\n本段材料首编号 {}，末编号 {}。\n字段必须遵循此 JSON Schema：\n{}",
            first.id,
            last.id,
            schema()
        );
    let mut issue = "";
    for attempt in 0..3 {
        let actual_prompt = if issue.is_empty() {
            prompt.to_string()
        } else {
            format!("{prompt}\n上次输出校验失败：{issue}。请重新依据原始材料输出完整 JSON，勿添加字段。")
        };
        let candidate = model.complete(&instruction, &actual_prompt, "Extraction", &schema());
        let error = match candidate.and_then(|value| validate(value, evidence)) {
            Ok(value) => return Ok(value),
            Err(LearningError::InvalidEvidence) => "evidence_ids 必须引用本段实际提供的材料编号",
            Err(LearningError::InvalidJson | LearningError::InvalidResponse) => {
                "根对象必须遵循输出结构，不能包含解释文字"
            }
            Err(error) => return Err(error),
        };
        issue = error;
        if attempt < 2 {
            progress(&format!(
                "输出校验失败，正在自动重试 {}/2：{issue}",
                attempt + 1
            ));
        }
    }
    Err(LearningError::InvalidResponse)
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fake {
        responses: Vec<Value>,
        calls: usize,
    }
    impl Model for Fake {
        fn complete(
            &mut self,
            _: &str,
            _: &str,
            name: &str,
            _: &Value,
        ) -> Result<Value, LearningError> {
            assert_eq!(name, "Extraction");
            self.calls += 1;
            Ok(self.responses.remove(0))
        }
    }
    fn source() -> Vec<Segment> {
        vec![Segment {
            id: "s0".into(),
            start: 0.0,
            end: 1.0,
            text: "下周五提交作业".into(),
        }]
    }
    fn payload(id: &str) -> Value {
        json!({"summary":"课堂","topics":[],"covered_until_id":"s0","knowledge_points":[{"text":"概念","evidence_ids":[id]}],"assignments":[{"text":"作业","evidence_ids":["s0"],"deadline":"下周五","deadline_quote":"下周五提交作业"}],"notices":[],"review_suggestions":[]})
    }
    #[test]
    fn invalid_citations_retry_and_valid_result_is_grounded() {
        let mut model = Fake {
            responses: vec![payload("fake"), payload("s0")],
            calls: 0,
        };
        let mut reports = Vec::new();
        let result = extract_chunk(&mut model, "课堂材料", &source(), |message| {
            reports.push(message.to_string())
        })
        .unwrap();
        assert_eq!(model.calls, 2);
        assert_eq!(reports.len(), 1);
        assert_eq!(result.assignments[0]["deadline"], "下周五");
        assert_eq!(result.knowledge_points[0]["source_range"]["start_id"], "s0");
    }
    #[test]
    fn repeated_invalid_output_never_returns_partial_findings() {
        let mut model = Fake {
            responses: vec![payload("bad"), payload("bad"), payload("bad")],
            calls: 0,
        };
        assert!(matches!(
            extract_chunk(&mut model, "课堂材料", &source(), |_| {}),
            Err(LearningError::InvalidResponse)
        ));
        assert_eq!(model.calls, 3);
    }
}
