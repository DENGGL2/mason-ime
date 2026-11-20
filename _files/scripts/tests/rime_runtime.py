"""Private-data Rime test client. Never opens UI or uses the live user's dictionary."""
import ctypes as c
import os
from pathlib import Path
import shutil


class Traits(c.Structure):
    _fields_ = [('data_size', c.c_int)] + [(name, c.c_char_p) for name in (
        'shared_data_dir', 'user_data_dir', 'distribution_name',
        'distribution_code_name', 'distribution_version', 'app_name')]
    _fields_ += [('modules', c.POINTER(c.c_char_p)), ('min_log_level', c.c_int),
                ('log_dir', c.c_char_p), ('prebuilt_data_dir', c.c_char_p),
                ('staging_dir', c.c_char_p)]


class Composition(c.Structure):
    _fields_ = [(name, c.c_int) for name in ('length', 'cursor_pos', 'sel_start', 'sel_end')]
    _fields_ += [('preedit', c.c_char_p)]


class Candidate(c.Structure):
    _fields_ = [('text', c.c_char_p), ('comment', c.c_char_p), ('reserved', c.c_void_p)]


class Menu(c.Structure):
    _fields_ = [(name, c.c_int) for name in (
        'page_size', 'page_no', 'is_last_page', 'highlighted_candidate_index', 'num_candidates')]
    _fields_ += [('candidates', c.POINTER(Candidate)), ('select_keys', c.c_char_p)]


class Context(c.Structure):
    _fields_ = [('data_size', c.c_int), ('composition', Composition), ('menu', Menu),
                ('commit_text_preview', c.c_char_p), ('select_labels', c.POINTER(c.c_char_p))]


class Commit(c.Structure):
    _fields_ = [('data_size', c.c_int), ('text', c.c_char_p)]


def fixture(target, installed, source):
    target, installed, source = map(Path, (target, installed, source))
    if target.exists():
        raise RuntimeError('Choose a new private fixture; existing data is preserved.')
    (target / 'build').mkdir(parents=True)
    (target / 'logs').mkdir()
    user = installed / '用户数据'
    for item in (user / 'build').iterdir():
        if item.is_file() and (item.name == 'default.yaml' or item.name.startswith(
                ('rime_frost.', 'rime_frost_aux.', 'radical_pinyin.', 'melt_eng.'))):
            shutil.copy2(item, target / 'build' / item.name)
    for name in ('lua', 'opencc'):
        shutil.copytree(user / name, target / name)
    (target / 'cn_dicts').mkdir()
    shutil.copy2(user / 'cn_dicts/8105.dict.yaml', target / 'cn_dicts/8105.dict.yaml')
    for name in ('rime.lua', 'custom_phrase.txt', 'zh-moqi.gram'):
        if (user / name).is_file():
            shutil.copy2(user / name, target / name)
    # Explicitly use source Mason Lua so the fixture evaluates the staged change.
    for item in (source / '_files/rime/lua').glob('mason_*.lua'):
        shutil.copy2(item, target / 'lua' / item.name)
    return target


class Rime:
    def __init__(self, program, user):
        program, user = Path(program).resolve(), Path(user).resolve()
        self.directory = os.add_dll_directory(str(program))
        self.lib = c.CDLL(str(program / 'rime.dll'))
        specs = {
            'RimeSetup': (None, [c.POINTER(Traits)]),
            'RimeInitialize': (None, [c.POINTER(Traits)]),
            'RimeFinalize': (None, []),
            'RimeCreateSession': (c.c_size_t, []),
            'RimeDestroySession': (c.c_int, [c.c_size_t]),
            'RimeSelectSchema': (c.c_int, [c.c_size_t, c.c_char_p]),
            'RimeSetOption': (None, [c.c_size_t, c.c_char_p, c.c_int]),
            'RimeProcessKey': (c.c_int, [c.c_size_t, c.c_int, c.c_int]),
            'RimeClearComposition': (None, [c.c_size_t]),
            'RimeGetContext': (c.c_int, [c.c_size_t, c.POINTER(Context)]),
            'RimeFreeContext': (None, [c.POINTER(Context)]),
            'RimeGetCommit': (c.c_int, [c.c_size_t, c.POINTER(Commit)]),
            'RimeFreeCommit': (None, [c.POINTER(Commit)]),
            'RimeSelectCandidate': (c.c_int, [c.c_size_t, c.c_size_t]),
        }
        for name, (result, args) in specs.items():
            function = getattr(self.lib, name)
            function.restype, function.argtypes = result, args
        traits = Traits()
        traits.data_size = c.sizeof(Traits) - c.sizeof(c.c_int)
        traits.shared_data_dir = str(user).encode()
        traits.user_data_dir = str(user).encode()
        traits.distribution_name = b'Mason Learning Proof'
        traits.distribution_code_name = b'Mason'
        traits.distribution_version = b'2.4.7'
        traits.app_name = b'mason.learning.proof'
        traits.min_log_level = 1
        traits.log_dir = str(user / 'logs').encode()
        traits.prebuilt_data_dir = str(user / 'build').encode()
        traits.staging_dir = str(user / 'build').encode()
        self.traits = traits
        self.lib.RimeSetup(c.byref(traits))
        self.lib.RimeInitialize(c.byref(traits))
        self.session = self.lib.RimeCreateSession()
        if not self.session or not self.lib.RimeSelectSchema(self.session, b'rime_frost'):
            raise RuntimeError('Private Rime session or schema initialization failed.')
        self.lib.RimeSetOption(self.session, b'ascii_mode', 0)
        self.lib.RimeSetOption(self.session, b'traditionalization', 0)

    def close(self):
        self.lib.RimeDestroySession(self.session)
        self.lib.RimeFinalize()
        self.directory.close()

    def key(self, value, modifiers=0):
        return bool(self.lib.RimeProcessKey(self.session, ord(value) if isinstance(value, str) else value, modifiers))

    def type(self, text):
        for char in text:
            self.key(char)
        return self.context()

    def clear(self):
        self.lib.RimeClearComposition(self.session)
        self.commit()

    def commit(self):
        value = Commit()
        value.data_size = c.sizeof(Commit) - c.sizeof(c.c_int)
        if not self.lib.RimeGetCommit(self.session, c.byref(value)):
            return ''
        text = (value.text or b'').decode()
        self.lib.RimeFreeCommit(c.byref(value))
        return text

    def context(self):
        value = Context()
        value.data_size = c.sizeof(Context) - c.sizeof(c.c_int)
        if not self.lib.RimeGetContext(self.session, c.byref(value)):
            return {'candidates': [], 'preedit': ''}
        result = dict(
            candidates=[dict(text=(value.menu.candidates[i].text or b'').decode(),
                             comment=(value.menu.candidates[i].comment or b'').decode())
                        for i in range(value.menu.num_candidates)],
            preedit=(value.composition.preedit or b'').decode(),
            page=value.menu.page_no, page_size=value.menu.page_size, selected=value.menu.highlighted_candidate_index,
            preview=(value.commit_text_preview or b'').decode(),
            last_page=bool(value.menu.is_last_page))
        self.lib.RimeFreeContext(c.byref(value))
        return result
