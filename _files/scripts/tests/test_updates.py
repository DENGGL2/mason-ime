"""Update integrity, retry, availability and notification tests. Fixtures are retained."""
import hashlib
import io
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock
import urllib.error
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import application_updater as app
import github_updates as github
import update_state

REPOSITORY = "https://github.com/DENGGL2/mason-ime"


class Response(io.BytesIO):
    def __init__(self, data, url="", headers=None):
        super().__init__(data)
        self.headers = headers or {"Content-Length": str(len(data))}
        self.url = url

    def geturl(self):
        return self.url


class UpdatesTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="mason-update-proof-"))
        self.info = {"product": "Mason", "version": "2.4.8", "github_url": REPOSITORY}

    def package(self, *, version="2.4.9", alter=False, extra=None):
        files = {"Mason.Install.exe": b"MZ-test-installer", "Mason.Install.exe.config": b"<configuration/>",
                 "_files/ui/project-info.json": json.dumps(dict(self.info, version=version)).encode("utf-8"),
                 "_files/scripts/settings_backend.py": b"# fixture"}
        entries = [{"Path": name, "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()} for name, data in files.items()]
        if alter:
            files["Mason.Install.exe"] = b"MZ-bad-installer!"
        path = self.root / ("package-" + str(len(list(self.root.glob('package-*')))) + ".zip")
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in files.items():
                archive.writestr("Mason/" + name, data)
            archive.writestr("Mason/_files/install-manifest.json", json.dumps({"Product": "Mason", "Version": version, "Files": entries}))
            if extra:
                archive.writestr(extra, b"unexpected")
        return path

    def release(self, path):
        return {"id": 1, "asset_id": 2, "tag": "v2.4.9", "asset_name": "Mason-2.4.9-Windows.zip",
                "url": REPOSITORY + "/releases/download/v2.4.9/Mason-2.4.9-Windows.zip",
                "digest": "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest(), "size": path.stat().st_size}

    def test_api_retries_only_transient_failures(self):
        calls = []
        def opener(request, timeout):
            calls.append(timeout)
            if len(calls) < 3:
                raise urllib.error.HTTPError(request.full_url, 503, "Busy", {}, None)
            return Response(b"ready")
        messages = []
        with github.open_retry(github.request(REPOSITORY), opener=opener, sleep=lambda seconds: None, progress=messages.append) as response:
            self.assertEqual(response.read(), b"ready")
        self.assertEqual(len(calls), 3)
        self.assertEqual(len(messages), 2)
        bad = urllib.error.HTTPError(REPOSITORY, 404, "Missing", {}, None)
        with self.assertRaisesRegex(github.NetworkError, "HTTP 404"):
            github.open_retry(github.request(REPOSITORY), opener=mock.Mock(side_effect=bad), sleep=lambda seconds: None)

    def test_rate_limit_is_eligible_for_fallback(self):
        error = urllib.error.HTTPError(REPOSITORY, 403, "Limited", {"X-RateLimit-Remaining": "0"}, None)
        with self.assertRaises(github.ApiRateLimited):
            github.open_retry(github.request(REPOSITORY), opener=mock.Mock(side_effect=error))

    def test_public_release_parser_binds_hash_to_the_right_asset(self):
        parser = github.ReleaseAssetsParser()
        parser.feed('<li><a href="/DENGGL2/mason-ime/releases/download/v2.4.9/other.zip">Other</a><clipboard-copy value="sha256:' + 'a'*64 + '"></clipboard-copy></li><li><a href="/DENGGL2/mason-ime/releases/download/v2.4.9/Mason-2.4.9-Windows.zip">Mason</a><clipboard-copy value="sha256:' + 'b'*64 + '"></clipboard-copy></li>')
        self.assertEqual(parser.assets[1]["digest"], "sha256:" + "b"*64)
        self.assertTrue(parser.assets[1]["url"].endswith("Mason-2.4.9-Windows.zip"))

    def test_download_restarts_after_interruption_and_reports_speed(self):
        package = self.package()
        release = self.release(package)
        destination = self.root / "download"
        destination.mkdir()
        broken = Response(package.read_bytes())
        broken.read = mock.Mock(side_effect=TimeoutError("read stalled"))
        progress = []
        with mock.patch.object(github, "open_retry", side_effect=[broken, Response(package.read_bytes())]), mock.patch.object(app.time, "sleep"):
            archive, digest = app.download(release, destination, progress.append)
        self.assertEqual(archive.read_bytes(), package.read_bytes())
        self.assertEqual(digest, release["digest"].removeprefix("sha256:"))
        self.assertTrue(any(item.get("bytes_per_second", 0) > 0 for item in progress))
        self.assertTrue(any("重试" in item["message"] for item in progress))
        self.assertTrue((destination / (release["asset_name"] + ".part-1")).exists())

    def test_wrong_archive_hash_prevents_installation(self):
        package = self.package()
        release = self.release(package)
        release["digest"] = "sha256:" + "0"*64
        destination = self.root / "download"
        destination.mkdir()
        with mock.patch.object(github, "open_retry", return_value=Response(package.read_bytes())), self.assertRaisesRegex(RuntimeError, "SHA-256"):
            app.download(release, destination, lambda item: None)
        self.assertFalse((destination / release["asset_name"]).exists())

    def test_valid_package_is_extracted_and_each_file_is_verified(self):
        path = self.package()
        stage = app.verify_and_extract(path, self.root, "2.4.9", lambda item: None)
        self.assertEqual((stage / "Mason.Install.exe").read_bytes(), b"MZ-test-installer")
        self.assertEqual(json.loads((stage / "_files/ui/project-info.json").read_text())["version"], "2.4.9")

    def test_corrupt_file_wrong_version_and_traversal_are_rejected(self):
        for case in ("corrupt", "version", "traversal", "undeclared"):
            folder = self.root / case
            folder.mkdir()
            path = self.package(version="2.4.10" if case == "version" else "2.4.9", alter=case == "corrupt",
                                extra="Mason/../escape.txt" if case == "traversal" else "Mason/unrelated.txt" if case == "undeclared" else None)
            with self.assertRaises(RuntimeError):
                app.verify_and_extract(path, folder, "2.4.9", lambda item: None)
        self.assertFalse((self.root / "escape.txt").exists())

    def test_prepare_and_handoff_preserve_target_and_user_files(self):
        package = self.package()
        release = self.release(package)
        user = self.root / "用户数据"
        user.mkdir()
        custom = user / "custom_phrase.txt"
        custom.write_text("personal words", encoding="utf-8")
        target = self.root / "已安装 Mason"
        def copied(release, folder, progress):
            path = folder / release["asset_name"]
            shutil.copy2(package, path)
            return path, release["digest"].removeprefix("sha256:")
        prepared = app.prepare(self.info, user, target, "2.4.9", release_provider=lambda: release, download_fn=copied)
        launch = mock.Mock(return_value=mock.Mock(pid=777))
        result = app.launch(user, target, prepared["prepared_path"], 123, launcher=launch)
        arguments = launch.call_args.args[0]
        self.assertEqual(arguments[1:], ["--update", "--target", str(target.resolve()), "--wait-pid", "123"])
        self.assertEqual(result["process_id"], 777)
        self.assertEqual(custom.read_text(encoding="utf-8"), "personal words")
        Path(prepared["installer"]).write_bytes(b"changed")
        with self.assertRaisesRegex(RuntimeError, "校验失败"):
            app.launch(user, target, prepared["prepared_path"], 123, launcher=launch)
        self.assertEqual(launch.call_count, 1)

    def test_available_updates_are_independent_and_each_notifies_once(self):
        release = {"tag": "v2.4.9"}
        update_state.record(self.root, "application", release, True)
        update_state.record(self.root, "dictionary", {"tag": "1.0.6"}, True)
        notify = mock.Mock(return_value=True)
        self.assertTrue(update_state.notify_once(self.root, "application", notify))
        self.assertFalse(update_state.notify_once(self.root, "application", notify))
        self.assertTrue(update_state.notify_once(self.root, "dictionary", notify))
        self.assertEqual(notify.call_count, 2)
        update_state.record(self.root, "application", {"tag": "v2.4.10"}, True)
        self.assertTrue(update_state.notify_once(self.root, "application", notify))
        flags = update_state.refresh_local(self.root, "2.4.10", "1.0.6")
        self.assertFalse(flags["app_available"])
        self.assertFalse(flags["dictionary_available"])
        self.assertIn("app_available=0", (self.root / update_state.STATUS_NAME).read_text())

    def test_failed_notification_does_not_claim_success(self):
        update_state.record(self.root, "application", {"tag": "v2.4.9"}, True)
        self.assertFalse(update_state.notify_once(self.root, "application", lambda kind, version: False))
        self.assertEqual(update_state.read(self.root)["application"]["notified_version"], "")

    def test_unsafe_release_addresses_are_rejected(self):
        for address in ("http://github.com/DENGGL2/mason-ime/releases/download/v2.4.9/file.zip",
                        "https://evil.example/DENGGL2/mason-ime/releases/download/v2.4.9/file.zip",
                        REPOSITORY + "/releases/download/v2.4.9/subfolder/file.zip",
                        REPOSITORY + "/releases/download/v2.4.9/file.zip?token=secret"):
            with self.assertRaises(RuntimeError):
                github.validate_asset_url(address, REPOSITORY, "v2.4.9")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    unittest.main(verbosity=2)
