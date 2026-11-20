"""Persist correction and selection-learning toggles without replacing user patches."""
import re

DEFAULTS = {'correction_enabled': True, 'learning_flexible': False}
KEY = 'mason_correction/enabled'
KEYS = {'correction_enabled': KEY, 'learning_flexible': 'mason_learning/flexible'}
BEGIN = '# mason-correction-settings-begin'
END = '# mason-correction-settings-end'
ENTRY = re.compile(r'^  [\"\']?(mason_correction/enabled|mason_learning/flexible)[\"\']?\s*:\s*(.*?)\s*$')


def validate(cfg):
    for field, label in (('correction_enabled', '纠正输入错误'), ('learning_flexible', '柔性词频学习')):
        if field in cfg and not isinstance(cfg[field], bool):
            raise ValueError(label + '选项必须为开启或关闭。')


def load(text):
    values = DEFAULTS.copy()
    for line in text.splitlines():
        match = ENTRY.match(line)
        if match:
            value = re.split(r'\s+#', match[2], maxsplit=1)[0].strip().lower()
            field = next(field for field, key in KEYS.items() if key == match[1])
            if value in ('true', 'false'):
                values[field] = value == 'true'
    return values


def build_yaml(existing, cfg):
    validate(cfg)
    lines = existing.splitlines()
    patches = [i for i, line in enumerate(lines) if re.fullmatch(r'patch:\s*(?:#.*)?', line)]
    if len(patches) > 1 or (not patches and re.search(r'(?m)^patch:', existing)):
        raise ValueError('现有输入配置的 patch 写法不能安全合并，原配置已保留。')
    if not patches:
        if lines and lines[-1].strip():
            lines.append('')
        lines.append('patch:')
        patches = [len(lines) - 1]
    start = patches[0] + 1
    end = start
    while end < len(lines) and (not lines[end].strip() or lines[end].startswith((' ', '#'))):
        end += 1
    additions = []
    for field, key in KEYS.items():
        entries = [i for i in range(start, end)
                   if (match := ENTRY.match(lines[i])) and match[1] == key]
        if len(entries) > 1:
            raise ValueError('现有输入配置存在重复的纠错或学习开关，原配置已保留。')
        entry = '  ' + key + ': ' + str(cfg.get(field, DEFAULTS[field])).lower()
        if entries:
            lines[entries[0]] = entry
        else:
            additions.append(entry)
    if additions:
        block_end = next((i for i in range(start, end) if lines[i] == END), None)
        if block_end is not None:
            lines[block_end:block_end] = additions
        else:
            lines[end:end] = [BEGIN, *additions, END]
    return '\n'.join(lines).rstrip() + '\n'
