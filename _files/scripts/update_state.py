"""Shared update availability, notification history and native-menu read state."""
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import ctypes
import hashlib
import json
import os
from pathlib import Path
import uuid

import github_updates

STATE_NAME = "mason-update-state.json"
STATUS_NAME = "mason-update-status.ini"
SEEN_NAME = "mason-update-seen.ini"
SHANGHAI = timezone(timedelta(hours=8))


def now_text():
    return datetime.now(SHANGHAI).strftime("%Y-%m-%d %H:%M")


@contextmanager
def state_lock(root):
    if os.name != "nt":
        yield
        return
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
    kernel.CreateMutexW.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    kernel.ReleaseMutex.argtypes = [ctypes.c_void_p]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    scope = hashlib.sha256(str(Path(root).resolve()).casefold().encode("utf-8")).hexdigest()[:24]
    handle = kernel.CreateMutexW(None, False, "Local\\Mason.UpdateState." + scope)
    if not handle:
        raise OSError("无法读取更新记录。")
    acquired = False
    try:
        acquired = kernel.WaitForSingleObject(handle, 8000) in (0, 0x80)
        if not acquired:
            raise RuntimeError("更新记录正在写入，请稍后重试。")
        yield
    finally:
        if acquired:
            kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)


def read(root):
    try:
        value = json.loads((Path(root) / STATE_NAME).read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def atomic_text(path, text, encoding="utf-8"):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".new-" + uuid.uuid4().hex)
    temporary.write_text(text, encoding=encoding)
    os.replace(temporary, path)


def _save(root, value):
    root = Path(root)
    atomic_text(root / STATE_NAME, json.dumps(value, ensure_ascii=False, indent=2))
    lines = ["[updates]"]
    for kind in ("application", "dictionary"):
        item = value.get(kind) or {}
        prefix = "app" if kind == "application" else "dictionary"
        version = str(item.get("version") or "")
        if version:
            github_updates.version_tuple(version)
        lines += [prefix + "_available=" + ("1" if item.get("available") else "0"),
                  prefix + "_version=" + version]
    atomic_text(root / STATUS_NAME, "\r\n".join(lines) + "\r\n", encoding="ascii")


def record(root, kind, release, available):
    if kind not in ("application", "dictionary"):
        raise ValueError("Invalid update kind")
    version = release["tag"].removeprefix("v")
    github_updates.version_tuple(version)
    with state_lock(root):
        value = read(root)
        previous = value.get(kind) or {}
        value[kind] = {"version": version, "available": bool(available), "checked_at": now_text(),
                       "release": release, "notified_version": previous.get("notified_version", "")}
        _save(root, value)
    return value[kind]


def refresh_local(root, current_version, dictionary_version=""):
    with state_lock(root):
        value = read(root)
        changed = False
        application = value.get("application")
        if application and application.get("version"):
            available = github_updates.version_tuple(application["version"]) > github_updates.version_tuple(current_version)
            if available != bool(application.get("available")):
                application["available"] = available
                changed = True
        dictionary = value.get("dictionary")
        if dictionary and dictionary_version and dictionary.get("version") == dictionary_version.removeprefix("v"):
            if dictionary.get("available"):
                dictionary["available"] = False
                changed = True
        if changed:
            _save(root, value)
    return flags(root, current_version)


def flags(root, current_version=None):
    value = read(root)
    application = value.get("application") or {}
    dictionary = value.get("dictionary") or {}
    app_available = bool(application.get("available"))
    if current_version and application.get("version"):
        app_available = github_updates.version_tuple(application["version"]) > github_updates.version_tuple(current_version)
    return {"app_available": app_available, "dictionary_available": bool(dictionary.get("available")),
            "app_version": application.get("version", ""), "dictionary_version": dictionary.get("version", "")}


def claim_background_check(root):
    with state_lock(root):
        value = read(root)
        now = datetime.now(SHANGHAI)
        try:
            previous = datetime.strptime(value.get('background_checked_at', ''), '%Y-%m-%d %H:%M').replace(tzinfo=SHANGHAI)
            if timedelta(0) <= now - previous < timedelta(hours=6):
                return False
        except ValueError:
            pass
        value['background_checked_at'] = now_text()
        _save(root, value)
    return True


def notify_once(root, kind, notify):
    with state_lock(root):
        value = read(root)
        item = value.get(kind) or {}
        if not item.get("available") or item.get("notified_version") == item.get("version"):
            return False
        if not notify(kind, item["version"]):
            return False
        item["notified_version"] = item["version"]
        _save(root, value)
        return True
