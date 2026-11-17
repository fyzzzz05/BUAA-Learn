//! C ABI for the desktop hosts. Only normalized JSON crosses this boundary.
use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::sync::Mutex;

use serde_json::{json, Value};

/// ABI major version. Increment for incompatible signatures or ownership changes.
/// This query allocates nothing and is safe before any handle is created.
#[no_mangle]
pub extern "C" fn buaa_core_abi_version() -> u32 {
    1
}

fn string_result<E: std::fmt::Display>(result: Result<Value, E>) -> *mut c_char {
    let value = match result {
        Ok(data) => json!({"ok": true, "data": data}),
        Err(error) => json!({"ok": false, "error": error.to_string()}),
    };
    // JSON encoding escapes embedded NUL bytes.
    CString::new(value.to_string())
        .expect("serialized JSON contains no NUL")
        .into_raw()
}
fn learning_result(result: Result<Value, crate::learning::LearningError>) -> *mut c_char {
    let value = match result {
        Ok(data) => json!({"ok":true,"data":data}),
        Err(error) => json!({"ok":false,"error":error.to_string(),"error_code":error.code()}),
    };
    CString::new(value.to_string())
        .expect("JSON escapes NUL")
        .into_raw()
}

unsafe fn input<'a>(ptr: *const c_char) -> Result<&'a str, crate::PortalDataError> {
    if ptr.is_null() {
        return Err(crate::PortalDataError::InvalidResponse);
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map_err(|_| crate::PortalDataError::InvalidResponse)
}

/// Inspect bounded original image bytes. The result is an owned JSON envelope.
/// # Safety
/// `bytes` must point to `length` readable bytes for this call only. Null/zero
/// and lengths exceeding 12 MiB are rejected before dereferencing the pointer.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_inspect_slide_image(
    bytes: *const u8,
    length: u32,
) -> *mut c_char {
    if bytes.is_null() || length == 0 || length as usize > crate::slide_image::MAX_BYTES {
        return string_result(Err::<Value, _>(crate::slide_image::ImageError));
    }
    let input = std::slice::from_raw_parts(bytes, length as usize);
    let result = std::panic::catch_unwind(|| crate::slide_image::inspect(input))
        .map_err(|_| crate::slide_image::ImageError)
        .and_then(|value| value)
        .and_then(|info| serde_json::to_value(info).map_err(|_| crate::slide_image::ImageError));
    string_result(result)
}

/// Parse school semester JSON.
///
/// # Safety
/// `body` must be a valid NUL-terminated UTF-8 C string for the call's duration.
/// The returned string must be released once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_parse_semesters(body: *const c_char) -> *mut c_char {
    let result = input(body)
        .and_then(crate::parse_semesters)
        .map(|v| json!(v));
    string_result(result)
}

/// Parse school course JSON for the explicitly selected semester.
///
/// # Safety
/// Both inputs must be valid NUL-terminated UTF-8 C strings for the call's duration.
/// The returned string must be released once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_parse_courses(
    body: *const c_char,
    semester: *const c_char,
) -> *mut c_char {
    let result = input(body)
        .and_then(|body| input(semester).and_then(|term| crate::parse_courses(body, term)))
        .map(|v| json!(v));
    string_result(result)
}

/// Parse an authenticated recording catalogue response.
///
/// # Safety
/// `body` must be a valid NUL-terminated UTF-8 C string for the call's duration.
/// The returned string must be released once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_parse_catalogue(body: *const c_char) -> *mut c_char {
    string_result(
        input(body)
            .and_then(crate::parse_catalogue)
            .map(|v| json!(v)),
    )
}

/// Parse a transcript response.
///
/// # Safety
/// `body` must be a valid NUL-terminated UTF-8 C string for the call's duration.
/// The returned string must be released once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_parse_transcript(body: *const c_char) -> *mut c_char {
    let result = input(body)
        .map_err(|_| crate::materials::MaterialError::InvalidTranscript)
        .and_then(crate::materials::parse_segments);
    string_result(result.map(|data| json!(data)))
}

/// Parse an array of PPT index rows.
///
/// # Safety
/// `body` must be a valid NUL-terminated UTF-8 C string. Release the returned
/// string exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_parse_slides(body: *const c_char) -> *mut c_char {
    let result = (|| -> Result<Value, crate::materials::MaterialError> {
        let raw = input(body).map_err(|_| crate::materials::MaterialError::InvalidSlides)?;
        let rows: Vec<Value> = serde_json::from_str(raw)
            .map_err(|_| crate::materials::MaterialError::InvalidSlides)?;
        Ok(json!(crate::materials::parse_slides(&rows)?))
    })();
    string_result(result)
}

/// Plan watches from JSON rule and timetable rows and an RFC 3339 current time.
///
/// # Safety
/// Inputs must be valid NUL-terminated UTF-8 C strings. Release the returned
/// string exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_plan_automation(
    rule: *const c_char,
    rows: *const c_char,
    current: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, crate::automation::AutomationError> {
        let invalid = || crate::automation::AutomationError::InvalidRule;
        let rule: crate::automation::Rule =
            serde_json::from_str(input(rule).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let rows: Vec<crate::automation::TimetableRow> =
            serde_json::from_str(input(rows).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let now = chrono::DateTime::parse_from_rfc3339(input(current).map_err(|_| invalid())?)
            .map_err(|_| invalid())?;
        Ok(json!(crate::automation::plan(&rule, &rows, now)?))
    })();
    string_result(result)
}

/// Validate model findings against the ordered transcript before saving.
///
/// # Safety
/// Both inputs must be valid NUL-terminated UTF-8 C strings. Release the result
/// exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_ground_points(
    points: *const c_char,
    evidence: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, crate::learning::LearningError> {
        let invalid = || crate::learning::LearningError::InvalidResponse;
        let points: Vec<Value> =
            serde_json::from_str(input(points).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let evidence: Vec<crate::materials::Segment> =
            serde_json::from_str(input(evidence).map_err(|_| invalid())?).map_err(|_| invalid())?;
        Ok(json!(crate::learning::ground_points(&points, &evidence)?))
    })();
    string_result(result)
}

/// Validate a model answer and its citations against retrieved source IDs.
///
/// # Safety
/// Inputs must be valid NUL-terminated UTF-8 strings; the last two contain JSON
/// arrays. Release the result exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_validate_answer(
    answer: *const c_char,
    cited: *const c_char,
    available: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, crate::learning::LearningError> {
        let invalid = || crate::learning::LearningError::InvalidResponse;
        let answer = input(answer).map_err(|_| invalid())?;
        let cited: Vec<String> =
            serde_json::from_str(input(cited).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let available: Vec<String> = serde_json::from_str(input(available).map_err(|_| invalid())?)
            .map_err(|_| invalid())?;
        crate::learning::validate_answer(answer, &cited, &available)?;
        Ok(Value::Null)
    })();
    string_result(result)
}

/// Build a source-grounded timeline from the topic plan and transcript.
///
/// # Safety
/// Inputs must be valid NUL-terminated UTF-8 JSON strings. Release the result
/// exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_timeline(
    plan: *const c_char,
    segments: *const c_char,
    findings: *const c_char,
    duration: f64,
) -> *mut c_char {
    let result = (|| -> Result<Value, crate::timeline::TimelineError> {
        let invalid = || crate::timeline::TimelineError::InvalidRange;
        let plan: crate::timeline::TopicPlan =
            serde_json::from_str(input(plan).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let segments: Vec<crate::materials::Segment> =
            serde_json::from_str(input(segments).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let findings: crate::timeline::Findings =
            serde_json::from_str(input(findings).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let chapters = crate::timeline::ground_topics(&plan, &segments)?;
        Ok(json!(crate::timeline::build_timeline(
            &chapters, &segments, &findings, duration
        )?))
    })();
    string_result(result)
}

fn model_provider(
    value: &str,
) -> Result<crate::learning::Provider, crate::learning::LearningError> {
    match value {
        "responses" => Ok(crate::learning::Provider::Responses),
        "chat" => Ok(crate::learning::Provider::ChatCompletions),
        _ => Err(crate::learning::LearningError::InvalidResponse),
    }
}

/// Build a provider request. `schema` is JSON null or an object with name and schema.
///
/// # Safety
/// All inputs must be valid NUL-terminated UTF-8 C strings. Free the result
/// exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_model_request(
    provider: *const c_char,
    model: *const c_char,
    system: *const c_char,
    prompt: *const c_char,
    schema: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, crate::learning::LearningError> {
        let invalid = || crate::learning::LearningError::InvalidResponse;
        let provider = model_provider(input(provider).map_err(|_| invalid())?)?;
        let model = input(model).map_err(|_| invalid())?;
        let system = input(system).map_err(|_| invalid())?;
        let prompt = input(prompt).map_err(|_| invalid())?;
        let schema: Value =
            serde_json::from_str(input(schema).map_err(|_| invalid())?).map_err(|_| invalid())?;
        let contract = if schema.is_null() {
            None
        } else {
            Some((
                schema
                    .get("name")
                    .and_then(Value::as_str)
                    .ok_or_else(invalid)?,
                schema.get("schema").cloned().ok_or_else(invalid)?,
            ))
        };
        crate::learning::request_body(provider, model, system, prompt, contract)
    })();
    learning_result(result)
}

/// Validate and parse one provider response.
///
/// # Safety
/// Inputs must be valid NUL-terminated UTF-8 C strings. Release the result
/// exactly once with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_model_parse(
    provider: *const c_char,
    body: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, crate::learning::LearningError> {
        let invalid = || crate::learning::LearningError::InvalidResponse;
        crate::learning::parse_response(
            model_provider(input(provider).map_err(|_| invalid())?)?,
            input(body).map_err(|_| invalid())?,
        )
    })();
    learning_result(result)
}

/// Opaque, mutex-protected account-scoped database handle.
pub struct CoreStore(Mutex<crate::store::Store>);

/// Atomically save a nonempty, owner-checked material snapshot and lesson status.
/// # Safety
/// Handle and UTF-8 strings must remain valid. Free the envelope exactly once.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_save_materials(
    handle: *mut CoreStore,
    owner: *const c_char,
    course: *const c_char,
    lesson: *const c_char,
    body: *const c_char,
) -> *mut c_char {
    string_result((|| -> Result<Value, String> {
        let material: Value = serde_json::from_str(input(body).map_err(|e| e.to_string())?)
            .map_err(|_| "课堂材料格式异常")?;
        handle
            .as_ref()
            .ok_or("本地数据库未打开")?
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .save_materials(
                input(owner).map_err(|e| e.to_string())?,
                input(course).map_err(|e| e.to_string())?,
                input(lesson).map_err(|e| e.to_string())?,
                &material,
            )
            .map_err(|e| e.to_string())?;
        Ok(Value::Null)
    })())
}

/// Open a versioned workspace after the host acquires its exclusive lease.
/// # Safety
/// Strings must be live UTF-8 C strings. `out` must point to writable storage.
/// Close the successful handle and free the envelope exactly once.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_open_versioned(
    path: *const c_char,
    backup: *const c_char,
    out: *mut *mut CoreStore,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let out = out.as_mut().ok_or("本地数据库输出无效")?;
        *out = std::ptr::null_mut();
        let path = input(path).map_err(|e| e.to_string())?;
        let backup = input(backup).map_err(|e| e.to_string())?;
        let (store, backed_up) = crate::store::Store::open_versioned(
            std::path::Path::new(path),
            std::path::Path::new(backup),
        )
        .map_err(|e| e.to_string())?;
        *out = Box::into_raw(Box::new(CoreStore(Mutex::new(store))));
        Ok(
            json!({"schema_version":crate::store::Store::WINDOWS_SCHEMA_VERSION,"backup_created":backed_up}),
        )
    })();
    string_result(result)
}

/// Read a non-secret host setting.
/// # Safety
/// Handle/text must be valid; free the returned envelope exactly once.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_setting_get(
    handle: *mut CoreStore,
    key: *const c_char,
) -> *mut c_char {
    string_result((|| -> Result<Value, String> {
        handle
            .as_ref()
            .ok_or("本地数据库未打开")?
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .get_setting(input(key).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())
    })())
}

/// Save a non-secret host setting atomically.
/// # Safety
/// Handle/text must be valid; free the returned envelope exactly once.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_setting_put(
    handle: *mut CoreStore,
    key: *const c_char,
    body: *const c_char,
) -> *mut c_char {
    string_result((|| -> Result<Value, String> {
        let body: Value = serde_json::from_str(input(body).map_err(|e| e.to_string())?)
            .map_err(|_| "配置格式异常")?;
        handle
            .as_ref()
            .ok_or("本地数据库未打开")?
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .set_setting(input(key).map_err(|e| e.to_string())?, &body)
            .map_err(|e| e.to_string())?;
        Ok(Value::Null)
    })())
}

/// Write an account-scoped record batch in one transaction.
/// # Safety
/// Handle/text must be valid; free the returned envelope exactly once.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_put_batch(
    handle: *mut CoreStore,
    owner: *const c_char,
    rows: *const c_char,
) -> *mut c_char {
    string_result((|| -> Result<Value, String> {
        let rows: Vec<Value> = serde_json::from_str(input(rows).map_err(|e| e.to_string())?)
            .map_err(|_| "批量记录格式异常")?;
        handle
            .as_ref()
            .ok_or("本地数据库未打开")?
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .put_batch(input(owner).map_err(|e| e.to_string())?, &rows)
            .map_err(|e| e.to_string())?;
        Ok(Value::Null)
    })())
}

/// Open or create a native database. Returns null on failure.
///
/// # Safety
/// `path` must be a valid NUL-terminated UTF-8 path. The caller owns the result
/// and must close it only after all operations on the handle have finished.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_open(path: *const c_char) -> *mut CoreStore {
    let Ok(path) = input(path) else {
        return std::ptr::null_mut();
    };
    match crate::store::Store::open(std::path::Path::new(path)) {
        Ok(store) => Box::into_raw(Box::new(CoreStore(Mutex::new(store)))),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Store one account-scoped JSON record and return a JSON result envelope.
///
/// # Safety
/// `handle` must be a live store handle; all text inputs must be valid
/// NUL-terminated UTF-8 strings. Release the result with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_put(
    handle: *mut CoreStore,
    owner: *const c_char,
    kind: *const c_char,
    id: *const c_char,
    body: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let kind = crate::store::RecordKind::parse(input(kind).map_err(|_| "记录类型无效")?)
            .map_err(|e| e.to_string())?;
        let id = input(id).map_err(|_| "记录标识无效")?;
        let body: Value = serde_json::from_str(input(body).map_err(|_| "记录内容无效")?)
            .map_err(|_| "记录内容无效")?;
        handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .put(owner, kind, id, &body)
            .map_err(|e| e.to_string())?;
        Ok(Value::Null)
    })();
    string_result(result)
}

/// Merge a batch of school records atomically. Inputs and handle must be valid.
/// # Safety
/// All pointers must remain valid throughout the call; free the result with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_merge_school(
    handle: *mut CoreStore,
    owner: *const c_char,
    records: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let records: Vec<Value> = serde_json::from_str(input(records).map_err(|_| "记录内容无效")?)
            .map_err(|_| "记录内容无效")?;
        handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .merge_school_records(owner, &records)
            .map_err(|e| e.to_string())?;
        Ok(Value::Null)
    })();
    string_result(result)
}

/// Read one account-scoped JSON record; a missing record returns JSON null.
///
/// # Safety
/// `handle` must be live; text inputs must be valid NUL-terminated UTF-8.
/// Release the returned string with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_get(
    handle: *mut CoreStore,
    owner: *const c_char,
    kind: *const c_char,
    id: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let kind = crate::store::RecordKind::parse(input(kind).map_err(|_| "记录类型无效")?)
            .map_err(|e| e.to_string())?;
        let id = input(id).map_err(|_| "记录标识无效")?;
        Ok(handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .get(owner, kind, id)
            .map_err(|e| e.to_string())?
            .unwrap_or(Value::Null))
    })();
    string_result(result)
}

/// List records belonging to one account and record kind.
///
/// # Safety
/// `handle` must be live; text inputs must be valid NUL-terminated UTF-8.
/// Release the returned string with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_list(
    handle: *mut CoreStore,
    owner: *const c_char,
    kind: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let kind = crate::store::RecordKind::parse(input(kind).map_err(|_| "记录类型无效")?)
            .map_err(|e| e.to_string())?;
        Ok(json!(handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .list(owner, kind)
            .map_err(|e| e.to_string())?))
    })();
    string_result(result)
}

/// Atomically persist a validated analysis and reconcile its assignments.
///
/// # Safety
/// `handle` must be live; owner and analysis must be NUL-terminated UTF-8.
/// Release the returned string with `buaa_core_string_free`.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_save_analysis(
    handle: *mut CoreStore,
    owner: *const c_char,
    analysis: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let analysis: Value = serde_json::from_str(input(analysis).map_err(|_| "课堂分析无效")?)
            .map_err(|_| "课堂分析无效")?;
        Ok(json!(handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .save_analysis(owner, &analysis)
            .map_err(|e| e.to_string())?))
    })();
    string_result(result)
}

/// Atomically require the material digest to match before saving analysis.
/// # Safety
/// The handle and all UTF-8 pointers must be valid; free the returned string.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_save_analysis_checked(
    handle: *mut CoreStore,
    owner: *const c_char,
    analysis: *const c_char,
    digest: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let analysis: Value = serde_json::from_str(input(analysis).map_err(|_| "课堂分析无效")?)
            .map_err(|_| "课堂分析无效")?;
        let digest = input(digest).map_err(|_| "材料标识无效")?;
        Ok(json!(handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .save_analysis_checked(owner, &analysis, Some(digest))
            .map_err(|e| e.to_string())?))
    })();
    string_result(result)
}

/// List account-scoped notice groups without transcript payloads.
/// # Safety
/// The handle and UTF-8 pointer must be valid; free the returned string.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_list_notice_groups(
    handle: *mut CoreStore,
    owner: *const c_char,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        Ok(json!(handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .list_notice_groups(owner)
            .map_err(|e| e.to_string())?))
    })();
    string_result(result)
}

/// Atomically update an active task's completion for one account.
/// # Safety
/// The handle and UTF-8 pointers must be valid; free the returned string.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_set_task_done(
    handle: *mut CoreStore,
    owner: *const c_char,
    id: *const c_char,
    done: u32,
) -> *mut c_char {
    let result = (|| -> Result<Value, String> {
        if done > 1 {
            return Err("作业状态无效".into());
        }
        let handle = handle.as_ref().ok_or("本地数据库未打开")?;
        let owner = input(owner).map_err(|_| "账号无效")?;
        let id = input(id).map_err(|_| "作业标识无效")?;
        handle
            .0
            .lock()
            .map_err(|_| "本地数据库不可用")?
            .set_task_done(owner, id, done == 1)
            .map_err(|e| e.to_string())
    })();
    string_result(result)
}

/// Close a store handle. Null is accepted.
///
/// # Safety
/// Non-null handles must be returned by `buaa_store_open`, used exactly once,
/// and not in use by another thread when this function is called.
#[no_mangle]
pub unsafe extern "C" fn buaa_store_close(handle: *mut CoreStore) {
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }
}

/// Free exactly one string returned from this library; null is accepted.
///
/// # Safety
/// A non-null pointer must be a live string returned by a function in this module.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_string_free(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn model_bridge_returns_stable_failure_codes_without_response_bodies() {
        unsafe {
            let provider = CString::new("responses").unwrap();
            for (body, code) in [
                (
                    r#"{"status":"incomplete","SENSITIVE":"never echo"}"#,
                    "incomplete",
                ),
                (
                    r#"{"status":"completed","output":[{"type":"message","content":[{"type":"refusal"}]}]}"#,
                    "refused",
                ),
            ] {
                let body = CString::new(body).unwrap();
                let pointer = buaa_core_model_parse(provider.as_ptr(), body.as_ptr());
                let result: Value =
                    serde_json::from_str(CStr::from_ptr(pointer).to_str().unwrap()).unwrap();
                assert_eq!(result["error_code"], code);
                assert!(!result.to_string().contains("SENSITIVE"));
                buaa_core_string_free(pointer);
            }
        }
    }
    #[test]
    fn c_abi_major_version_is_stable() {
        assert_eq!(buaa_core_abi_version(), 1);
    }

    #[test]
    fn c_bridge_returns_owned_structured_result_and_rejects_null() {
        unsafe {
            let body = CString::new(r#"{"code":200,"content":[{"xnxq":"term"}]}"#).unwrap();
            let ptr = buaa_core_parse_semesters(body.as_ptr());
            let result: Value =
                serde_json::from_str(CStr::from_ptr(ptr).to_str().unwrap()).unwrap();
            assert_eq!(result["data"][0], "term");
            buaa_core_string_free(ptr);
            let ptr = buaa_core_parse_courses(std::ptr::null(), std::ptr::null());
            let result: Value =
                serde_json::from_str(CStr::from_ptr(ptr).to_str().unwrap()).unwrap();
            assert_eq!(result["ok"], false);
            buaa_core_string_free(ptr);
        }
    }

    #[test]
    fn c_bridge_plans_automatic_recording_watches() {
        unsafe {
            let rule = CString::new(r#"{"course_id":"c","enabled":true}"#).unwrap();
            let rows =
                CString::new(r#"[{"course_id":"c","date":"2026-10-21","time":"08:00-09:35"}]"#)
                    .unwrap();
            let now = CString::new("2026-10-22T10:00:00+08:00").unwrap();
            let ptr = buaa_core_plan_automation(rule.as_ptr(), rows.as_ptr(), now.as_ptr());
            let result: Value =
                serde_json::from_str(CStr::from_ptr(ptr).to_str().unwrap()).unwrap();
            assert_eq!(result["data"]["pending_count"], 1);
            buaa_core_string_free(ptr);
        }
    }

    #[test]
    fn c_store_bridge_keeps_accounts_separate() {
        let path = std::env::temp_dir().join(format!(
            "buaa-ffi-{}-{}.sqlite",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        unsafe {
            let path_arg = CString::new(path.to_str().unwrap()).unwrap();
            let owner = CString::new("alice").unwrap();
            let other = CString::new("bob").unwrap();
            let kind = CString::new("course").unwrap();
            let id = CString::new("id").unwrap();
            let body = CString::new(r#"{"name":"example"}"#).unwrap();
            let handle = buaa_store_open(path_arg.as_ptr());
            assert!(!handle.is_null());
            let result = buaa_store_put(
                handle,
                owner.as_ptr(),
                kind.as_ptr(),
                id.as_ptr(),
                body.as_ptr(),
            );
            let value: Value =
                serde_json::from_str(CStr::from_ptr(result).to_str().unwrap()).unwrap();
            assert_eq!(value["ok"], true);
            buaa_core_string_free(result);
            let result = buaa_store_get(handle, other.as_ptr(), kind.as_ptr(), id.as_ptr());
            let value: Value =
                serde_json::from_str(CStr::from_ptr(result).to_str().unwrap()).unwrap();
            assert!(value["data"].is_null());
            buaa_core_string_free(result);
            buaa_store_close(handle);
        }
        std::fs::remove_file(path).unwrap();
    }
}

/// Open an exclusive temporary PDF path. Returns null on failure.
/// # Safety
/// Both strings must be valid null-terminated UTF-8 for the call.
#[no_mangle]
pub unsafe extern "C" fn buaa_pdf_create(
    path: *const c_char,
    snapshot: *const c_char,
) -> *mut crate::slide_pdf::SlidePdf {
    std::panic::catch_unwind(|| {
        let path = input(path).ok()?;
        let snapshot = input(snapshot).ok()?;
        crate::slide_pdf::SlidePdf::create(std::path::Path::new(path), snapshot)
            .ok()
            .map(|pdf| Box::into_raw(Box::new(pdf)))
    })
    .ok()
    .flatten()
    .unwrap_or(std::ptr::null_mut())
}

/// Append an image to a temporary PDF.
/// # Safety
/// Use an exclusive live handle and length readable bytes.
#[no_mangle]
pub unsafe extern "C" fn buaa_pdf_add(
    handle: *mut crate::slide_pdf::SlidePdf,
    bytes: *const u8,
    length: u32,
    seconds: u64,
) -> *mut c_char {
    use crate::slide_pdf::Error;
    if handle.is_null()
        || bytes.is_null()
        || length == 0
        || length as usize > crate::slide_image::MAX_BYTES
    {
        return string_result(Err::<Value, _>(Error::Image));
    }
    let bytes = std::slice::from_raw_parts(bytes, length as usize);
    string_result(
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            (&mut *handle).add(bytes, seconds).map(|_| Value::Null)
        }))
        .unwrap_or(Err(Error::Image)),
    )
}

/// Finish the temporary PDF. Release the handle before publishing the file.
/// # Safety
/// Use an exclusive live handle. Call once after all images succeed.
#[no_mangle]
pub unsafe extern "C" fn buaa_pdf_finish(handle: *mut crate::slide_pdf::SlidePdf) -> *mut c_char {
    use crate::slide_pdf::Error;
    if handle.is_null() {
        return string_result(Err::<Value, _>(Error::Image));
    }
    string_result(
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            (&mut *handle).finish().map(|pages| json!(pages))
        }))
        .unwrap_or(Err(Error::Image)),
    )
}

/// Release a PDF handle. The caller owns temporary file cleanup.
/// # Safety
/// Pass null or an exclusively owned handle exactly once.
#[no_mangle]
pub unsafe extern "C" fn buaa_pdf_close(handle: *mut crate::slide_pdf::SlidePdf) {
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }
}

type AutomationCallback = unsafe extern "C" fn(*mut std::ffi::c_void, *const c_char) -> *mut c_char;
type AutomationRelease = unsafe extern "C" fn(*mut std::ffi::c_void, *mut c_char);

struct ForeignAutomation {
    context: *mut std::ffi::c_void,
    send: AutomationCallback,
    release: AutomationRelease,
}

impl ForeignAutomation {
    fn call<T: serde::de::DeserializeOwned>(
        &self,
        request: Value,
    ) -> Result<T, crate::automation_execution::RunError> {
        use crate::automation_execution::RunError;
        let request = CString::new(request.to_string()).map_err(|_| RunError::Service)?;
        let result = unsafe { (self.send)(self.context, request.as_ptr()) };
        if result.is_null() {
            return Err(RunError::Service);
        }
        let value = unsafe { CStr::from_ptr(result) }
            .to_str()
            .ok()
            .and_then(|value| serde_json::from_str::<T>(value).ok());
        unsafe { (self.release)(self.context, result) };
        value.ok_or(RunError::Service)
    }
}

impl crate::automation_execution::AutomationServices for ForeignAutomation {
    fn load_rule(
        &mut self,
        course_id: &str,
    ) -> Result<Option<crate::automation::Rule>, crate::automation_execution::RunError> {
        self.call(json!({"method":"load","id":course_id}))
    }
    fn save_rule(
        &mut self,
        rule: &crate::automation::Rule,
    ) -> Result<(), crate::automation_execution::RunError> {
        self.call(json!({"method":"save","rule":rule}))
    }
    fn sync_lessons(
        &mut self,
        course_id: &str,
    ) -> Result<Vec<crate::Lesson>, crate::automation_execution::RunError> {
        self.call(json!({"method":"catalogue","id":course_id}))
    }
    fn fetch_materials(
        &mut self,
        lesson_id: &str,
    ) -> Result<
        crate::automation_execution::MaterialAvailability,
        crate::automation_execution::RunError,
    > {
        let (text_segments, slides) = self.call(json!({"method":"materials","id":lesson_id}))?;
        Ok(crate::automation_execution::MaterialAvailability {
            text_segments,
            slides,
        })
    }
    fn analyze_lesson(
        &mut self,
        lesson_id: &str,
    ) -> Result<(), crate::automation_execution::RunError> {
        self.call(json!({"method":"analyze","id":lesson_id}))
    }
}

/// Run one course using synchronous, account-scoped host callbacks.
/// # Safety
/// Inputs must remain valid until return. The host releases callback results;
/// the caller releases the final envelope with buaa_core_string_free.
#[no_mangle]
pub unsafe extern "C" fn buaa_core_run_automation(
    context: *mut std::ffi::c_void,
    send: Option<AutomationCallback>,
    release: Option<AutomationRelease>,
    course: *const c_char,
    rows: *const c_char,
    current: *const c_char,
) -> *mut c_char {
    use crate::automation_execution::RunError;
    let run = || -> Result<Value, RunError> {
        let invalid = |_| RunError::InvalidRule;
        let course = input(course).map_err(invalid)?;
        let rows = serde_json::from_str::<Vec<crate::automation::TimetableRow>>(
            input(rows).map_err(invalid)?,
        )
        .map_err(|_| RunError::InvalidRule)?;
        let now = chrono::DateTime::parse_from_rfc3339(input(current).map_err(invalid)?)
            .map_err(|_| RunError::InvalidRule)?;
        let mut host = ForeignAutomation {
            context,
            send: send.ok_or(RunError::Service)?,
            release: release.ok_or(RunError::Service)?,
        };
        let outcome = crate::automation_execution::run(&mut host, course, &rows, now)?;
        Ok(json!({"processed":outcome.processed,"pending_count":outcome.pending_count}))
    };
    string_result(std::panic::catch_unwind(run).unwrap_or(Err(RunError::Service)))
}

/// Snapshot a database using SQLite's backup API.
/// # Safety
/// Both paths must be valid null-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn buaa_legacy_snapshot(
    source: *const c_char,
    destination: *const c_char,
    require_idle: u32,
) -> *mut c_char {
    let run = || -> Result<Value, crate::store::StoreError> {
        use crate::store::StoreError;
        let source = std::path::Path::new(input(source).map_err(|_| StoreError::InvalidPath)?);
        let destination =
            std::path::Path::new(input(destination).map_err(|_| StoreError::InvalidPath)?);
        crate::legacy_windows::snapshot(source, destination, require_idle != 0)?;
        Ok(Value::Null)
    };
    string_result(
        std::panic::catch_unwind(run).unwrap_or(Err(crate::store::StoreError::UnsafeImport)),
    )
}

/// Read one legacy owner with no settings or other accounts.
/// # Safety
/// Both strings must be valid null-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn buaa_legacy_rows(
    source: *const c_char,
    owner: *const c_char,
) -> *mut c_char {
    let run = || -> Result<Value, crate::store::StoreError> {
        use crate::store::StoreError;
        let source = std::path::Path::new(input(source).map_err(|_| StoreError::InvalidPath)?);
        let owner = input(owner).map_err(|_| StoreError::InvalidIdentifier)?;
        crate::legacy_windows::rows(source, owner)
    };
    string_result(
        std::panic::catch_unwind(run).unwrap_or(Err(crate::store::StoreError::UnsafeImport)),
    )
}
