param([string]$PackageDir)
$ErrorActionPreference='Stop'
if(-not $PackageDir){
    $taskResources=Split-Path $PSScriptRoot -Parent
    $PackageDir=Split-Path $taskResources -Parent
    if((Split-Path $taskResources -Leaf) -eq '资源'){$PackageDir=Split-Path $PackageDir -Parent}
}
$taskResources=Join-Path $PackageDir '_files'
if(-not(Test-Path -LiteralPath $taskResources -PathType Container)){$taskResources=Join-Path $PackageDir '程序\资源'}
foreach($taskScript in @(Get-ChildItem -LiteralPath (Join-Path $taskResources 'scripts') -Filter '*.ps1' -File)){
    $taskData=[IO.File]::ReadAllBytes($taskScript.FullName)
    $taskNonAscii=@($taskData | Where-Object {$_ -gt 127}).Count -gt 0
    $taskBom=$taskData.Length -ge 3 -and $taskData[0] -eq 239 -and $taskData[1] -eq 187 -and $taskData[2] -eq 191
    if($taskNonAscii -and -not $taskBom){throw ('脚本须保存为带 BOM 的 UTF-8：'+$taskScript.Name)}
    $taskTokens=$null;$taskErrors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($taskScript.FullName,[ref]$taskTokens,[ref]$taskErrors)
    if($taskErrors.Count -gt 0){throw ('脚本语法检查失败：'+$taskScript.Name+' '+$taskErrors[0].Message)}
}
$taskUi=Join-Path $taskResources 'ui'
& (Join-Path $taskResources 'scripts\build_native_tools.ps1') -UiDir $taskUi
$taskFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if(-not(Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))){$taskFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'}
$taskArgs=@('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:'+(Join-Path $PackageDir 'Mason.Install.exe')),('/win32manifest:'+(Join-Path $taskUi 'Mason.Install.manifest')),'/r:System.dll','/r:System.Core.dll','/r:System.Xaml.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
foreach($taskName in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')){$taskArgs+='/r:'+(Join-Path $taskFramework ('WPF\'+$taskName))}
$taskArgs+=@((Join-Path $taskUi 'MasonInstall.cs'),(Join-Path $taskUi 'MasonInstallCore.cs'),(Join-Path $taskUi 'MasonNativeUi.cs'),(Join-Path $taskUi 'MasonPaths.cs'))
$taskArgs+='/win32icon:'+(Join-Path $taskUi 'Mason.ico')
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if($LASTEXITCODE -ne 0){throw '安装小窗编译失败。'}
foreach($taskBuild in @(@{Source='MasonDeployer.cs';Output='Mason.Deployer.exe';Manifest='Mason.Deployer.manifest'},@{Source='MasonServer.cs';Output='Mason.Server.exe';Manifest=''})){
    $taskProxy=@('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:'+(Join-Path $taskUi $taskBuild.Output)),'/r:System.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll',(Join-Path $taskUi $taskBuild.Source),(Join-Path $taskUi 'MasonPaths.cs'))
    if($taskBuild.Manifest){$taskProxy+='/win32manifest:'+(Join-Path $taskUi $taskBuild.Manifest)}
    if($taskBuild.Source -eq 'MasonDeployer.cs'){$taskProxy+=@((Join-Path $taskUi 'MasonNativeMenu.cs'),(Join-Path $taskUi 'MasonModes.cs'))}
    $taskProxy+='/win32icon:'+(Join-Path $taskUi 'Mason.ico')
    & (Join-Path $taskFramework 'csc.exe') @taskProxy
    if($LASTEXITCODE -ne 0){throw ('入口编译失败：'+$taskBuild.Output)}
}

$taskCompatibility=@('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:'+(Join-Path $taskUi 'Mason.Compatibility.exe')),'/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll','/r:System.Web.Extensions.dll',(Join-Path $taskUi 'MasonNativeUi.cs'),(Join-Path $taskUi 'MasonNativeCompatibility.cs'))
& (Join-Path $taskFramework 'csc.exe') @taskCompatibility
if($LASTEXITCODE -ne 0){throw '中文目录兼容性工具编译失败。'}

$taskTray=@('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:'+(Join-Path $taskUi 'Mason.Tray.exe')),('/win32manifest:'+(Join-Path $taskUi 'Mason.Tray.manifest')),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',(Join-Path $taskUi 'MasonTray.cs'),(Join-Path $taskUi 'MasonModes.cs'),(Join-Path $taskUi 'MasonPaths.cs'),('/win32icon:'+(Join-Path $taskUi 'Mason.ico')))
& (Join-Path $taskFramework 'csc.exe') @taskTray
if($LASTEXITCODE -ne 0){throw '托盘界面编译失败。'}
$taskInput=@('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:'+(Join-Path $taskUi 'Mason.InputSetup.exe')),'/r:System.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll',(Join-Path $taskUi 'MasonInputSetup.cs'),(Join-Path $taskUi 'MasonPaths.cs'),('/win32icon:'+(Join-Path $taskUi 'Mason.ico')))
& (Join-Path $taskFramework 'csc.exe') @taskInput
if($LASTEXITCODE -ne 0){throw '输入界面配置工具编译失败。'}
