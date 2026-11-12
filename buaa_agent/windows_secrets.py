"""Current-user DPAPI for the Windows desktop backend."""
import base64
import ctypes
from ctypes import wintypes


class Blob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]


def _transform(data: bytes, endpoint: str, decrypt: bool) -> bytes:
    crypt = ctypes.WinDLL("crypt32.dll", use_last_error=True, winmode=0x800)
    kernel = ctypes.WinDLL("kernel32.dll", use_last_error=True, winmode=0x800)
    local_free = kernel.LocalFree
    local_free.argtypes = [ctypes.c_void_p]
    local_free.restype = ctypes.c_void_p
    source = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    context = ("BUAA Learn model key v1:" + endpoint).encode("utf-8")
    entropy = (ctypes.c_ubyte * len(context)).from_buffer_copy(context)
    incoming, scope, outgoing = Blob(len(data), source), Blob(len(context), entropy), Blob()
    call = crypt.CryptUnprotectData if decrypt else crypt.CryptProtectData
    call.argtypes = [
        ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.POINTER(Blob),
        ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob),
    ]
    call.restype = wintypes.BOOL
    try:
        # UI forbidden, current-user scope.
        if not call(ctypes.byref(incoming), None, ctypes.byref(scope), None, None, 1, ctypes.byref(outgoing)):
            raise ctypes.WinError(ctypes.get_last_error())
        return ctypes.string_at(outgoing.data, outgoing.size)
    finally:
        ctypes.memset(source, 0, len(data))
        if outgoing.data:
            ctypes.memset(outgoing.data, 0, outgoing.size)
            local_free(outgoing.data)


def protect(key: str, endpoint: str) -> str:
    return base64.b64encode(_transform(key.encode("utf-8"), endpoint, False)).decode("ascii")


def unprotect(ciphertext: str, endpoint: str) -> str:
    return _transform(base64.b64decode(ciphertext, validate=True), endpoint, True).decode("utf-8")
