"""Mason-owned Windows toasts; protocol activation opens the existing settings window."""
import json
import os
from pathlib import Path
import subprocess
import uuid

import update_state


def show(app_dir, user_dir, kind, version):
    if os.name != "nt" or kind not in ("application", "dictionary"):
        return False
    app = Path(app_dir).resolve()
    settings = app / "程序/资源/ui/Mason.Settings.exe"
    if not settings.is_file() or not (app / "mason-install.json").is_file():
        return False
    powershell = Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    script = Path(__file__).with_name("mason_notify.ps1")
    request = Path(user_dir) / "更新缓存" / ("通知-" + uuid.uuid4().hex + ".json")
    value = {"app_dir": str(app), "settings": str(settings), "kind": kind, "version": version,
             "title": "麻绳输入法有新版本" if kind == "application" else "麻绳输入法词库可以更新",
             "message": ("Mason " + version + " 已发布，点击查看更新。" if kind == "application"
                         else "发现新版词库 " + version + "，点击打开词库更新。")}
    update_state.atomic_text(request, json.dumps(value, ensure_ascii=False))
    process = subprocess.run([str(powershell), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                              "-File", str(script), "-RequestPath", str(request)],
                             creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True, timeout=20)
    return process.returncode == 0
