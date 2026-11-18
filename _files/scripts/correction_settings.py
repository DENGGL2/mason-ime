"""Persist Mason's correction toggle without replacing other Rime patches."""
import re

DEFAULTS = {'correction_enabled': True}
KEY = 'mason_correction/enabled'
BEGIN = '# mason-correction-settings-begin'
END = '# mason-correction-settings-end'
ENTRY = re.compile(r'^  [\"\']?mason_correction/enabled[\"\']?\s*:\s*(.*?)\s*$')


def validate(cfg):
    if 'correction_enabled' in cfg and not isinstance(cfg['correction_enabled'], bool):
        raise ValueError('纠正输入错误选项必须为开启或关闭。')


def load(text):
    values = DEFAULTS.copy()
    for line in text.splitlines():
        match = ENTRY.match(line)
        if match:
            value = re.split(r'\s+#', match[1], maxsplit=1)[0].strip().lower()
            values['correction_enabled'] = value == 'true'
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
    entries = [i for i in range(start, end) if ENTRY.match(lines[i])]
    if len(entries) > 1:
        raise ValueError('现有输入配置存在重复的纠错开关，原配置已保留。')
    entry = '  ' + KEY + ': ' + str(cfg.get('correction_enabled', True)).lower()
    if entries:
        lines[entries[0]] = entry
    else:
        lines[end:end] = [BEGIN, entry, END]
    return '\n'.join(lines).rstrip() + '\n'
