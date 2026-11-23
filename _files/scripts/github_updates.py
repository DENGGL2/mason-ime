"""Bounded GitHub release requests, with retries and a public-page fallback."""
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from html.parser import HTMLParser

MAX_METADATA_BYTES = 2 * 1024 * 1024
TRANSIENT_STATUS = {408, 429, 500, 502, 503, 504}


class NetworkError(RuntimeError):
    pass


class ApiRateLimited(NetworkError):
    pass


def version_tuple(value):
    value = str(value).removeprefix("v")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", value):
        raise RuntimeError("更新版本信息格式无效。")
    parts = tuple(map(int, value.split(".")))
    return parts + (0,) * (4 - len(parts))


def repository_parts(address):
    match = re.fullmatch(r"https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/?", address)
    if not match:
        raise RuntimeError("GitHub 更新地址格式无效。")
    return match[1], match[2]


def request(url, *, binary=False, method=None):
    return urllib.request.Request(url, headers={
        "User-Agent": "Mason-update",
        "Accept": "application/octet-stream" if binary else "application/vnd.github+json",
    }, method=method)


def open_retry(req, *, timeout=15, attempts=3, progress=lambda message: None,
               sleep=time.sleep, opener=None):
    opener = opener or urllib.request.urlopen
    for attempt in range(attempts):
        try:
            return opener(req, timeout=timeout)
        except urllib.error.HTTPError as error:
            if error.code in (403, 429) and error.headers.get("X-RateLimit-Remaining") == "0":
                raise ApiRateLimited("GitHub 检查次数已达限制，正在改用公开发布页。") from error
            if error.code not in TRANSIENT_STATUS or attempt == attempts - 1:
                raise NetworkError("GitHub 请求未完成（HTTP " + str(error.code) + "），请稍后重试。") from error
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            if attempt == attempts - 1:
                raise NetworkError("连接 GitHub 未完成，已重试。请检查网络后重试。") from error
        progress("网络暂时没有响应，正在重试… " + str(attempt + 2) + "/" + str(attempts))
        sleep(.5 * (attempt + 1))


def read_limited(response, maximum=MAX_METADATA_BYTES):
    data = response.read(maximum + 1)
    if len(data) > maximum:
        raise RuntimeError("更新信息超出预期大小。")
    return data


def validate_asset_url(url, repository, tag=None):
    owner, name = repository_parts(repository)
    parts = urllib.parse.urlsplit(url)
    prefix = "/" + owner + "/" + name + "/releases/download/"
    if (parts.scheme != "https" or parts.hostname != "github.com" or parts.port not in (None, 443)
            or parts.username or parts.password or parts.query or parts.fragment
            or not parts.path.startswith(prefix)):
        raise RuntimeError("更新下载地址不符合预期。")
    segments = parts.path[len(prefix):].split("/")
    if len(segments) != 2 or not segments[0] or not segments[1]:
        raise RuntimeError("更新下载地址不符合预期。")
    if tag is not None and urllib.parse.unquote(segments[0]) != tag:
        raise RuntimeError("更新包与版本信息不一致。")
    return url


class ReleaseAssetsParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.assets = []
        self.row = None
        self.depth = 0

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "li":
            if self.row is None:
                self.row = {"url": "", "digest": ""}
                self.depth = 0
            self.depth += 1
        if self.row is None:
            return
        if tag == "a" and "/releases/download/" in attrs.get("href", ""):
            self.row["url"] = urllib.parse.urljoin("https://github.com", attrs["href"])
        if tag == "clipboard-copy" and re.fullmatch(r"sha256:[0-9a-fA-F]{64}", attrs.get("value", "")):
            self.row["digest"] = attrs["value"].lower()

    def handle_endtag(self, tag):
        if tag == "li" and self.row is not None:
            self.depth -= 1
            if self.depth == 0:
                if self.row["url"]:
                    self.assets.append(self.row)
                self.row = None


def _fallback_release(repository, asset_name, progress):
    owner, name = repository_parts(repository)
    progress("正在通过 GitHub 发布页检查更新…")
    with open_retry(request(repository.rstrip("/") + "/releases/latest", method="HEAD"), progress=progress) as response:
        parts = urllib.parse.urlsplit(response.geturl())
    prefix = "/" + owner + "/" + name + "/releases/tag/"
    if parts.scheme != "https" or parts.hostname != "github.com" or not parts.path.startswith(prefix):
        raise RuntimeError("GitHub 尚未发布可用版本。")
    tag = urllib.parse.unquote(parts.path[len(prefix):])
    version_tuple(tag)
    expected_name = asset_name(tag.removeprefix("v"))
    parser = ReleaseAssetsParser()
    expanded = repository.rstrip("/") + "/releases/expanded_assets/" + urllib.parse.quote(tag, safe="")
    with open_retry(request(expanded), progress=progress) as response:
        parser.feed(read_limited(response).decode("utf-8"))
    asset = next((item for item in parser.assets
                  if urllib.parse.unquote(urllib.parse.urlsplit(item["url"]).path.rsplit("/", 1)[-1]) == expected_name), None)
    if asset is None:
        raise RuntimeError("最新发布中没有找到可用的更新包。")
    validate_asset_url(asset["url"], repository, tag)
    return {"id": "tag:" + tag, "tag": tag, "asset_id": expected_name + ":" + asset["digest"],
            "asset_name": expected_name, "url": asset["url"], "digest": asset["digest"], "size": 0,
            "notes": "", "published_at": "", "source": "release-page"}


def latest_release(repository, asset_name, *, progress=lambda message: None):
    owner, name = repository_parts(repository)
    endpoint = "https://api.github.com/repos/" + owner + "/" + name + "/releases/latest"
    try:
        with open_retry(request(endpoint), progress=progress) as response:
            data = json.loads(read_limited(response).decode("utf-8"))
    except ApiRateLimited:
        return _fallback_release(repository, asset_name, progress)
    except (UnicodeError, ValueError) as error:
        raise RuntimeError("GitHub 更新信息无法解析，请稍后重试。") from error
    if not isinstance(data, dict) or data.get("draft") or data.get("prerelease"):
        raise RuntimeError("GitHub 尚未发布可用的正式版本。")
    tag = str(data.get("tag_name", ""))
    version_tuple(tag)
    expected_name = asset_name(tag.removeprefix("v"))
    asset = next((item for item in data.get("assets", []) if item.get("name") == expected_name), None)
    if asset is None:
        raise RuntimeError("最新发布中没有找到可用的更新包。")
    url = validate_asset_url(str(asset.get("browser_download_url", "")), repository, tag)
    if urllib.parse.unquote(urllib.parse.urlsplit(url).path.rsplit('/', 1)[-1]) != expected_name:
        raise RuntimeError('更新包名称与下载地址不一致。')
    digest = str(asset.get("digest") or "").lower()
    if digest and not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise RuntimeError("更新包校验信息格式无效。")
    return {"id": data["id"], "tag": tag, "asset_id": asset["id"], "asset_name": expected_name,
            "url": url, "digest": digest, "size": int(asset.get("size") or 0),
            "notes": str(data.get("body") or "")[:8000], "published_at": str(data.get("published_at") or ""),
            "source": "api"}
