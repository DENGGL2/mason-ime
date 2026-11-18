param(
    [string]$SourcePath,
    [string]$OutputPath,
    [string]$ManifestPath
)
$ErrorActionPreference = 'Stop'
$taskUi = Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'
if (-not $SourcePath) { $SourcePath = Join-Path $taskUi 'MasonSettings.cs' }
if (-not $OutputPath) { $OutputPath = Join-Path $taskUi 'Mason.Settings.exe' }
if (-not $ManifestPath) { $ManifestPath = Join-Path (Split-Path $SourcePath -Parent) 'Mason.Settings.manifest' }
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) { $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$taskWpf = Join-Path $taskFramework 'WPF'
$taskArgs = @('/nologo','/target:winexe','/optimize+','/codepage:65001',('/out:' + $OutputPath),('/win32manifest:' + $ManifestPath),'/r:System.dll','/r:System.Core.dll','/r:System.Xaml.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll')
foreach ($taskName in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $taskArgs += '/r:' + (Join-Path $taskWpf $taskName) }
$taskArgs += @($SourcePath,(Join-Path (Split-Path $SourcePath -Parent) 'MasonPaths.cs'),(Join-Path (Split-Path $SourcePath -Parent) 'MasonNavigationIcons.cs'),(Join-Path (Split-Path $SourcePath -Parent) 'MasonColorMemory.cs'))
$taskIcon=Join-Path (Split-Path $SourcePath -Parent) 'Mason.ico'
if(Test-Path -LiteralPath $taskIcon){$taskArgs += '/win32icon:'+$taskIcon}
& (Join-Path $taskFramework 'csc.exe') @taskArgs
if ($LASTEXITCODE -ne 0) { throw '设置程序编译失败。' }
