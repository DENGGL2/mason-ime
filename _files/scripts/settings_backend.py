"""Native settings window's JSON bridge. No GUI or third-party dependencies."""
import copy
import json
import os
from pathlib import Path
import shutil
import sys
import winreg
import urllib.request
import urllib.parse
import urllib.error
import mason_paths
import settings_core as core
from dictionary_updater import read_update_state, state_text, update_dictionary


def emit(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def state():
    rime_dir = core.get_rime_dir()
    return dict(cfg=core.load_config(rime_dir), rime_dir=rime_dir,
                weasel_dir=core.get_weasel_dir(), update_status=state_text(read_update_state(rime_dir)), **mason_paths.folders(__file__))


def validate(cfg):
    value = copy.deepcopy(core.DEFAULT_CONFIG)
    value.update({key: cfg[key] for key in value if key in cfg})
    value['ui_theme'] = value['ui_theme'] if value['ui_theme'] in ('system','light','dark') else 'light'
    value['radius'] = max(0, min(20, int(value['radius'])))
    value['fontsize'] = max(11, min(24, int(value['fontsize'])))
    value['horizontal'] = bool(value['horizontal'])
    core.fuzzy_settings.validate(value)
    core.correction_settings.validate(value)
    core.fuzzy_settings.validate(value)
    for key in ('custom_bg','custom_fg','custom_hl'):
        if not core.re.fullmatch(r'#[0-9a-fA-F]{6}',value[key]):
            raise ValueError('颜色格式应为 #RRGGBB。')
    if not core.re.fullmatch(r'[A-Za-z0-9_\-]+',str(value['scheme'])):
        raise ValueError('主题名称格式无效。')
    return value


def migrate(current, target, weasel):
    current,target = Path(current).resolve(),Path(target).resolve()
    if target == current or current in target.parents or target in current.parents:
        raise RuntimeError('新位置与当前词库目录不能相同，也不能互相包含。')
    if target.exists() and any(target.iterdir()):
        raise RuntimeError('新位置已有文件，请选择空目录，避免覆盖。')
    changed=False
    try:
        core.stop_server(weasel)
        shutil.copytree(current,target,dirs_exist_ok=True)
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER,r'Software\Rime\Weasel') as key:
            winreg.SetValueEx(key,'RimeUserDir',0,winreg.REG_SZ,str(target))
        changed=True
        ok,error=core.deploy(weasel)
        if not ok:
            raise RuntimeError(error)
        return '词库位置已更新，原目录完整保留。'
    except Exception:
        if changed:
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER,r'Software\Rime\Weasel') as key:
                winreg.SetValueEx(key,'RimeUserDir',0,winreg.REG_SZ,str(current))
        raise
    finally:
        core.start_server(weasel)


def check_application_update():
    info_path = Path(__file__).resolve().parent.parent / 'ui/project-info.json'
    info = json.loads(info_path.read_text(encoding='utf-8-sig'))
    address = str(info.get('github_url', '')).strip()
    if not address:
        return '尚未配置 GitHub 更新地址。项目发布后即可检查新版。'
    match = core.re.fullmatch(r'https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/?', address)
    if not match:
        raise RuntimeError('GitHub 更新地址格式无效。')
    api = 'https://api.github.com/repos/' + match[1] + '/' + match[2] + '/releases/latest'
    request = urllib.request.Request(api, headers={'Accept':'application/vnd.github+json','User-Agent':'Mason/' + str(info['version'])})
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read(2 * 1024 * 1024 + 1)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return 'GitHub 尚未发布可用版本。'
        if error.code in (403, 429) and error.headers.get('X-RateLimit-Remaining') == '0':
            # The public latest-release redirect selects stable releases without API quota.
            fallback = urllib.request.Request(address.rstrip('/') + '/releases/latest',
                headers={'User-Agent': 'Mason/' + str(info['version'])}, method='HEAD')
            with urllib.request.urlopen(fallback, timeout=15) as response:
                destination = urllib.parse.urlsplit(response.geturl())
            prefix = '/' + match[1] + '/' + match[2] + '/releases/tag/'
            if destination.scheme != 'https' or destination.netloc.lower() != 'github.com' or not destination.path.startswith(prefix):
                raise RuntimeError('更新下载地址格式无效。')
            latest = urllib.parse.unquote(destination.path[len(prefix):]).removeprefix('v')
        else:
            raise RuntimeError('检查更新未完成：HTTP ' + str(error.code)) from error
    else:
        if len(raw) > 2 * 1024 * 1024:
            raise RuntimeError('更新信息过大。')
        release = json.loads(raw.decode('utf-8'))
        latest = str(release.get('tag_name','')).removeprefix('v')
    def version(value):
        if not core.re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', value):
            raise RuntimeError('更新版本信息格式无效。')
        parts = tuple(int(part) for part in value.split('.'))
        return parts + (0,) * (4 - len(parts))
    if version(latest) > version(str(info['version'])):
        return '发现新版本 ' + latest + '，当前版本 ' + str(info['version']) + '。\n' + address + '/releases/latest'
    return '当前版本 ' + str(info['version']) + ' 已是最新。'


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    action = sys.argv[1] if len(sys.argv)>1 else 'state'
    try:
        payload = json.loads(sys.stdin.read() or '{}')
        if action == 'check_update':
            emit(dict(type='result', ok=True, message=check_application_update()))
            return 0
        initial = state()
        rime,weasel = initial['rime_dir'],initial['weasel_dir']
        if action=='state':
            emit(dict(type='result',ok=True,data=initial))
            return 0
        if not weasel or not os.path.isdir(rime):
            raise RuntimeError('请先运行 Mason.Install.exe 安装麻绳输入法。')
        message=''
        if action=='save':
            cfg=validate(payload['cfg'])
            core.save_config(rime,cfg)
            ok,error=core.deploy(weasel)
            if not ok:
                raise RuntimeError('设置已保存，部署未完成：'+error)
            message='设置已应用。'
        elif action=='deploy':
            ok,error=core.deploy(weasel)
            if not ok: raise RuntimeError(error)
            message='重新部署完成。'
        elif action=='update':
            result=update_dictionary(rime,weasel,deploy_fn=core.deploy,
                                    progress=lambda msg:emit(dict(type='progress',message=msg)),
                                    before_apply=lambda:core.stop_server(weasel),
                                    after_apply=lambda:core.start_server(weasel))
            message=result.message
        elif action=='migrate':
            message=migrate(rime,payload['target'],weasel)
        else:
            raise RuntimeError('不支持的操作。')
        emit(dict(type='result',ok=True,message=message,data=state()))
        return 0
    except Exception as exc:
        emit(dict(type='result',ok=False,message=str(exc)))
        return 1


if __name__=='__main__':
    sys.exit(main())
