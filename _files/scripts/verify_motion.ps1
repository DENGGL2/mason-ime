param(
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [Parameter(Mandatory=$true)][string]$TestDir,
    [switch]$SettingsOnly
)
$ErrorActionPreference='Stop'
$taskPackage=[IO.Path]::GetFullPath($PackageDir)
$taskTest=[IO.Path]::GetFullPath($TestDir)
if(Test-Path -LiteralPath $taskTest){throw '请使用一个新的验证目录，保留已有验证结果。'}
[IO.Directory]::CreateDirectory($taskTest)|Out-Null
$taskUi=Join-Path $taskTest 'ui'
$taskMock=Join-Path $taskTest 'mock'
[IO.Directory]::CreateDirectory($taskUi)|Out-Null
[IO.Directory]::CreateDirectory($taskMock)|Out-Null
[IO.Directory]::CreateDirectory((Join-Path $taskUi 'icons'))|Out-Null
& (Join-Path $taskPackage '_files\scripts\build_settings.ps1') -OutputPath (Join-Path $taskUi 'Mason.Settings.exe')
if(-not $SettingsOnly){& (Join-Path $taskPackage '_files\scripts\build_native_text.ps1') -PackageDir $taskPackage -BuildDir (Join-Path $taskTest 'native-build')}
foreach($taskName in @('appearance.svg','input.svg','guide.svg')){
    Copy-Item -LiteralPath (Join-Path $taskPackage ('_files\ui\icons\'+$taskName)) -Destination (Join-Path $taskUi ('icons\'+$taskName))
}
Copy-Item -LiteralPath (Join-Path $taskPackage '_files\ui\project-info.json') -Destination (Join-Path $taskUi 'project-info.json')
Copy-Item -LiteralPath (Join-Path $taskPackage '_files\ui\Mason.Settings.exe.config') -Destination (Join-Path $taskUi 'Mason.MotionProof.exe.config')
$taskBackend=@'
from pathlib import Path
import json,sys
json.loads(sys.stdin.read() or '{}')
assert sys.argv[1]=='state'
print(json.dumps(dict(type='result',ok=True,data=json.loads(Path(__file__).with_name('state.json').read_text(encoding='utf-8')))))
'@
[IO.File]::WriteAllText((Join-Path $taskMock 'private_backend.py'),$taskBackend,[Text.UTF8Encoding]::new($false))
$env:MASON_MOTION_TEST_PACKAGE=$taskPackage
$env:MASON_MOTION_TEST_ROOT=$taskMock
$taskPrepare=@'
import sys,os,json
from pathlib import Path
repo=Path(os.environ['MASON_MOTION_TEST_PACKAGE'])
root=Path(os.environ['MASON_MOTION_TEST_ROOT'])
sys.path.insert(0,str(repo/'_files/scripts'))
import settings_core as core
program=root/'program';user=root/'user'
program.mkdir();user.mkdir()
state=dict(cfg=core.DEFAULT_CONFIG.copy(),rime_dir=str(user),weasel_dir=str(program),program_dir=str(program),app_dir=str(root),log_dir=str(root/'logs'),backup_dir=str(root/'backups'),update_status='Not checked')
(root/'state.json').write_text(json.dumps(state),encoding='utf-8')
'@
$taskPrepare | python -
if($LASTEXITCODE -ne 0){throw '隔离设置数据创建失败。'}
$taskFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskWpf=Join-Path $taskFramework 'WPF'
$taskArguments=@('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:'+(Join-Path $taskUi 'Mason.MotionProof.exe')),('/r:'+(Join-Path $taskUi 'Mason.Settings.exe')),'/r:System.dll','/r:System.Core.dll','/r:System.Xaml.dll','/r:System.Web.Extensions.dll')
foreach($taskName in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')){$taskArguments+='/r:'+(Join-Path $taskWpf $taskName)}
$taskArguments+=(Join-Path $taskPackage '_files\ui\tests\MasonMotionProof.cs')
& (Join-Path $taskFramework 'csc.exe') @taskArguments
if($LASTEXITCODE -ne 0){throw '设置动效验证程序编译失败。'}
$taskVcRoot=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC'
$taskVc=(Get-ChildItem -LiteralPath $taskVcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$taskVersion=(Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$taskResults=[Collections.Generic.List[object]]::new()
foreach($taskArch in $(if($SettingsOnly){@()}else{@('x64','x86')})){
    $taskNative=Join-Path $taskTest ('native-'+$taskArch)
    [IO.Directory]::CreateDirectory($taskNative)|Out-Null
    $env:LIB=$taskVc+'\lib\'+$taskArch+';'+$taskSdk+'\Lib\'+$taskVersion+'\ucrt\'+$taskArch+';'+$taskSdk+'\Lib\'+$taskVersion+'\um\'+$taskArch
    & (Join-Path $taskVc ('bin\Hostx64\'+$taskArch+'\cl.exe')) /nologo /EHsc /MT /utf-8 /std:c++17 /O2 /DUNICODE /D_UNICODE ('/Fe:'+(Join-Path $taskNative 'WeaselServer.exe')) ('/Fo:'+(Join-Path $taskNative 'MasonCandidateMotionProof.obj')) (Join-Path $taskPackage '_files\native\tests\MasonCandidateMotionProof.cpp') /link /SUBSYSTEM:WINDOWS
    if($LASTEXITCODE -ne 0){throw ('候选窗动效验证程序编译失败：'+$taskArch)}
    $taskResult=Join-Path $taskNative 'result.json'
    $taskArgument='"'+(Join-Path $taskPackage ('_files\native\'+$taskArch+'\Mason.dll'))+'" "'+$taskResult+'"'
    $taskProcess=Start-Process -FilePath (Join-Path $taskNative 'WeaselServer.exe') -ArgumentList $taskArgument -WindowStyle Hidden -PassThru
    try{if(-not $taskProcess.WaitForExit(15000)){throw '候选窗验证超时。'};$taskExit=$taskProcess.ExitCode}finally{$taskProcess.Dispose()}
    $taskResultData=ConvertFrom-Json ([IO.File]::ReadAllText($taskResult,[Text.Encoding]::UTF8))
    if($taskExit -ne 0 -or $taskResultData.status -ne 'passed'){throw ($taskArch+'：'+$taskResultData.error)}
    $taskResults.Add([PSCustomObject]@{Component='candidate';Architecture=$taskArch;Result=$taskResultData})
}
$taskResult=Join-Path $taskTest 'wpf-result.json'
$taskArgument='"'+(Join-Path $taskMock 'private_backend.py')+'" "'+$taskResult+'"'
$taskProcess=Start-Process -FilePath (Join-Path $taskUi 'Mason.MotionProof.exe') -ArgumentList $taskArgument -WindowStyle Hidden -PassThru
try{if(-not $taskProcess.WaitForExit(20000)){throw '设置窗口验证超时。'};$taskExit=$taskProcess.ExitCode}finally{$taskProcess.Dispose()}
$taskResultData=ConvertFrom-Json ([IO.File]::ReadAllText($taskResult,[Text.Encoding]::UTF8))
if($taskExit -ne 0 -or $taskResultData.status -ne 'passed'){throw $taskResultData.error}
$taskResults.Add([PSCustomObject]@{Component='settings';Result=$taskResultData})
$taskSummary=[PSCustomObject]@{Status='passed';PersonalUserDataOpened=$false;SystemInputPreferencesModified=$false;Results=$taskResults}
[IO.File]::WriteAllText((Join-Path $taskTest 'summary.json'),($taskSummary|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$taskResults|Select-Object Component,Architecture,@{Name='Status';Expression={$_.Result.status}}|Format-Table -AutoSize
