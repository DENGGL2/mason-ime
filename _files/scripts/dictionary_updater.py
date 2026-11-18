"""白霜发布版词库更新。保存备份、校验内容，不删除目录或批量删除文件。"""
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

RELEASE_URL = "https://api.github.com/repos/gaboolic/rime-frost/releases/latest"
ASSET_NAME = "rime-frost-schemas.zip"
STATE_FILE = ".rime-mini-update.json"
SHANGHAI = timezone(timedelta(hours=8))
MAX_DOWNLOAD_BYTES = 160 * 1024 * 1024
MAX_EXPANDED_BYTES = 700 * 1024 * 1024


@dataclass
class UpdateResult:
    changed: bool
    message: str
    checked_at: str
    updated_at: str | None
    release: str


def now_text():
    return datetime.now(SHANGHAI).strftime("%Y-%m-%d %H:%M")


def read_update_state(rime_dir):
    try:
        value = json.loads((Path(rime_dir) / STATE_FILE).read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def state_text(state):
    if state.get("status") == "latest":
        updated = state.get("updated_at")
        return "已是最新" + (f" · 上次更新 {updated}" if updated else f" · {state.get('checked_at', '')} 已检查")
    if state.get("status") == "updated":
        return f"{state['updated_at']} 已更新到最新"
    return "尚未检查词库更新"


def _write_state(root, state):
    temporary = root / (".rime-mini-state-" + uuid.uuid4().hex + ".json")
    temporary.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, root / STATE_FILE)


def _request(url):
    return urllib.request.Request(url, headers={"User-Agent": "Mason/2.0",
                                                "Accept": "application/vnd.github+json"})


def latest_release():
    try:
        with urllib.request.urlopen(_request(RELEASE_URL), timeout=30) as response:
            data = json.loads(response.read(2 * 1024 * 1024).decode("utf-8"))
    except (urllib.error.URLError, ValueError, TimeoutError, OSError) as error:
        raise RuntimeError("无法检查最新词库，请检查网络后重试。") from error
    asset = next((item for item in data.get("assets", []) if item.get("name") == ASSET_NAME), None)
    if not asset:
        raise RuntimeError("最新发布中没有找到完整词库包，当前词库保持原状。")
    url = asset.get("browser_download_url", "")
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.hostname != "github.com" or not parts.path.startswith("/gaboolic/rime-frost/releases/download/"):
        raise RuntimeError("词库下载地址不符合预期，已停止更新。")
    if asset.get("size", 0) > MAX_DOWNLOAD_BYTES:
        raise RuntimeError("词库包超出预期大小，已停止更新。")
    return dict(id=data["id"], tag=data.get("tag_name", "最新发布"), asset_id=asset["id"],
                url=url, digest=asset.get("digest"), size=asset.get("size", 0))


def download_release(release, destination, progress):
    size = release.get("size", 0)
    downloaded = 0
    last_percent = -1
    digest = hashlib.sha256()
    try:
        with urllib.request.urlopen(_request(release["url"]), timeout=45) as response, destination.open("xb") as output:
            while True:
                chunk = response.read(256 * 1024)
                if not chunk:
                    break
                downloaded += len(chunk)
                if downloaded > MAX_DOWNLOAD_BYTES:
                    raise RuntimeError("下载内容超出预期大小，当前词库保持原状。")
                output.write(chunk)
                digest.update(chunk)
                percent = min(100, int(downloaded * 100 / size)) if size else int(downloaded / (1024 * 1024))
                if percent != last_percent:
                    progress(f"正在下载最新词库… {percent}%" if size else f"正在下载最新词库… {percent} MB")
                    last_percent = percent
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise RuntimeError("词库下载未完成，请检查网络后重试。") from error
    if size and downloaded != size:
        raise RuntimeError("词库包下载不完整，当前词库保持原状。")
    expected = release.get("digest") or ""
    if expected.startswith("sha256:") and digest.hexdigest() != expected.removeprefix("sha256:"):
        raise RuntimeError("词库包校验失败，当前词库保持原状。")


def _allowed(relative):
    path = PurePosixPath(relative)
    if path.is_absolute() or not path.parts or ".." in path.parts or any(":" in part or "\\" in part for part in path.parts):
        return False
    if len(path.parts) > 1:
        return path.parts[0] in ("cn_dicts", "cn_dicts_cell", "en_dicts", "opencc")
    return relative.endswith(".dict.yaml") or relative in ("essay.txt", "zh-moqi.gram")


def _target(root, relative):
    if not _allowed(relative):
        raise RuntimeError("词库路径不符合预期，已停止更新。")
    result = (root / relative).resolve()
    if root not in result.parents:
        raise RuntimeError("词库目标路径超出用户目录，已停止更新。")
    return result


def _hash(path):
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _current_matches(root, state):
    hashes = state.get("file_hashes")
    if not isinstance(hashes, dict) or not hashes:
        return False
    for relative, expected in hashes.items():
        if not isinstance(relative, str) or not _allowed(relative) or _hash(_target(root, relative)) != expected:
            return False
    return True


def _stage_archive(archive_path, stage):
    hashes = {}
    with zipfile.ZipFile(archive_path) as archive:
        names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
        markers = [name for name in names if PurePosixPath(name).name == "rime_frost.schema.yaml"]
        if len(markers) != 1:
            raise RuntimeError("词库包结构不符合预期，当前词库保持原状。")
        prefix = markers[0][:-len("rime_frost.schema.yaml")]
        selected = []
        total = 0
        for entry in archive.infolist():
            if entry.is_dir() or not entry.filename.startswith(prefix):
                continue
            relative = entry.filename[len(prefix):]
            if not _allowed(relative):
                continue
            if relative in hashes or any(name == relative for _, name in selected):
                raise RuntimeError("词库包中存在重复文件，已停止更新。")
            total += entry.file_size
            if total > MAX_EXPANDED_BYTES:
                raise RuntimeError("词库包解压大小超出预期，已停止更新。")
            selected.append((entry, relative))
        if not any(relative == "rime_frost.dict.yaml" for _, relative in selected) or not any(relative.startswith("cn_dicts/") for _, relative in selected):
            raise RuntimeError("词库包缺少核心词典，当前词库保持原状。")
        for entry, relative in selected:
            target = _target(stage, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            with archive.open(entry) as source, target.open("xb") as output:
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    digest.update(chunk)
                    output.write(chunk)
            hashes[relative] = digest.hexdigest()
    return hashes


def update_dictionary(rime_dir, weasel_dir, *, deploy_fn, progress=lambda message: None,
                      release_provider=latest_release, download_fn=download_release,
                      before_apply=lambda:None, after_apply=lambda:None):
    root = Path(rime_dir).resolve()
    if not root.is_dir():
        raise RuntimeError("用户词库目录不存在，请先安装输入法。")
    progress("正在检查最新词库…")
    release = release_provider()
    previous = read_update_state(root)
    checked = now_text()
    version_key = (release["id"], release["asset_id"])
    if (previous.get("release_id"), previous.get("asset_id")) == version_key:
        progress("正在确认本地词库…")
        if _current_matches(root, previous):
            state = dict(previous, checked_at=checked, status="latest")
            _write_state(root, state)
            return UpdateResult(False, "已是最新。\n检查时间：" + checked, checked, previous.get("updated_at"), release["tag"])
    run = root / ".rime-mini-updates" / (datetime.now(SHANGHAI).strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
    run.mkdir(parents=True, exist_ok=False)
    stage = run / "staged"
    stage.mkdir()
    archive_path = run / ASSET_NAME
    download_fn(release, archive_path, progress)
    progress("正在校验词库内容…")
    hashes = _stage_archive(archive_path, stage.resolve())
    changed = [relative for relative, expected in hashes.items() if _hash(_target(root, relative)) != expected]
    state = dict(release_id=release["id"], asset_id=release["asset_id"], release_tag=release["tag"],
                 checked_at=checked, file_hashes=hashes, updated_at=previous.get("updated_at"))
    if not changed:
        state["status"] = "latest"
        _write_state(root, state)
        return UpdateResult(False, "已是最新。\n检查时间：" + checked, checked, state["updated_at"], release["tag"])
    backup = run / "backup"
    backup.mkdir()
    progress("正在备份当前词库…")
    old = []
    for relative in changed:
        target = _target(root, relative)
        if target.is_file():
            saved = _target(backup.resolve(), relative)
            saved.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(target, saved)
            old.append(relative)
    # 备份失败会直接停止；不在缺少可恢复副本时覆盖数据。
    apply_started=False
    written=set()
    try:
        apply_started=True
        before_apply()
        progress("正在写入新词库…")
        for relative in changed:
            target = _target(root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            os.replace(_target(stage.resolve(), relative), target)
            written.add(relative)
        progress("正在部署最新词库…")
        success, error = deploy_fn(weasel_dir)
        if not success:
            raise RuntimeError("词库部署未完成：" + error)
    except Exception as error:
        # 逐文件恢复已覆盖的内容。新增加的文件保留，不批量删除。
        for relative in old:
            if relative in written:
                shutil.copy2(_target(backup.resolve(), relative), _target(root, relative))
        try:
            if written:
                deploy_fn(weasel_dir)
        except Exception:
            pass
        raise RuntimeError("更新未完成，已恢复原词库文件。可稍后重试。\n" + str(error)) from error
    finally:
        if apply_started:
            after_apply()
    updated = now_text()
    state.update(status="updated", updated_at=updated, backup_dir=str(backup))
    _write_state(root, state)
    return UpdateResult(True, updated + " 已更新到最新词库。", checked, updated, release["tag"])
