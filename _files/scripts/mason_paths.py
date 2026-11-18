"""Resolve Mason's application folders; environment changes are child-process only."""
from pathlib import Path
import json
import os
import winreg

def app_dir(script_file=__file__):
    for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SOFTWARE\Mason',0,winreg.KEY_READ|view) as key:
                folder=Path(winreg.QueryValueEx(key,'InstallDir')[0])
            receipt=folder/'mason-install.json'
            if receipt.is_file() and json.loads(receipt.read_text(encoding='utf-8-sig')).get('product')=='Mason':
                return folder
        except (OSError,ValueError,TypeError):
            pass
    for folder in Path(script_file).resolve().parents:
        if (folder/'Mason.Install.exe').is_file():
            return folder
    return Path(script_file).resolve().parents[2]

def folders(script_file=__file__):
    root=app_dir(script_file)
    return dict(app_dir=str(root),program_dir=str(root/'程序'),user_dir=str(root/'用户数据'),
                log_dir=str(root/'日志'),backup_dir=str(root/'备份'))

def runtime_env(script_file=__file__):
    paths=folders(script_file)
    log_dir=Path(paths['log_dir'])
    log_dir.mkdir(parents=True,exist_ok=True)
    (log_dir/'rime.weasel').mkdir(exist_ok=True)
    result=os.environ.copy()
    result.update(TEMP=str(log_dir),TMP=str(log_dir),GLOG_log_dir=str(log_dir/'rime.weasel'))
    return result
