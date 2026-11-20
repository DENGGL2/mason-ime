"""Regression tests against the installed Rime DLL, using isolated dictionaries."""
import argparse
import ctypes
import json
from pathlib import Path
import shutil
import subprocess
import sys
from rime_runtime import fixture, Rime

PROGRAM = Path(r'D:\Program Files (x86)\Mason\程序')
SOURCE = Path(__file__).resolve().parents[3]
MARKER = '\u2063'


def check(value, message):
    if not value:
        raise AssertionError(message)


def history(user):
    path = user / 'mason-learning.tsv'
    return path.read_text(encoding='utf-8').splitlines() if path.exists() else []


def count(user, code, text=None):
    return sum(line.split('\t')[2] == code and (text is None or line.split('\t')[3] == text)
               for line in history(user))


def choose(rime, index):
    check(rime.lib.RimeSelectCandidate(rime.session, index), 'Candidate selection failed.')
    return rime.commit()


def choose_text(rime, text, max_pages=32):
    for unused in range(max_pages):
        context=rime.context()
        for index,candidate in enumerate(context['candidates']):
            if candidate['text']==text:
                return choose(rime,context['page']*context['page_size']+index)
        if context['last_page']:
            break
        rime.key('=')
    raise AssertionError('Requested candidate disappeared: '+text)


def controlled_fixture(user, flexible):
    check(not user.exists(), 'Preserve previous fixture and use a new test directory.')
    (user / 'build').mkdir(parents=True)
    (user / 'logs').mkdir()
    (user / 'lua').mkdir()
    for name in ('mason_learning.lua', 'mason_preedit.lua', 'mason_candidates.lua', 'mason_correction_guard.lua'):
        shutil.copy2(SOURCE / '_files/rime/lua' / name, user / 'lua' / name)
    (user / 'lua/learning_proof.lua').write_text('''
local M = {}
function M.func(input, segment, env)
  for i=0,139 do
    local text = i==0 and '默认词' or i==1 and '常用词' or '备选' .. string.format('%03d',i)
    local cand = Candidate('table',segment.start,segment._end,text,'')
    cand.preedit,cand.quality=input,1.2-i*.001;yield(cand)
  end
end
return M
''', encoding='utf-8')
    (user / 'build/default.yaml').write_text('schema_list:\n  - schema: rime_frost\nmenu:\n  page_size: 8\n',encoding='utf-8')
    (user / 'build/rime_frost.schema.yaml').write_text('''
schema:
  schema_id: rime_frost
  name: Mason private learning proof
  version: '1'
switches:
  - name: ascii_mode
    reset: 0
  - name: traditionalization
    reset: 0
  - name: mars
    reset: 0
  - name: chinese_english
    reset: 0
  - name: emoji
    reset: 0
engine:
  processors:
    - lua_processor@*mason_preedit
    - lua_processor@*mason_correction_guard
    - ascii_composer
    - recognizer
    - key_binder
    - speller
    - selector
    - navigator
    - express_editor
  segmentors:
    - ascii_segmentor
    - abc_segmentor
    - fallback_segmentor
  translators:
    - lua_translator@*learning_proof
  filters:
    - lua_filter@*mason_candidates
speller:
  alphabet: abcdefghijklmnopqrstuvwxyz
  delimiter: " '"
key_binder:
  bindings:
    - {when: has_menu, accept: semicolon, send: '2'}
    - {when: has_menu, accept: equal, send: Page_Down}
    - {when: has_menu, accept: minus, send: Page_Up}
mason_correction:
  enabled: true
mason_learning:
  flexible: FLEXIBLE
'''.replace('FLEXIBLE', str(flexible).lower()), encoding='utf-8')


def run_real(user):
    fixture(user, PROGRAM.parent, SOURCE)
    # Native cloud DLL loading depends on the host executable; this local-only
    # fixture intentionally has inert cloud callbacks and never sends requests.
    (user / 'rime.lua').write_text('cloud_pinyin_processor=function() return 2 end\ncloud_pinyin_translator=function() end\n',encoding='utf-8')
    rime = Rime(PROGRAM, user)
    result = {}
    try:
        for code, expected in (('shh', '时候'), ('shid', '是的')):
            context = rime.type(code)
            check(MARKER not in context['preedit'], code + ' was incorrectly marked red.')
            check(not context['candidates'][0]['text'] in ('书', '谁'), code + ' was displaced by correction.')
            # Locate the requested abbreviation in the actual lazy dictionary.
            found = False
            for page in range(32):
                context = rime.context()
                for index, cand in enumerate(context['candidates']):
                    if cand['text'] == expected:
                        found = True
                        selected = choose(rime, context['page'] * context['page_size'] + index)
                        check(selected == expected, 'Wrong abbreviation commit.')
                        break
                if found or context['last_page']:
                    break
                rime.key('=')
            check(found, code + ' no longer matches ' + expected)
            check(count(user, code, expected) == 1, 'First abbreviation choice was not recorded once.')
            context = rime.type(code)
            found = False
            for page in range(32):
                context = rime.context()
                for index, cand in enumerate(context['candidates']):
                    if cand['text'] == expected:
                        found = True
                        check(choose(rime, context['page'] * context['page_size'] + index) == expected, 'Second choice committed wrong text.')
                        break
                if found or context['last_page']:
                    break
                rime.key('=')
            check(found and count(user, code, expected) == 2, 'Second abbreviation choice was lost.')
            context = rime.type(code)
            check(context['candidates'][0]['text'] == expected, 'Two choices did not pin ' + expected)
            check(MARKER not in context['preedit'], 'Learned abbreviation became red.')
            result[code] = dict(first=context['candidates'][0]['text'],preedit=context['preedit'],selections=2)
            rime.clear()
        context = rime.type('nigapsuwo')
        check(context['candidates'][0]['text'] == '你告诉我', 'Genuine keyboard typo correction regressed.')
        check('p' + MARKER in context['preedit'], 'Wrong p lost its red metadata.')
        for unused in range(2):
            check(choose(rime, 0) == '你告诉我', 'Corrected choice commit regressed.')
            context = rime.type('nigapsuwo')
        check(context['candidates'][0]['text'] == '你告诉我' and 'p' + MARKER in context['preedit'],
              'Learned corrected choice lost its original typo marker.')
        result['nigapsuwo'] = dict(first='你告诉我',red_letter='p',selections=count(user,'nigapsuwo'))
        rime.clear()
        check(count(user, 'nihao') == 0, 'Fixture is not isolated.')
        rime.type('nihao');rime.key(' ');rime.commit()
        check(count(user,'nihao') == 0, 'Accepting automatic first candidate was counted.')
        rime.type('nihao');rime.key(';');rime.commit()
        check(count(user,'nihao') == 1, 'Semicolon selection was not counted exactly once.')
        result['automatic_first_ignored'] = True
        return result
    finally:
        rime.close()


def run_controlled(user, flexible):
    controlled_fixture(user, flexible)
    rime = Rime(PROGRAM, user)
    try:
        if flexible:
            for unused in range(2):
                rime.type('shh');check(choose_text(rime, '备选030') == '备选030','Frequency choice failed.')
            context = rime.type('shh')
            check(context['candidates'][0]['text'] == '默认词', 'Flexible mode incorrectly applied two-choice hard pinning.')
            rime.clear()
            # Repeated explicit selection changes weight without a fixed count threshold.
            for unused in range(40):
                rime.type('shh')
                check(choose_text(rime,'备选030')=='备选030','Frequency choice failed.')
            context=rime.type('shh')
            check(context['candidates'][0]['text']=='备选030','Repeated frequency did not improve ordering.')
            return dict(two_choices_not_forced=True,weighted_first='备选030',count=count(user,'shh','备选030'))
        context=rime.type('shh')
        check(context['candidates'][0]['text']=='默认词','Initial order changed.')
        check(choose(rime,1)=='常用词','First choice failed.')
        check(count(user,'shh','常用词')==1,'First click counted twice.')
        context=rime.type('shh')
        check(context['candidates'][0]['text']=='默认词','Single choice was hard pinned.')
        rime.key('2');check(rime.commit()=='常用词','Numeric selection failed.')
        check(count(user,'shh','常用词')==2,'Numeric choice was not counted once.')
        context=rime.type('shh')
        check(context['candidates'][0]['text']=='常用词','Second choice was not pinned.')
        check(len({c['text'] for c in context['candidates']})==len(context['candidates']), 'Pinned choice duplicated.')
        rime.key(' ');rime.commit()
        check(count(user,'shh','常用词')==2,'Automatic learned first choice incorrectly changed history.')
        context=rime.type('shid')
        check(context['candidates'][0]['text']=='默认词','Preference leaked to another code.')
        rime.key('=');rime.key('-');rime.key(0xff1b)
        check(count(user,'shid')==0,'Browsing or cancellation was counted.')
        rime.type('shid');rime.key(0xff54);rime.key(' ');rime.commit()
        check(count(user,'shid','常用词')==1,'Arrow plus confirmation was not counted once.')
        rime.type('shid');rime.key(0xff0d,1);rime.commit()
        check(count(user,'shid')==1,'Literal pinyin commit was counted.')
        rime.type('shid')
        rime.lib.RimeCommitComposition.argtypes=[ctypes.c_size_t]
        rime.lib.RimeCommitComposition(rime.session);rime.commit()
        check(count(user,'shid')==1,'Host blur commit was counted.')
        for unused in range(2):
            rime.type('abc');check(choose(rime,120)=='备选120','Distant candidate selection failed.')
        context=rime.type('abc')
        check(context['candidates'][0]['text']=='备选120','Distant learned choice was not recovered.')
        rime.clear()
        rime.lib.RimeSetOption(rime.session,b'traditionalization',1)
        context=rime.type('shh')
        check(context['candidates'][0]['text']=='默认词','Preference leaked across conversion modes.')
        return dict(one_choice_not_forced=True,two_choices_first='常用词',
                    distant_choice_first='备选120',automatic_and_cancel_ignored=True,code_and_mode_isolated=True)
    finally:
        rime.close()


def run_restart(user):
    rime=Rime(PROGRAM,user)
    try:
        context=rime.type('shh')
        check(context['candidates'][0]['text']=='常用词','Learning did not survive a new engine process.')
        return dict(persistent_first='常用词',count=count(user,'shh','常用词'))
    finally:
        rime.close()


def main():
    global PROGRAM
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',required=True)
    parser.add_argument('--program',default=str(PROGRAM),help='Installed Mason program directory containing rime.dll.')
    parser.add_argument('--case',choices=['real','hard','flexible','restart'])
    args=parser.parse_args()
    PROGRAM=Path(args.program).resolve()
    root=Path(args.output).resolve()
    if args.case:
        user=root/('hard' if args.case=='restart' else args.case)
        functions={'real':lambda:run_real(user),'hard':lambda:run_controlled(user,False),
                   'flexible':lambda:run_controlled(user,True),'restart':lambda:run_restart(user)}
        print(json.dumps(functions[args.case](),ensure_ascii=False))
        return
    check(not root.exists(),'Choose a fresh result directory; no test data is deleted.')
    root.mkdir(parents=True)
    results={}
    for name in ('real','hard','flexible','restart'):
        child=subprocess.run([sys.executable,'-X','utf8',str(Path(__file__).resolve()),
                              '--output',str(root),'--program',str(PROGRAM),'--case',name],capture_output=True,text=True,encoding='utf-8')
        (root/(name+'-stderr.txt')).write_text(child.stderr,encoding='utf-8')
        if child.returncode:
            raise RuntimeError(name+': '+child.stdout+'\n'+child.stderr)
        results[name]=json.loads(child.stdout)
    (root/'result.json').write_text(json.dumps(results,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(results,ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
