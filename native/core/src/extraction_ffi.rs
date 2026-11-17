//! C bridge for validated analysis with a host-provided model transport.
use crate::{
    extraction::{self, Model},
    learning::LearningError,
    materials::Segment,
};
use serde_json::{json, Value};
use std::{
    ffi::{c_void, CStr, CString},
    os::raw::c_char,
};

type SendFn = unsafe extern "C" fn(*mut c_void, *const c_char) -> *mut c_char;
type ReleaseFn = unsafe extern "C" fn(*mut c_void, *mut c_char);
type ProgressFn = unsafe extern "C" fn(*mut c_void, *const c_char);
struct ForeignModel {
    context: *mut c_void,
    send: SendFn,
    release: ReleaseFn,
}
impl Model for ForeignModel {
    fn complete(
        &mut self,
        system: &str,
        prompt: &str,
        name: &str,
        schema: &Value,
    ) -> Result<Value, LearningError> {
        let request = CString::new(
            json!({"system":system,"prompt":prompt,"schema":{"name":name,"schema":schema}})
                .to_string(),
        )
        .map_err(|_| LearningError::InvalidResponse)?;
        let pointer = unsafe { (self.send)(self.context, request.as_ptr()) };
        if pointer.is_null() {
            return Err(LearningError::Failed);
        }
        let value = unsafe { CStr::from_ptr(pointer) }
            .to_str()
            .ok()
            .and_then(|text| serde_json::from_str::<Value>(text).ok());
        unsafe { (self.release)(self.context, pointer) };
        let value = value.ok_or(LearningError::InvalidResponse)?;
        if value.get("ok").and_then(Value::as_bool) != Some(true) {
            return Err(match value.get("code").and_then(Value::as_str) {
                Some("invalid_json") => LearningError::InvalidJson,
                Some("invalid_response") => LearningError::InvalidResponse,
                Some("incomplete") => LearningError::Incomplete,
                Some("refused") => LearningError::Refused,
                _ => LearningError::Failed,
            });
        }
        value
            .get("data")
            .cloned()
            .ok_or(LearningError::InvalidResponse)
    }
}
unsafe fn read<'a>(pointer: *const c_char) -> Result<&'a str, LearningError> {
    if pointer.is_null() {
        return Err(LearningError::InvalidResponse);
    }
    CStr::from_ptr(pointer)
        .to_str()
        .map_err(|_| LearningError::InvalidResponse)
}
fn output(result: Result<Value, LearningError>) -> *mut c_char {
    let value = match result {
        Ok(data) => json!({"ok":true,"data":data}),
        Err(error) => json!({"ok":false,"error":error.to_string(),"error_code":error.code()}),
    };
    CString::new(value.to_string())
        .expect("JSON escapes NUL")
        .into_raw()
}
/// Analyze one transcript chunk using a callback. No model results are saved.
///
/// # Safety
/// Inputs must be valid NUL-terminated UTF-8; callbacks and context must remain
/// valid for the entire call. The callback returns an owned JSON string freed
/// by `release`. Free the function result with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_extract_chunk(
    context: *mut c_void,
    send: Option<SendFn>,
    release: Option<ReleaseFn>,
    prompt: *const c_char,
    evidence: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, LearningError> {
        let model = ForeignModel {
            context,
            send: send.ok_or(LearningError::InvalidResponse)?,
            release: release.ok_or(LearningError::InvalidResponse)?,
        };
        let evidence: Vec<Segment> =
            serde_json::from_str(read(evidence)?).map_err(|_| LearningError::InvalidResponse)?;
        let mut model = model;
        Ok(json!(extraction::extract_chunk(
            &mut model,
            read(prompt)?,
            &evidence,
            |_| {}
        )?))
    })();
    output(result)
}

/// Analyze an entire lesson; no result is persisted until the host saves it.
///
/// # Safety
/// `input` must be NUL-terminated UTF-8 JSON and callbacks/context must remain
/// valid throughout the call. Progress strings are borrowed for the callback.
/// Free the returned result with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_analyze_lesson(
    context: *mut c_void,
    send: Option<SendFn>,
    release: Option<ReleaseFn>,
    progress: Option<ProgressFn>,
    input: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, LearningError> {
        let mut model = ForeignModel {
            context,
            send: send.ok_or(LearningError::InvalidResponse)?,
            release: release.ok_or(LearningError::InvalidResponse)?,
        };
        let data: crate::analysis_pipeline::AnalysisInput =
            serde_json::from_str(read(input)?).map_err(|_| LearningError::InvalidResponse)?;
        crate::analysis_pipeline::analyze(&mut model, &data, |message| {
            if let (Some(progress), Ok(message)) = (progress, CString::new(message)) {
                unsafe { progress(context, message.as_ptr()) };
            }
        })
    })();
    output(result)
}

/// Run course-scoped question answering with the host model callback.
/// # Safety
/// The input and callbacks must remain valid for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_ask(
    context: *mut c_void,
    send: Option<SendFn>,
    release: Option<ReleaseFn>,
    input: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, LearningError> {
        let mut model = ForeignModel {
            context,
            send: send.ok_or(LearningError::InvalidResponse)?,
            release: release.ok_or(LearningError::InvalidResponse)?,
        };
        let input: crate::learning::AskInput =
            serde_json::from_str(read(input)?).map_err(|_| LearningError::InvalidResponse)?;
        crate::learning::ask(&mut model, &input)
    })();
    output(result)
}
