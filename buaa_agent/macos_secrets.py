"""Store macOS desktop model credentials in the user's Keychain."""
import ctypes as c
import hashlib
import re
import uuid

_SERVICE = b"io.github.fyzzzz05.buaa-learn.desktop.model"
_NOT_FOUND = -25300


def _frameworks():
    security = c.CDLL("/System/Library/Frameworks/Security.framework/Security")
    foundation = c.CDLL("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")
    pointer = c.POINTER(c.c_void_p)
    uint_pointer = c.POINTER(c.c_uint32)
    identity = [c.c_void_p, c.c_uint32, c.c_char_p, c.c_uint32, c.c_char_p]
    signatures = {
        "SecKeychainAddGenericPassword": [*identity, c.c_uint32, c.c_char_p, pointer],
        "SecKeychainFindGenericPassword": [*identity, uint_pointer, pointer, pointer],
        "SecKeychainItemFreeContent": [c.c_void_p, c.c_void_p],
        "SecKeychainItemDelete": [c.c_void_p],
    }
    for name, arguments in signatures.items():
        function = getattr(security, name)
        function.argtypes = arguments
        function.restype = c.c_int32
    foundation.CFRelease.argtypes = [c.c_void_p]
    foundation.CFRelease.restype = None
    return security, foundation


def _identity(reference, endpoint):
    if not isinstance(reference, str) or not re.fullmatch(r"keychain:[0-9a-f]{32}", reference):
        raise ValueError("Invalid Keychain reference")
    account = hashlib.sha256((endpoint + "\0" + reference).encode()).hexdigest().encode()
    return None, len(_SERVICE), _SERVICE, len(account), account


def _check(status):
    if status:
        raise OSError(status, "Keychain operation failed")


def protect(key, endpoint):
    security, _ = _frameworks()
    reference = "keychain:" + uuid.uuid4().hex
    data = key.encode()
    _check(security.SecKeychainAddGenericPassword(
        *_identity(reference, endpoint), len(data), data, None))
    return reference


def unprotect(reference, endpoint):
    security, _ = _frameworks()
    size, data = c.c_uint32(), c.c_void_p()
    _check(security.SecKeychainFindGenericPassword(
        *_identity(reference, endpoint), c.byref(size), c.byref(data), None))
    try:
        return c.string_at(data, size.value).decode()
    finally:
        security.SecKeychainItemFreeContent(None, data)


def discard(reference, endpoint):
    security, foundation = _frameworks()
    item = c.c_void_p()
    status = security.SecKeychainFindGenericPassword(
        *_identity(reference, endpoint), None, None, c.byref(item))
    if status == _NOT_FOUND:
        return
    _check(status)
    try:
        _check(security.SecKeychainItemDelete(item))
    finally:
        foundation.CFRelease(item)
