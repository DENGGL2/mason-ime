"""Merge Mason's fuzzy spelling rules without replacing personal Rime rules."""
import json
import re

# These derive rules follow the fuzzy spelling examples in rime_frost.schema.yaml.
GROUPS = {
    "nl": ("derive/^n/l/", "derive/^l/n/"),
    "lr": ("derive/^l/r/", "derive/^r/l/"),
    "fh": ("derive/^f/h/", "derive/^h/f/"),
    "gk": ("derive/^g/k/", "derive/^k/g/"),
    "z_zh": ("derive/^zh/z/", "derive/^z([^h])/zh$1/"),
    "c_ch": ("derive/^ch/c/", "derive/^c([^h])/ch$1/"),
    "s_sh": ("derive/^sh/s/", "derive/^s([^h])/sh$1/"),
    "an_ang": ("derive/ang$/an/", "derive/an$/ang/"),
    "en_eng": ("derive/eng$/en/", "derive/en$/eng/"),
    "in_ing": ("derive/in$/ing/", "derive/ing$/in/"),
    "ian_iang": ("derive/ian$/iang/", "derive/iang$/ian/"),
    "uan_uang": ("derive/uan$/uang/", "derive/uang$/uan/"),
}
DEFAULTS = dict(fuzzy_enabled=False, **{"fuzzy_" + key: False for key in GROUPS})
META = "# mason-fuzzy: "
BEGIN = "# mason-fuzzy begin"
END = "# mason-fuzzy end"
CREATED = "# mason-fuzzy append"


def load(text):
    cfg = DEFAULTS.copy()
    match = re.search(r"(?m)^# mason-fuzzy: (.+)$", text)
    if match:
        try:
            value = json.loads(match.group(1))
            cfg["fuzzy_enabled"] = value.get("enabled") is True
            selected = value.get("groups", [])
            for key in GROUPS:
                cfg["fuzzy_" + key] = key in selected
        except (ValueError, TypeError, AttributeError):
            pass
    return cfg


def validate(cfg):
    for key in DEFAULTS:
        if key in cfg and not isinstance(cfg[key], bool):
            raise ValueError("模糊音选项必须为开启或关闭。")


def selected_rules(cfg):
    if not cfg.get("fuzzy_enabled", False):
        return []
    rules = []
    for group, values in GROUPS.items():
        if cfg.get("fuzzy_" + group, False):
            for rule in values:
                if rule not in rules:
                    rules.append(rule)
    return rules


def _flow_items(value):
    """Keep each original YAML scalar verbatim when expanding an inline list."""
    source = value.strip()
    if not source.startswith("["):
        raise ValueError("现有模糊音追加配置不是列表，原配置已保留。")
    quote, escaped, depth, start, items = None, False, 1, 1, []
    for index in range(1, len(source)):
        char = source[index]
        if quote:
            if escaped:
                escaped = False
            elif char == "\\" and quote == '"':
                escaped = True
            elif char == quote:
                # A doubled single quote stays inside a YAML single-quoted scalar.
                if quote == "'" and index + 1 < len(source) and source[index + 1] == "'":
                    escaped = True
                else:
                    quote = None
            continue
        if char in "\"'":
            quote = char
        elif char == "[":
            depth += 1
        elif char == "]":
            depth -= 1
            if depth == 0:
                item = source[start:index].strip()
                if item:
                    items.append(item)
                tail = source[index + 1:].strip()
                if tail and not tail.startswith("#"):
                    break
                return items, tail
        elif char == "," and depth == 1:
            item = source[start:index].strip()
            if not item:
                raise ValueError("现有模糊音列表格式无效，原配置已保留。")
            items.append(item)
            start = index + 1
    raise ValueError("现有模糊音列表格式无效，原配置已保留。")


def _scalar(line):
    match = re.match(r"^\s*-\s+(.+?)\s*$", line)
    if not match:
        return None
    value = match.group(1)
    if value.startswith('"'):
        try:
            return json.JSONDecoder().raw_decode(value)[0]
        except ValueError:
            return None
    if value.startswith("'"):
        match = re.match(r"^'((?:[^']|'')*)'", value)
        return match.group(1).replace("''", "'") if match else None
    return re.split(r"\s+#", value, maxsplit=1)[0].strip()


def build_yaml(existing, cfg):
    validate(cfg)
    choices = [key for key in GROUPS if cfg.get("fuzzy_" + key, False)]
    rules = selected_rules(cfg)
    owned = META in existing or BEGIN in existing or CREATED in existing
    if not owned and not rules and not cfg.get("fuzzy_enabled") and not choices:
        return existing
    lines = existing.splitlines()
    starts = [i for i, line in enumerate(lines) if re.match(r"^patch:\s*(?:#.*)?$", line)]
    if len(starts) > 1 or (not starts and re.search(r"(?m)^patch:", existing)):
        raise ValueError("现有输入配置的 patch 写法不能安全合并，原配置已保留。")
    if not starts:
        if lines and lines[-1].strip():
            lines.append("")
        lines.append("patch:")
        starts = [len(lines) - 1]
    patch = starts[0]
    stop = patch + 1
    while stop < len(lines) and (not lines[stop].strip() or lines[stop].startswith((" ", "#"))):
        stop += 1
    entry_re = re.compile(r"^( +)(?:\"speller/algebra/\+\"|'speller/algebra/\+'|speller/algebra/\+)\s*:\s*(.*)$")
    entries = [(i, entry_re.match(lines[i])) for i in range(patch + 1, stop) if entry_re.match(lines[i])]
    if len(entries) > 1:
        raise ValueError("现有配置存在重复的模糊音追加项，原配置已保留。")
    if entries:
        index, match = entries[0]
        indent, suffix = match.groups()
        last = index + 1
        while last < stop:
            line = lines[last]
            spaces = len(line) - len(line.lstrip())
            if line.strip() and not line.lstrip().startswith(("#", "- ")) and spaces <= len(indent):
                break
            last += 1
        body = lines[index + 1:last]
        created = CREATED in suffix
        if suffix and not suffix.startswith("#"):
            items, comment = _flow_items(suffix)
            body = [indent + "  - " + item for item in items] + body
            suffix = comment
        cleaned, inside = [], False
        for line in body:
            marker = line.strip()
            if marker == BEGIN:
                if inside:
                    raise ValueError("模糊音标记格式无效，原配置已保留。")
                inside = True
            elif marker == END:
                if not inside:
                    raise ValueError("模糊音标记格式无效，原配置已保留。")
                inside = False
            elif not inside:
                cleaned.append(line)
        if inside:
            raise ValueError("模糊音标记未闭合，原配置已保留。")
        user_rules = {_scalar(line) for line in cleaned}
        addition = [rule for rule in rules if rule not in user_rules]
        if addition:
            cleaned += [indent + "  " + BEGIN]
            cleaned += [indent + "  - " + json.dumps(rule, ensure_ascii=False) for rule in addition]
            cleaned += [indent + "  " + END]
        meaningful = [line for line in cleaned if line.strip() and not line.lstrip().startswith("#")]
        if created and not meaningful:
            lines[index:last] = cleaned
        else:
            if not meaningful and not addition:
                suffix = "[] " + (suffix if not created else "")
            lines[index:last] = [indent + '"speller/algebra/+":' + (" " + suffix if suffix else "")] + cleaned
    elif rules:
        child_indent = [len(line) - len(line.lstrip()) for line in lines[patch + 1:stop]
                        if line.strip() and not line.lstrip().startswith("#")]
        indent = " " * (min(child_indent) if child_indent else 2)
        block = [indent + '"speller/algebra/+": ' + CREATED, indent + "  " + BEGIN]
        block += [indent + "  - " + json.dumps(rule, ensure_ascii=False) for rule in rules]
        block += [indent + "  " + END]
        lines[stop:stop] = block
    lines = [line for line in lines if not line.startswith(META)]
    metadata = json.dumps(dict(enabled=cfg.get("fuzzy_enabled", False), groups=choices), separators=(",", ":"))
    return "\n".join([META + metadata] + lines).rstrip() + "\n"
