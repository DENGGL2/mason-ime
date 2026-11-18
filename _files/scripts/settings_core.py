# -*- coding: utf-8 -*-
"""麻绳输入法设置：仅依赖 Python 标准库与 Windows 自带组件。"""
import copy
import ctypes
import math
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
import winreg
import fuzzy_settings
import mason_paths

APP_TITLE = "麻绳输入法 · 设置"
FONT_FAMILY = "Microsoft YaHei UI"
FONT = (FONT_FAMILY, 10)
FONT_S = (FONT_FAMILY, 9)
FONT_B = (FONT_FAMILY, 11, "bold")
ICON_FONT = "Segoe MDL2 Assets"
ICONS = dict(menu="\ue700", appearance="\ue790", maintenance="\ue8b7", guide="\ue82d",
             grid="\ue80a", color="\ue790", radius="\ue739", font="\ue8d2", preview="\ue765",
             sun="\ue706", moon="\ue708", add="\ue710", refresh="\ue72c", save="\ue74e",
             update="\ue896", sync="\ue895", folder="\ue8b7", tools="\ue713", check="\ue8fb")
CREATE_NO_WINDOW = 0x08000000
SCRIPT_DIR = Path(__file__).resolve().parent
UI_THEMES = {
    "light": dict(bg="#F9F9F9", sidebar="#EEEEEE", surface="#F3F3F3",
                  soft="#EFEFEF", text="#202020", muted="#555555", faint="#797979",
                  border="#E0E0E0", accent="#202020", accent_soft="#D5D5D5",
                  hover="#E5E5E5", success="#70A52A", shadow="#DADADA", dialog="#E8E8E8"),
    "dark": dict(bg="#171717", sidebar="#202020", surface="#242424",
                 soft="#2D2D2D", text="#EFEFEF", muted="#BDBDBD", faint="#969696",
                 border="#424242", accent="#E6E6E6", accent_soft="#454545",
                 hover="#373737", success="#91B95E", shadow="#101010", dialog="#303030"),
}
# 主题名、ID、浅色高亮、深色高亮；实际候选窗与预览共用同一份数据。
PALETTES = [
    ("晴空蓝", "fluent", "#2563EB", "#89AAFF"),
    ("薄雾紫", "violet", "#7958D6", "#B7A0FF"),
    ("青松绿", "sage", "#258267", "#83D6BC"),
    ("石墨灰", "graphite", "#252525", "#D0D0D0"),
]
LEGACY_PRESETS = [
    ("碧水", "aqua", "#EEEEEC", "#000000", "#0A3AFA", "#FFFFFF"),
    ("明月", "luna", "#FFFFFF", "#000000", "#000000", "#FFFFFF"),
    ("墨池", "ink", "#FFFFFF", "#000000", "#000000", "#FFFFFF"),
    ("谷歌", "google", "#FFFFFF", "#000000", "#3975CE", "#FFFFFF"),
    ("青天", "azure", "#014E8B", "#EEF8FF", "#015EA9", "#FFFF7F"),
    ("暗堂", "dark_temple", "#222222", "#E6E3D8", "#333333", "#DAF692"),
]
DEFAULT_CONFIG = dict(scheme="mini_graphite_light", ui_theme="system", radius=10,
                      fontsize=14, horizontal=False, custom_bg="#FFFFFF",
                      custom_fg="#1C2434", custom_hl="#2563EB")
DEFAULT_CONFIG.update(fuzzy_settings.DEFAULTS)
DEFAULT_CONFIG.update(fuzzy_settings.DEFAULTS)


def get_rime_dir():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Rime\Weasel") as key:
            value, _ = winreg.QueryValueEx(key, "RimeUserDir")
            if value and os.path.isdir(value):
                return value
    except OSError:
        pass
    return os.path.join(os.environ.get("APPDATA", ""), "Rime")


def get_weasel_dir():
    for view in (winreg.KEY_WOW64_32KEY, winreg.KEY_WOW64_64KEY):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Rime\Weasel",
                                0, winreg.KEY_READ | view) as key:
                base, _ = winreg.QueryValueEx(key, "InstallDir")
        except OSError:
            continue
        root = Path(base)
        if (root / "WeaselDeployer.exe").is_file():
            return str(root)
        if root.is_dir():
            for child in sorted(root.glob("weasel-*"), reverse=True):
                if (child / "WeaselDeployer.exe").is_file():
                    return str(child)
    return None


def to_rime_color(color):
    """Weasel 默认 ABGR：RGB #2563EB 必须写成 0xEB6325。"""
    color = color.lstrip("#")
    return "0x" + (color[4:6] + color[2:4] + color[0:2]).lower()


def from_rime_color(value):
    value = value.strip().lower().removeprefix("0x")[-6:]
    return "#" + (value[4:6] + value[2:4] + value[0:2]).upper()


def luminance(color):
    rgb = [int(color.lstrip("#")[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    rgb = [v / 12.92 if v <= .04045 else ((v + .055) / 1.055) ** 2.4 for v in rgb]
    return sum(a * b for a, b in zip(rgb, (.2126, .7152, .0722)))


def readable_color(bg):
    lum = luminance(bg)
    return "#FFFFFF" if (1.05 / (lum + .05)) >= ((lum + .05) / .05) else "#111827"


def scheme_colors(scheme, cfg=None):
    cfg = cfg or DEFAULT_CONFIG
    if scheme == "__custom__":
        bg, fg, hl = (cfg[k] for k in ("custom_bg", "custom_fg", "custom_hl"))
        return bg, fg, hl, readable_color(hl)
    for _, name, light, dark in PALETTES:
        if scheme == f"mini_{name}_light":
            return "#FFFFFF", "#243047", light, "#FFFFFF"
        if scheme == f"mini_{name}_dark":
            return "#333333", "#EDF1FA", dark, "#111827"
    for _, sid, bg, fg, hl, hf in LEGACY_PRESETS:
        if sid == scheme:
            return bg, fg, hl, hf
    return scheme_colors(DEFAULT_CONFIG["scheme"])


def load_config(rime_dir):
    cfg = copy.deepcopy(DEFAULT_CONFIG)
    fuzzy_path = Path(rime_dir) / "rime_frost.custom.yaml"
    if fuzzy_path.is_file():
        cfg.update(fuzzy_settings.load(fuzzy_path.read_text(encoding="utf-8-sig")))
    path = Path(rime_dir) / "weasel.custom.yaml"
    try:
        text = path.read_text(encoding="utf-8-sig")
    except OSError:
        return cfg
    def scalar(key):
        found = re.search(r"(?m)^\s*[\"']?" + re.escape(key) + r"[\"']?\s*:\s*([^#\r\n]+)", text)
        return found.group(1).strip().strip("\"'") if found else None
    scheme = scalar("style/color_scheme")
    if scheme:
        cfg["scheme"] = "__custom__" if scheme == "custom" else scheme
    for key, field, low, high in (("style/font_point", "fontsize", 11, 24),
                                   ("style/layout/corner_radius", "radius", 0, 20)):
        value = scalar(key)
        if value and value.isdecimal():
            cfg[field] = max(low, min(high, int(value)))
    value = scalar("style/horizontal")
    if value:
        cfg["horizontal"] = value.lower() == "true"
    theme = re.search(r"(?m)^# rime-mini-ui:\s*(light|dark|system)\s*$", text)
    cfg["ui_theme"] = theme.group(1) if theme else ("dark" if cfg["scheme"].endswith("_dark") else "light")
    match = re.search(r"(?m)^([ \t]*)[\"']?preset_color_schemes/custom[\"']?\s*:\s*$", text)
    if match:
        remaining = text[match.end():]
        indent = len(match.group(1))
        block = []
        for line in remaining.splitlines():
            if line.strip() and not line.lstrip().startswith("#") and len(line) - len(line.lstrip()) <= indent:
                break
            block.append(line)
        block = "\n".join(block)
        for field, key in (("custom_bg", "back_color"), ("custom_fg", "candidate_text_color"),
                           ("custom_hl", "hilited_candidate_back_color")):
            value = re.search(r"(?m)^\s*" + key + r":\s*(0x[0-9a-fA-F]{6,8})", block)
            if not value and field == "custom_fg":
                value = re.search(r"(?m)^\s*text_color:\s*(0x[0-9a-fA-F]{6,8})", block)
            if value:
                cfg[field] = from_rime_color(value.group(1))
    return cfg


def managed_patch(cfg):
    selected = "custom" if cfg["scheme"] == "__custom__" else cfg["scheme"]
    selected_dark = selected
    if cfg['ui_theme'] == 'system' and re.fullmatch(r'mini_(fluent|violet|sage|graphite)_(light|dark)', selected):
        family = selected.rsplit('_',1)[0]
        selected,selected_dark = family+'_light',family+'_dark'
    lines = [f"  style/color_scheme: {selected}",
             f"  style/color_scheme_dark: {selected_dark}",
             "  style/font_face: Microsoft YaHei UI",
             "  style/label_font_face: Microsoft YaHei UI",
             "  style/comment_font_face: Microsoft YaHei UI",
             f"  style/font_point: {cfg['fontsize']}",
             f"  style/label_font_point: {max(10, cfg['fontsize'] - 3)}",
             f"  style/comment_font_point: {max(10, cfg['fontsize'] - 2)}",
             f"  style/horizontal: {str(cfg['horizontal']).lower()}",
             "  style/inline_preedit: false", "  style/preedit_type: composition",
             "  style/display_tray_icon: false",
             "  style/label_format: '%s'", "  style/mark_text: ''",
             "  style/layout/border_width: 1", "  style/layout/margin_x: 12",
             "  style/layout/margin_y: 10", "  style/layout/spacing: 10",
             "  style/layout/candidate_spacing: 6", "  style/layout/hilite_spacing: 2",
             "  style/layout/hilite_padding: 6",
             f"  style/layout/corner_radius: {cfg['radius']}",
             f"  style/layout/round_corner: {max(0, cfg['radius'] - 3)}",
             "  style/layout/shadow_radius: 5", "  style/layout/shadow_offset_x: 0",
             "  style/layout/shadow_offset_y: 2"]
    schemes = [(name, f"mini_{sid}_{theme}") for name, sid, _, _ in PALETTES for theme in ("light", "dark")]
    if cfg["scheme"] == "__custom__":
        schemes.append(("自定义", "custom"))
    for name, sid in schemes:
        bg, fg, hl, hf = scheme_colors("__custom__" if sid == "custom" else sid, cfg)
        is_dark = luminance(bg) < .3
        muted = "#A3B0C5" if is_dark else "#66758C"
        border = "#555555" if is_dark else "#DFE5EF"
        lines += [f"  preset_color_schemes/{sid}:", f"    name: {name} · {'深色' if is_dark else '浅色'}",
                  "    color_format: abgr", f"    back_color: {to_rime_color(bg)}",
                  f"    border_color: {to_rime_color(border)}", "    shadow_color: 0x16000000",
                  f"    text_color: {to_rime_color(muted)}", f"    candidate_text_color: {to_rime_color(fg)}",
                  f"    label_color: {to_rime_color(muted)}", f"    comment_text_color: {to_rime_color(muted)}",
                  f"    hilited_text_color: {to_rime_color(fg)}", f"    hilited_back_color: {to_rime_color(bg)}",
                  f"    hilited_candidate_back_color: {to_rime_color(hl)}",
                  f"    hilited_candidate_text_color: {to_rime_color(hf)}",
                  f"    hilited_label_color: {to_rime_color(hf)}",
                  f"    hilited_comment_text_color: {to_rime_color(hf)}"]
    return lines


def build_yaml(cfg, existing=""):
    """保留其他补丁与顶层字段，只替换此工具管理的外观字段。"""
    if re.search(r"(?m)^patch:[ \t]*[^\s#]", existing):
        raise RuntimeError("现有配置使用了内联 patch 写法，已保留原文件；请先把 patch 改为多行写法。")
    new_lines = managed_patch(cfg)
    keys = {line.strip().split(":", 1)[0] for line in new_lines if line.startswith("  ") and not line.startswith("   ")}
    keys.add("preset_color_schemes/custom")
    source = [line for line in existing.splitlines()
              if not line.startswith("# rime-mini-ui:") and line not in ("# rime-mini appearance begin", "# rime-mini appearance end")]
    result = []
    patch_found = False
    in_patch = False
    skipping = False
    entry_indent = 2
    inserted = False
    for line in source:
        content = line.strip()
        indent = len(line) - len(line.lstrip())
        if re.match(r"^patch:\s*(?:#.*)?$", line):
            patch_found = in_patch = True
            result.append(line)
            continue
        if in_patch and content and not content.startswith("#") and indent == 0:
            result.extend(new_lines)
            inserted = True
            in_patch = False
            skipping = False
        if in_patch and content and not content.startswith("#"):
            if indent <= entry_indent:
                key = content.split(":", 1)[0].strip("\"'")
                skipping = key in keys
                if indent != 2 and not skipping:
                    entry_indent = indent
            if skipping:
                continue
        elif skipping and in_patch:
            continue
        result.append(line)
    if in_patch and not inserted:
        result.extend(new_lines)
    if not patch_found:
        result.extend(["", "patch:"] + new_lines)
    header = ["# rime-mini-ui: " + cfg["ui_theme"]]
    if not existing:
        header.append("# 麻绳输入法双主题外观；其他输入偏好保持原配置。")
    return "\n".join(header + result).strip() + "\n"


def save_config(rime_dir, cfg):
    path = Path(rime_dir) / "weasel.custom.yaml"
    existing = path.read_text(encoding="utf-8-sig") if path.exists() else ""
    value = build_yaml(cfg, existing)
    fuzzy_path = Path(rime_dir) / "rime_frost.custom.yaml"
    fuzzy_existing = fuzzy_path.read_text(encoding="utf-8-sig") if fuzzy_path.exists() else ""
    fuzzy_value = fuzzy_settings.build_yaml(fuzzy_existing, cfg)
    # Validate both merges before changing either file. Every changed original is backed up.
    if fuzzy_value != fuzzy_existing:
        if fuzzy_path.exists():
            fuzzy_backup = fuzzy_path.with_name(fuzzy_path.name + ".mason-backup-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
            shutil.copy2(fuzzy_path, fuzzy_backup)
        fuzzy_temp = fuzzy_path.with_name(".mason-fuzzy-write-" + uuid.uuid4().hex + ".yaml")
        fuzzy_temp.write_text(fuzzy_value, encoding="utf-8", newline="\n")
        os.replace(fuzzy_temp, fuzzy_path)
    if value == existing:
        return str(path)
    if value != existing and path.exists():
        backup = path.with_name(path.name + ".mini-backup-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
        shutil.copy2(path, backup)
    temp = path.with_name(".rime-mini-write-" + uuid.uuid4().hex + ".yaml")
    temp.write_text(value, encoding="utf-8", newline="\n")
    os.replace(temp, path)
    return str(path)


def deploy(weasel_dir):
    exe = Path(weasel_dir) / "WeaselDeployer.exe"
    if not exe.is_file():
        return False, "未找到部署程序。"
    try:
        result = subprocess.run([str(exe), "/deploy"], timeout=300, creationflags=CREATE_NO_WINDOW, env=mason_paths.runtime_env(__file__),
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return result.returncode == 0, "" if result.returncode == 0 else f"部署程序返回 {result.returncode}。"
    except subprocess.TimeoutExpired:
        return False, "部署等待超时，可在「词库与维护」中重试。"
    except OSError as exc:
        return False, str(exc)


def start_server(weasel_dir):
    engine = Path(weasel_dir)
    launcher = engine / "Mason.Server.exe"
    subprocess.Popen([str(launcher if launcher.is_file() else engine / "WeaselServer.exe")],
                     creationflags=CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     env=mason_paths.runtime_env(__file__) if (engine / "mason-runtime.json").is_file() else None)


def stop_server(weasel_dir):
    engine = Path(weasel_dir)
    launcher = engine / "Mason.Server.exe"
    subprocess.run([str(launcher if launcher.is_file() else engine / "WeaselServer.exe"), "/quit"], timeout=25, check=True,
                   creationflags=CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


