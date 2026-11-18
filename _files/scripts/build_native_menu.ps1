param([Parameter(Mandatory=$true)][string]$PackageDir,[Parameter(Mandatory=$true)][string]$BuildDir)
$ErrorActionPreference='Stop'
$taskResources=Join-Path $PackageDir '_files'
if(-not(Test-Path -LiteralPath $taskResources -PathType Container)){$taskResources=Join-Path $PackageDir '程序\资源'}
$taskNative=Join-Path $taskResources 'native'
$taskVcRoot=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC'
$taskVc=(Get-ChildItem -LiteralPath $taskVcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$taskSdkVersion=(Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
if(-not $taskVc -or -not $taskSdkVersion){throw '编译原生菜单入口需要现有的 Visual C++ Build Tools 和 Windows SDK。'}
[IO.Directory]::CreateDirectory($BuildDir)|Out-Null
$env:INCLUDE=$taskVc+'\include;'+$taskSdk+'\Include\'+$taskSdkVersion+'\ucrt;'+$taskSdk+'\Include\'+$taskSdkVersion+'\um;'+$taskSdk+'\Include\'+$taskSdkVersion+'\shared'
foreach($taskArchitecture in @('x64','x86')){
    $taskOutput=Join-Path $taskNative $taskArchitecture
    [IO.Directory]::CreateDirectory($taskOutput)|Out-Null
    $env:LIB=$taskVc+'\lib\'+$taskArchitecture+';'+$taskSdk+'\Lib\'+$taskSdkVersion+'\ucrt\'+$taskArchitecture+';'+$taskSdk+'\Lib\'+$taskSdkVersion+'\um\'+$taskArchitecture
    $taskResource=Join-Path $BuildDir ('MasonMenuBridge-'+$taskArchitecture+'.res')
    & (Join-Path $taskSdk ('bin\'+$taskSdkVersion+'\x64\rc.exe')) /nologo ('/fo'+$taskResource) (Join-Path $taskNative 'MasonMenuBridge.rc')
    if($LASTEXITCODE -ne 0){throw '菜单入口版本资源编译失败。'}
    & (Join-Path $taskVc ('bin\Hostx64\'+$taskArchitecture+'\cl.exe')) /nologo /LD /EHsc /MT /utf-8 ('/Fe:'+(Join-Path $taskOutput 'Mason.Menu.dll')) ('/Fo:'+(Join-Path $BuildDir ('MasonMenuBridge-'+$taskArchitecture+'.obj'))) (Join-Path $taskNative 'MasonMenuBridge.cpp') $taskResource /link ('/DEF:'+(Join-Path $taskNative 'MasonMenuBridge.def')) ('/IMPLIB:'+(Join-Path $BuildDir ('MasonMenuBridge-'+$taskArchitecture+'.lib'))) user32.lib
    if($LASTEXITCODE -ne 0){throw ('原生菜单入口编译失败：'+$taskArchitecture)}
}
