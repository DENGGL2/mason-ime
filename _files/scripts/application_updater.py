"""Download and verify a Mason release before handing off to its GUI installer."""
from pathlib import Path, PurePosixPath
import hashlib
import http.client
import json
import os
import re
import stat
import subprocess
import time
import urllib.error
import uuid
import zipfile

import github_updates
import update_state

MAX_DOWNLOAD = 256 * 1024 * 1024
MAX_EXPANDED = 1024 * 1024 * 1024
MAX_FILES = 2500


def check(info, *, progress=lambda message: None, release_provider=None):
    repository = str(info.get("github_url") or "").strip()
    github_updates.repository_parts(repository)
    release_provider = release_provider or (lambda: github_updates.latest_release(
        repository, lambda version: "Mason-" + version + "-Windows.zip", progress=progress))
    release = release_provider()
    latest = release["tag"].removeprefix("v")
    current = str(info["version"])
    available = github_updates.version_tuple(latest) > github_updates.version_tuple(current)
    message = ("发现新版本 " + latest + "，当前版本 " + current + "。" if available
               else "当前版本 " + current + " 已是最新。")
    return {"has_update": available, "current_version": current, "latest_version": latest,
            "message": message, "release": release}


def _sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(release, folder, progress):
    total = int(release.get("size") or 0)
    if total < 0 or total > MAX_DOWNLOAD:
        raise RuntimeError("更新包大小超出预期。")
    expected = str(release.get("digest") or "").removeprefix("sha256:")
    if not re.fullmatch(r"[0-9a-f]{64}", expected):
        raise RuntimeError("更新包缺少 SHA-256 校验信息，请稍后重新检查更新。")
    for attempt in range(3):
        received = 0
        started = time.monotonic()
        last_emit = started - 1
        last_bytes = 0
        last_time = started
        speed = 0.0
        partial = folder / (release["asset_name"] + ".part-" + str(attempt + 1))
        digest = hashlib.sha256()
        try:
            with github_updates.open_retry(github_updates.request(release["url"], binary=True), timeout=15,
                                           progress=lambda message: progress({"phase": "download", "message": message})) as response, partial.open("xb") as output:
                reported = int(response.headers.get("Content-Length") or 0)
                if reported > MAX_DOWNLOAD or total and reported and total != reported:
                    raise RuntimeError("下载大小与发布信息不一致。")
                total = total or reported
                while True:
                    chunk = response.read(64 * 1024)
                    if not chunk:
                        break
                    received += len(chunk)
                    if received > MAX_DOWNLOAD or total and received > total:
                        raise RuntimeError("更新包大小超出预期。")
                    output.write(chunk)
                    digest.update(chunk)
                    now = time.monotonic()
                    if now - last_emit >= .12:
                        instant = (received - last_bytes) / max(.01, now - last_time)
                        speed = instant if not speed else .35 * instant + .65 * speed
                        progress({"phase": "download", "message": "正在下载更新…", "received": received,
                                  "total": total, "bytes_per_second": speed,
                                  "percent": received * 100 / total if total else None})
                        last_bytes, last_time, last_emit = received, now, now
            if total and received != total:
                raise github_updates.NetworkError("更新包下载不完整，正在重新下载。")
            if digest.hexdigest() != expected:
                raise RuntimeError("更新包 SHA-256 校验失败，尚未运行安装程序。")
            archive = folder / release["asset_name"]
            os.replace(partial, archive)
            progress({"phase": "verify", "message": "下载完成，正在校验安装材料…", "received": received,
                      "total": total, "bytes_per_second": 0, "percent": 100})
            return archive, digest.hexdigest()
        except (github_updates.NetworkError, urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException) as error:
            if attempt == 2:
                raise RuntimeError("更新包下载未完成，已重试。可以点击重试继续更新。") from error
            progress({"phase": "download", "message": "下载中断，正在重试… " + str(attempt + 2) + "/3",
                      "received": 0, "total": total, "bytes_per_second": 0, "percent": 0})
            time.sleep(.5 * (attempt + 1))
    raise RuntimeError("更新包下载未完成。")


def _relative(value):
    if not isinstance(value, str) or "\\" in value or ":" in value:
        raise RuntimeError("更新包包含无效文件路径。")
    parts = value.split("/")
    if not parts or any(not part or part in (".", "..") or part.endswith((".", " "))
                        or re.search(r'[<>"|?*\x00-\x1f]', part)
                        or re.match(r"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", part, re.I) for part in parts):
        raise RuntimeError("更新包包含无效文件路径。")
    return PurePosixPath(value)


def verify_and_extract(archive_path, folder, version, progress):
    stage = folder / "Mason"
    stage.mkdir(exist_ok=False)
    with zipfile.ZipFile(archive_path) as archive:
        files = [entry for entry in archive.infolist() if not entry.is_dir()]
        installers = [entry for entry in files if PurePosixPath(entry.filename).name == "Mason.Install.exe"]
        if len(installers) != 1 or len(files) > MAX_FILES:
            raise RuntimeError("更新包结构不符合预期。")
        prefix = installers[0].filename[:-len("Mason.Install.exe")]
        entries = {}
        normalized = set()
        expanded = 0
        for entry in files:
            if not entry.filename.startswith(prefix):
                raise RuntimeError("更新包文件超出安装目录。")
            relative = entry.filename[len(prefix):]
            _relative(relative)
            if relative.casefold() in normalized or stat.S_ISLNK(entry.external_attr >> 16):
                raise RuntimeError("更新包包含重复文件或文件链接。")
            normalized.add(relative.casefold())
            entries[relative] = entry
            expanded += entry.file_size
        if expanded > MAX_EXPANDED:
            raise RuntimeError("更新包解压大小超出预期。")
        manifest_name = "_files/install-manifest.json"
        if manifest_name not in entries or entries[manifest_name].file_size > 8 * 1024 * 1024:
            raise RuntimeError("更新包缺少安装清单。")
        manifest = json.loads(archive.read(entries[manifest_name]).decode("utf-8-sig"))
        declared = manifest.get("Files")
        if manifest.get("Product") != "Mason" or manifest.get("Version") != version or not isinstance(declared, list) or len(declared) < 3:
            raise RuntimeError("更新包版本或安装清单不符合预期。")
        declared_names = set()
        for item in declared:
            relative = item.get("Path")
            _relative(relative)
            if relative in declared_names or relative not in entries or entries[relative].file_size != item.get("Bytes"):
                raise RuntimeError("更新包文件与清单不一致。")
            if not re.fullmatch(r"[0-9a-f]{64}", str(item.get("Sha256") or "")):
                raise RuntimeError("更新包文件缺少校验信息。")
            declared_names.add(relative)
        if declared_names | {manifest_name} != set(entries) or "Mason.Install.exe" not in declared_names:
            raise RuntimeError("更新包存在未声明的文件。")
        done = 0
        for relative, entry in entries.items():
            destination = stage / _relative(relative)
            destination.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, destination.open("xb") as output:
                while True:
                    chunk = source.read(1024 * 1024)
                    if not chunk:
                        break
                    output.write(chunk)
                    done += len(chunk)
            progress({"phase": "verify", "message": "正在校验安装材料…", "received": done,
                      "total": expanded, "bytes_per_second": 0, "percent": done * 100 / expanded if expanded else 100})
        for item in declared:
            if _sha(stage / _relative(item["Path"])) != item["Sha256"]:
                raise RuntimeError("安装材料校验失败：" + item["Path"])
        product = json.loads((stage / "_files/ui/project-info.json").read_text(encoding="utf-8-sig"))
        if product.get("product") != "Mason" or product.get("version") != version:
            raise RuntimeError("更新包设置版本不一致。")
    return stage


def prepare(info, user_dir, app_dir, expected_version, *, progress=lambda item: None,
            release_provider=None, download_fn=download):
    result = check(info, release_provider=release_provider,
                   progress=lambda message: progress({"phase": "check", "message": message}))
    if not result["has_update"]:
        raise RuntimeError(result["message"])
    if result["latest_version"] != expected_version:
        raise RuntimeError("最新版本已变化，请重新检查更新。")
    release = result["release"]
    github_updates.validate_asset_url(release["url"], info["github_url"], release["tag"])
    run = Path(user_dir).resolve() / "更新缓存" / (expected_version + "-" + uuid.uuid4().hex[:12])
    run.mkdir(parents=True, exist_ok=False)
    archive, digest = download_fn(release, run, progress)
    stage = verify_and_extract(archive, run, expected_version, progress)
    installer = stage / "Mason.Install.exe"
    value = {"version": expected_version, "installer": str(installer), "installer_sha256": _sha(installer),
             "source_dir": str(stage), "app_dir": str(Path(app_dir).resolve()), "archive_sha256": digest,
             "bytes": archive.stat().st_size, "prepared_at": update_state.now_text()}
    ready = run / "ready.json"
    update_state.atomic_text(ready, json.dumps(value, ensure_ascii=False, indent=2))
    value["prepared_path"] = str(ready)
    return value


def launch(user_dir, app_dir, prepared_path, settings_pid, *, launcher=None):
    cache = Path(user_dir).resolve() / "更新缓存"
    ready = Path(prepared_path).resolve()
    if not ready.is_relative_to(cache) or ready.name != "ready.json":
        raise RuntimeError("更新任务位置无效。")
    value = json.loads(ready.read_text(encoding="utf-8-sig"))
    source = (ready.parent / "Mason").resolve()
    installer = source / "Mason.Install.exe"
    if (Path(value["source_dir"]).resolve() != source or Path(value["installer"]).resolve() != installer
            or Path(value["app_dir"]).resolve() != Path(app_dir).resolve()
            or _sha(installer) != value["installer_sha256"]):
        raise RuntimeError("更新任务校验失败，请重新下载。")
    if not isinstance(settings_pid, int) or settings_pid <= 0:
        raise RuntimeError("设置窗口进程无效。")
    arguments = [str(installer), "--update", "--target", str(Path(app_dir).resolve()), "--wait-pid", str(settings_pid)]
    process = (launcher or subprocess.Popen)(arguments, cwd=str(source))
    return {"process_id": process.pid, "version": value["version"], "app_dir": str(app_dir)}
