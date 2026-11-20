param([Parameter(Mandatory=$true)][string]$PackageDir,[Parameter(Mandatory=$true)][string]$BuildDir)
$ErrorActionPreference='Stop'
$taskNative=Join-Path $PackageDir '_files\native'
$taskVcRoot=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC'
$taskVc=(Get-ChildItem -LiteralPath $taskVcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$taskVersion=(Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
if(-not $taskVc -or -not $taskVersion){throw '原生标红绘制需要现有的 Visual C++ Build Tools 和 Windows SDK。'}
[IO.Directory]::CreateDirectory($BuildDir)|Out-Null
$env:INCLUDE=$taskVc+'\include;'+$taskSdk+'\Include\'+$taskVersion+'\ucrt;'+$taskSdk+'\Include\'+$taskVersion+'\um;'+$taskSdk+'\Include\'+$taskVersion+'\shared'
foreach($taskArch in @('x64','x86')){
  $taskOut=Join-Path $taskNative $taskArch
  [IO.Directory]::CreateDirectory($taskOut)|Out-Null
  $env:LIB=$taskVc+'\lib\'+$taskArch+';'+$taskSdk+'\Lib\'+$taskVersion+'\ucrt\'+$taskArch+';'+$taskSdk+'\Lib\'+$taskVersion+'\um\'+$taskArch
  $taskResource=Join-Path $BuildDir ('MasonTextAdapter-'+$taskArch+'.res')
  & (Join-Path $taskSdk ('bin\'+$taskVersion+'\x64\rc.exe')) /nologo /c65001 ('/fo'+$taskResource) (Join-Path $taskNative 'MasonTextAdapter.rc')
  if($LASTEXITCODE -ne 0){throw '标红绘制版本资源编译失败。'}
  $taskDef=Join-Path $taskNative $(if($taskArch -eq 'x86'){'MasonTextAdapter.x86.def'}else{'MasonTextAdapter.def'})
  & (Join-Path $taskVc ('bin\Hostx64\'+$taskArch+'\cl.exe')) /nologo /LD /EHsc /MT /utf-8 /std:c++17 /O2 /DUNICODE /D_UNICODE ('/Fe:'+(Join-Path $taskOut 'Mason.dll')) ('/Fo:'+(Join-Path $BuildDir ('MasonTextAdapter-'+$taskArch+'.obj'))) (Join-Path $taskNative 'MasonTextAdapter.cpp') $taskResource /link ('/DEF:'+$taskDef) ('/IMPLIB:'+(Join-Path $BuildDir ('MasonTextAdapter-'+$taskArch+'.lib'))) ('/MAP:'+(Join-Path $BuildDir ('MasonTextAdapter-'+$taskArch+'.map'))) user32.lib gdi32.lib comctl32.lib
  if($LASTEXITCODE -ne 0){throw ('原生标红绘制编译失败：'+$taskArch)}
}
