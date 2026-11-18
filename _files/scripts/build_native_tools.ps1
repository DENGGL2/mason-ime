param([string]$UiDir)
$ErrorActionPreference='Stop'
if(-not $UiDir){$UiDir=Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'}
$masonFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if(-not(Test-Path -LiteralPath (Join-Path $masonFramework 'csc.exe'))){$masonFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'}
$masonArguments=@('/nologo','/target:library','/optimize+','/codepage:65001',('/out:'+(Join-Path $UiDir 'Mason.NativeTools.dll')),'/r:System.dll','/r:System.Core.dll','/r:System.Xml.dll',(Join-Path $UiDir 'MasonNativeUi.cs'),(Join-Path $UiDir 'MasonNativeHost.cs'),(Join-Path $UiDir 'MasonNativeText.cs'),(Join-Path $UiDir 'MasonNativeToolsInfo.cs'))
& (Join-Path $masonFramework 'csc.exe') @masonArguments
if($LASTEXITCODE -ne 0){throw '原生界面运行库编译失败。'}
