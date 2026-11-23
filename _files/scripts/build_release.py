from pathlib import Path
import hashlib
import json
import shutil
import sys

sys.stdout.reconfigure(encoding="utf-8")
import argparse
import os
import subprocess
parser = argparse.ArgumentParser(description="Build Mason's runtime-only installation package.")
parser.add_argument("--output-dir", required=True)
parser.add_argument("--no-build", action="store_true")
args = parser.parse_args()
development = Path(__file__).resolve().parents[2]
stage = Path(args.output_dir).resolve()
if stage.exists():
    raise RuntimeError("Output directory already exists; preserve it and choose a new path.")
version = json.loads((development / "_files/ui/project-info.json").read_text(encoding="utf-8-sig"))["version"]
if not args.no_build:
    powershell = Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    subprocess.run([str(powershell), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", str(development / "_files/scripts/build_native_text.ps1"),
                    "-PackageDir", str(development), "-BuildDir", str(development / "build/native-text")],
                   creationflags=subprocess.CREATE_NO_WINDOW, check=True)
    for script in ("build_settings.ps1", "build_installer.ps1"):
        subprocess.run([str(powershell), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", str(development / "_files/scripts" / script)],
                       creationflags=subprocess.CREATE_NO_WINDOW, check=True)
assert not stage.exists(), "Preserve any existing runtime package; do not overwrite it."
ui_names = {
    "Mason.Settings.exe", "Mason.Settings.exe.config", "Mason.Deployer.exe", "Mason.Deployer.exe.config",
    "Mason.Server.exe", "Mason.Server.exe.config", "Mason.Compatibility.exe", "Mason.InputSetup.exe",
    "Mason.NativeTools.dll", "Mason.ico", "default-appearance.yaml", "project-info.json"
}
script_names = {
    "settings_backend.py", "settings_core.py", "mason_paths.py", "fuzzy_settings.py", "correction_settings.py", "dictionary_updater.py",
    "github_updates.py", "application_updater.py", "update_state.py", "windows_notifications.py", "mason_notify.ps1",
    "_migrate_layout.ps1", "_settings_entry.ps1", "_menu_protocol.ps1", "_apply_ui.ps1", "_tray_menu.ps1",
    "_native_ui.ps1", "_brand_windows.ps1", "_refresh_taskbar.ps1"
}
root_names = {"Mason.Install.exe", "Mason.Install.exe.config", "使用说明.md", "LICENSE", "THIRD_PARTY_NOTICES.md"}
kept, excluded = [], []


def include(relative):
    parts = relative.parts
    if len(parts) == 1:
        return relative.name in root_names
    if parts[0] != "_files":
        return False
    if len(parts) == 2:
        return relative.name == "weasel-0.17.4.0-installer.exe"
    if parts[1] == "rime":
        return relative.name != "README.md"
    if parts[1] == "cloud":
        return relative.name != "README.md"
    if parts[1] == "native":
        return parts[2] in {"x64", "x86"} and relative.name in {"Mason.Menu.dll", "Mason.dll"}
    if parts[1] == "scripts":
        return len(parts) == 3 and relative.name in script_names
    if parts[1] == "ui":
        return len(parts) == 3 and relative.name in ui_names or len(parts) == 4 and parts[2] == "icons" and relative.name in {"appearance.svg", "input.svg", "guide.svg"}
    return False


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


for path in sorted(development.rglob("*")):
    if not path.is_file() or "__pycache__" in path.parts or path.suffix == ".pyc":
        continue
    relative = path.relative_to(development)
    if include(relative):
        target = stage / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        kept.append(dict(Path=relative.as_posix(), Sha256=sha(target), Bytes=target.stat().st_size))
    elif relative.as_posix() != "_files/install-manifest.json":
        excluded.append(dict(path=relative.as_posix(), bytes=path.stat().st_size))
assert root_names <= {item["Path"] for item in kept}
assert all((stage / "_files/scripts" / name).is_file() for name in script_names)
assert all((stage / "_files/ui" / name).is_file() for name in ui_names)
assert (stage / "_files/weasel-0.17.4.0-installer.exe").is_file()
manifest = dict(Product="Mason", Version=version, Files=kept)
text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
(stage / "_files/install-manifest.json").write_text(text, encoding="utf-8")
(development / "_files/install-manifest.json").write_text(text, encoding="utf-8")
assert not list(stage.rglob("*.cs")) and not list(stage.rglob("*.bat")) and not list(stage.rglob("build_*.ps1"))
assert not (stage / "_files/ui/Mason.Tray.exe").exists()
for path in (stage / "_files/scripts").glob("*.ps1"):
    assert ".cs')" not in path.read_text(encoding="utf-8-sig"), path.name
report = dict(status="prepared", version=version, kept_files=len(kept) + 1, excluded_files=len(excluded),
              excluded_bytes=sum(item["bytes"] for item in excluded), excluded=excluded,
              cleanup_method="allow-list copy; no file deletion", complete_engine_dictionary_cloud=True)
print(json.dumps({key: value for key, value in report.items() if key != "excluded"}, ensure_ascii=False))
