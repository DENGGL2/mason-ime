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
import traceback
import uuid
import mason_paths
import settings_core as core
from dictionary_updater import read_update_state, state_text, update_dictionary, latest_release
import application_updater
import update_state
import windows_notifications


def emit(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def state():
    rime_dir = core.get_rime_dir()
    return dict(cfg=core.load_config(rime_dir), rime_dir=rime_dir,
                weasel_dir=core.get_weasel_dir(), update_status=state_text(read_update_state(rime_dir)),
                update_flags=update_state.flags(rime_dir, project_info()['version']), **mason_paths.folders(__file__))


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


def project_info():
    info_path = Path(__file__).resolve().parent.parent / 'ui/project-info.json'
    return json.loads(info_path.read_text(encoding='utf-8-sig'))


def check_application_update():
    return application_updater.check(project_info())['message']


def notify_updates(rime_dir, app_dir):
    for kind in ('application', 'dictionary'):
        try:
            update_state.notify_once(rime_dir, kind, lambda item, version: windows_notifications.show(app_dir, rime_dir, item, version))
        except Exception as error:
            log_update_error('notification', error)


def log_update_error(action, error):
    try:
        root = Path(mason_paths.folders(__file__)['log_dir']) / '更新记录'
        path = root / (action + '-' + uuid.uuid4().hex[:12] + '.json')
        update_state.atomic_text(path, json.dumps({'action': action, 'time': update_state.now_text(),
                                                 'message': str(error), 'details': traceback.format_exc()},
                                                ensure_ascii=False, indent=2))
    except Exception:
        pass


def check_dictionary_availability(rime_dir):
    release = latest_release()
    installed = read_update_state(rime_dir)
    available = str(installed.get('release_tag') or '').removeprefix('v') != release['tag'].removeprefix('v')
    update_state.record(rime_dir, 'dictionary', release, available)
    return release


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    action = sys.argv[1] if len(sys.argv)>1 else 'state'
    try:
        payload = json.loads(sys.stdin.read() or '{}')
        if action in ('check_update', 'watch_updates', 'download_update', 'launch_update'):
            rime_dir = core.get_rime_dir()
            folders = mason_paths.folders(__file__)
            info = project_info()
            current_dictionary = read_update_state(rime_dir).get('release_tag', '')
            update_state.refresh_local(rime_dir, info['version'], current_dictionary)
            if action == 'check_update':
                result = application_updater.check(info)
                update_state.record(rime_dir, 'application', result['release'], result['has_update'])
                notify_updates(rime_dir, folders['app_dir'])
                emit(dict(type='result', ok=True, message=result['message'], data=result))
            elif action == 'download_update':
                result = application_updater.prepare(info, rime_dir, folders['app_dir'], str(payload['version']),
                    progress=lambda item: emit(dict(type='progress', **item)))
                emit(dict(type='result', ok=True, message='更新已下载并校验完成。', data=result))
            elif action == 'launch_update':
                result = application_updater.launch(rime_dir, folders['app_dir'], payload['prepared_path'], int(payload['settings_pid']))
                emit(dict(type='result', ok=True, message='正在打开覆盖升级窗口…', data=result))
            else:
                if update_state.claim_background_check(rime_dir):
                    for kind in ('application', 'dictionary'):
                        try:
                            if kind == 'application':
                                result = application_updater.check(info)
                                update_state.record(rime_dir, kind, result['release'], result['has_update'])
                            else:
                                check_dictionary_availability(rime_dir)
                        except Exception as error:
                            log_update_error('check-' + kind, error)
                notify_updates(rime_dir, folders['app_dir'])
                emit(dict(type='result', ok=True, data=update_state.flags(rime_dir, info['version'])))
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
            release = {}
            def dictionary_release():
                release.update(check_dictionary_availability(rime))
                return release
            result=update_dictionary(rime,weasel,deploy_fn=core.deploy,
                                    progress=lambda msg:emit(dict(type='progress',message=msg)),
                                    release_provider=dictionary_release,
                                    before_apply=lambda:core.stop_server(weasel),
                                    after_apply=lambda:core.start_server(weasel))
            update_state.record(rime, 'dictionary', release, False)
            message=result.message
        elif action=='migrate':
            message=migrate(rime,payload['target'],weasel)
        else:
            raise RuntimeError('不支持的操作。')
        emit(dict(type='result',ok=True,message=message,data=state()))
        return 0
    except Exception as exc:
        if action in ('update', 'check_update', 'watch_updates', 'download_update', 'launch_update'):
            log_update_error(action, exc)
        emit(dict(type='result',ok=False,message=str(exc)))
        return 1


if __name__=='__main__':
    sys.exit(main())
