//! C bridge for school networking using an injected, redirect-free HTTPS transport.
use crate::network::{Method, NetworkError, Request, Response, SchoolSession, Transport};
use serde_json::{json, Value};
use std::{
    ffi::{c_void, CStr, CString},
    os::raw::c_char,
    sync::Mutex,
};

type SendFn = unsafe extern "C" fn(*mut c_void, *const c_char) -> *mut c_char;
type ReleaseFn = unsafe extern "C" fn(*mut c_void, *mut c_char);

struct ForeignTransport {
    context: *mut c_void,
    send: SendFn,
    release: ReleaseFn,
}
impl Transport for ForeignTransport {
    fn send(&self, request: &Request) -> Result<Response, NetworkError> {
        let headers: serde_json::Map<String, Value> = request
            .headers
            .iter()
            .map(|(key, value)| (key.clone(), json!(value)))
            .collect();
        let data = json!({"url":request.url,"method":if request.method == Method::Post {"POST"} else {"GET"},"headers":headers,"body":request.body});
        let request = CString::new(data.to_string()).map_err(|_| NetworkError::InvalidResponse)?;
        let response = unsafe { (self.send)(self.context, request.as_ptr()) };
        if response.is_null() {
            return Err(NetworkError::Transport);
        }
        let result = unsafe { CStr::from_ptr(response) }
            .to_str()
            .ok()
            .and_then(|text| serde_json::from_str::<Value>(text).ok());
        unsafe { (self.release)(self.context, response) };
        let data = result.ok_or(NetworkError::Transport)?;
        let status = data
            .get("status")
            .and_then(Value::as_u64)
            .and_then(|v| u16::try_from(v).ok())
            .ok_or(NetworkError::Transport)?;
        let body = data
            .get("body")
            .and_then(Value::as_str)
            .ok_or(NetworkError::Transport)?;
        Ok(Response {
            status,
            body: body.into(),
        })
    }
}

pub struct CoreSchool(Mutex<SchoolSession<ForeignTransport>>);

/// Return sensitive credentials for the host's OS credential store. Never log the result.
/// # Safety
/// Handle must be live. Free the returned string with buaa_core_string_free.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_credentials(handle: *mut CoreSchool) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        Ok(session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .credentials())
    })())
}
unsafe fn read<'a>(pointer: *const c_char) -> Result<&'a str, NetworkError> {
    if pointer.is_null() {
        return Err(NetworkError::InvalidResponse);
    }
    CStr::from_ptr(pointer)
        .to_str()
        .map_err(|_| NetworkError::InvalidResponse)
}
fn output(result: Result<Value, NetworkError>) -> *mut c_char {
    let body = match result {
        Ok(data) => json!({"ok":true,"data":data}),
        Err(error) => {
            let code = match error {
                NetworkError::LoginRequired => "login_required",
                NetworkError::RecordingAuth => "recording_auth",
                NetworkError::Transport => "school_transport",
                _ => "school_error",
            };
            json!({"ok":false,"error":error.to_string(),"error_code":code})
        }
    };
    CString::new(body.to_string())
        .expect("JSON escapes NUL")
        .into_raw()
}

/// Open a transient school session. The host must retain `context` until close.
///
/// # Safety
/// Text arguments must be valid NUL-terminated UTF-8, callbacks must be live
/// for the session lifetime, and the transport must reject redirects and verify TLS.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_open(
    context: *mut c_void,
    send: Option<SendFn>,
    release: Option<ReleaseFn>,
    token: *const c_char,
    refresh: *const c_char,
    role: *const c_char,
) -> *mut CoreSchool {
    let (Some(send), Some(release), Ok(token), Ok(refresh), Ok(role)) =
        (send, release, read(token), read(refresh), read(role))
    else {
        return std::ptr::null_mut();
    };
    let transport = ForeignTransport {
        context,
        send,
        release,
    };
    match SchoolSession::new(transport, token.into(), refresh.into(), role.into()) {
        Ok(session) => Box::into_raw(Box::new(CoreSchool(Mutex::new(session)))),
        Err(_) => std::ptr::null_mut(),
    }
}
/// Set the separate recording token. Free the result with `buaa_core_string_free`.
///
/// # Safety
/// Handle must be live; token must be a valid NUL-terminated UTF-8 string.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_set_recording(
    handle: *mut CoreSchool,
    token: *const c_char,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .set_recording_token(read(token)?.into())?;
        Ok(Value::Null)
    })())
}
/// Read available semesters. Free the result with `buaa_core_string_free`.
///
/// # Safety
/// Handle must be live; its callbacks and context must remain valid.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_semesters(handle: *mut CoreSchool) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        Ok(json!(session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .semesters()?))
    })())
}
/// Read student courses for the chosen semester. Free the returned string.
///
/// # Safety
/// Handle must be live; semester must be valid NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_courses(
    handle: *mut CoreSchool,
    semester: *const c_char,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        Ok(json!(session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .courses(read(semester)?)?))
    })())
}

/// Return a transient signed recording entry. Never log or persist the result.
/// # Safety
/// Handle and NUL-terminated arguments must remain valid throughout the call.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_recording_signature(
    handle: *mut CoreSchool,
    course: *const c_char,
    task: *const c_char,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .recording_signature(read(course)?, read(task)?)
    })())
}
/// Read dated timetable rows. Free the returned string.
///
/// # Safety
/// Handle must be live; dates must be valid NUL-terminated UTF-8 strings.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_schedule(
    handle: *mut CoreSchool,
    start: *const c_char,
    end: *const c_char,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        Ok(json!(session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .schedule(read(start)?, read(end)?)?))
    })())
}
/// Read recording catalogue. Free the returned string.
///
/// # Safety
/// Handle must be live; course ID must be valid NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_catalogue(
    handle: *mut CoreSchool,
    course: *const c_char,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        Ok(json!(session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .catalogue(read(course)?)?))
    })())
}
/// Read transcript and PPT indexes. Free the returned string.
///
/// # Safety
/// Handle must be live; both IDs must be valid NUL-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_materials(
    handle: *mut CoreSchool,
    course: *const c_char,
    lesson: *const c_char,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        let material = session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .materials(read(course)?, read(lesson)?)?;
        Ok(json!(material))
    })())
}

/// Read material using the synchronized lesson duration when the service omits it.
/// # Safety
/// Handle and IDs must remain valid for the call. Free the returned string.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_materials_with_duration(
    handle: *mut CoreSchool,
    course: *const c_char,
    lesson: *const c_char,
    duration: f64,
) -> *mut c_char {
    output((|| {
        let session = handle.as_ref().ok_or(NetworkError::LoginRequired)?;
        let material = session
            .0
            .lock()
            .map_err(|_| NetworkError::Transport)?
            .materials_with_duration(read(course)?, read(lesson)?, duration)?;
        Ok(json!(material))
    })())
}
/// Close the session. Null is accepted.
///
/// # Safety
/// A non-null handle must be returned by `buaa_school_open`, closed once, and
/// not be in use by another thread. Release the transport context afterwards.
#[no_mangle]
pub unsafe extern "C" fn buaa_school_close(handle: *mut CoreSchool) {
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }
}
