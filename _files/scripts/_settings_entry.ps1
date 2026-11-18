param(
    [Parameter(Mandatory=$true)][string]$WeaselDir,
    [Parameter(Mandatory=$true)][string]$AppDir,
    [switch]$InspectOnly
)
$ErrorActionPreference='Stop'
$taskEngine=[IO.Path]::GetFullPath($WeaselDir).TrimEnd('\')
$taskApp=[IO.Path]::GetFullPath($AppDir).TrimEnd('\')
$taskServer=Join-Path $taskEngine 'WeaselServer.exe'
$taskNative=Join-Path $taskEngine 'WeaselDeployer.exe'
$taskEntry=Join-Path $taskEngine 'Mason.Deployer.exe'
$taskResources=Join-Path $taskApp '程序\资源'
if(-not(Test-Path -LiteralPath $taskResources -PathType Container)){$taskResources=Join-Path $taskApp '_files'}
$taskSource=Join-Path $taskResources 'ui\Mason.Deployer.exe'
$taskSettings=Join-Path $taskResources 'ui\Mason.Settings.exe'
foreach($taskFile in @($taskServer,$taskNative,$taskSource,$taskSettings)) {if(-not(Test-Path -LiteralPath $taskFile -PathType Leaf)){throw ('设置入口缺少文件：'+$taskFile)}}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($taskSettings).ProductName -ne 'Mason'){throw '设置程序的产品标识不匹配。'}

# Equal-length UTF-16 file names. Only the verified .rdata command target changes;
# the original native deployer, TSF protocol and executable code remain in place.
$taskBytes=[IO.File]::ReadAllBytes($taskServer)
$taskPe=[BitConverter]::ToInt32($taskBytes,0x3c)
if($taskPe -lt 64 -or [BitConverter]::ToUInt32($taskBytes,$taskPe) -ne 0x4550){throw '引擎程序的 PE 格式无效。'}
$taskSections=[BitConverter]::ToUInt16($taskBytes,$taskPe+6)
$taskOptional=[BitConverter]::ToUInt16($taskBytes,$taskPe+20)
$taskSectionOffset=$taskPe+24+$taskOptional
$taskStart=-1;$taskSize=0
for($taskIndex=0;$taskIndex -lt $taskSections;$taskIndex++) {
    $taskHeader=$taskSectionOffset+40*$taskIndex
    $taskName=[Text.Encoding]::ASCII.GetString($taskBytes,$taskHeader,8).Trim([char]0)
    if($taskName -eq '.rdata'){$taskSize=[BitConverter]::ToInt32($taskBytes,$taskHeader+16);$taskStart=[BitConverter]::ToInt32($taskBytes,$taskHeader+20);break}
}
if($taskStart -lt 0 -or $taskStart+$taskSize -gt $taskBytes.Length){throw '没有找到可验证的引擎命令数据区。'}
$taskOld=[Text.Encoding]::Unicode.GetBytes("WeaselDeployer.exe`0")
$taskNew=[Text.Encoding]::Unicode.GetBytes("Mason.Deployer.exe`0")
if($taskOld.Length -ne $taskNew.Length){throw '设置入口命令长度不匹配。'}
function Find-Token([byte[]]$Token) {
    $taskMatches=[Collections.Generic.List[int]]::new()
    for($taskOffset=$taskStart;$taskOffset -le $taskStart+$taskSize-$Token.Length;$taskOffset+=2){
        if($taskBytes[$taskOffset] -ne $Token[0]){continue}
        $taskEqual=$true
        for($taskByte=1;$taskByte -lt $Token.Length;$taskByte++){if($taskBytes[$taskOffset+$taskByte] -ne $Token[$taskByte]){$taskEqual=$false;break}}
        if($taskEqual){$taskMatches.Add($taskOffset)}
    }
    return $taskMatches.ToArray()
}
$taskOriginal=@(Find-Token $taskOld)
$taskPatched=@(Find-Token $taskNew)
if(-not(($taskOriginal.Count -eq 1 -and $taskPatched.Count -eq 0) -or ($taskOriginal.Count -eq 0 -and $taskPatched.Count -eq 1))){throw '引擎命令与支持的格式不一致，原程序未修改。'}
if($InspectOnly){[PSCustomObject]@{OriginalCount=$taskOriginal.Count;EntryCount=$taskPatched.Count;CodeUnchanged=$true;NativeDeployerPreserved=$true;AppDir=$taskApp}|ConvertTo-Json;return}
$taskRunning=@(Get-Process -Name WeaselServer -ErrorAction SilentlyContinue | Where-Object {try{$_.Path -ieq $taskServer}catch{$false}})
# An existing Mason entry is safe to refresh while the input service is running.
if($taskOriginal.Count -eq 1 -and $taskRunning.Count -gt 0){throw '请先停止输入法服务再连接设置入口。'}
$taskStamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
function Write-Atomic([string]$Path,[byte[]]$Data) {
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)) -ine $taskEngine){throw '设置入口目标超出引擎目录。'}
    if(Test-Path -LiteralPath $Path){Copy-Item -LiteralPath $Path -Destination ($Path+'.mason-entry-backup-'+$taskStamp) -ErrorAction Stop}
    $taskTemp=$Path+'.mason-entry-new-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllBytes($taskTemp,$Data)
    if(Test-Path -LiteralPath $Path){[IO.File]::Replace($taskTemp,$Path,($Path+'.mason-entry-replaced-'+[Guid]::NewGuid().ToString('N')))}else{[IO.File]::Move($taskTemp,$Path)}
}
$taskConfig=ConvertTo-Json -InputObject @{product='Mason';app_dir=$taskApp;version='2.4.4'} -Compress
$taskMachine=[BitConverter]::ToUInt16($taskBytes,$taskPe+4)
$taskArchitecture=if($taskMachine -eq 0x8664){'x64'}elseif($taskMachine -eq 0x14c){'x86'}else{throw '当前引擎架构未提供菜单更新入口。'}
$taskMenuSource=Join-Path $taskResources ('native\'+$taskArchitecture+'\Mason.Menu.dll')
if(-not(Test-Path -LiteralPath $taskMenuSource -PathType Leaf) -or [Diagnostics.FileVersionInfo]::GetVersionInfo($taskMenuSource).ProductName -ne 'Mason'){throw '未找到当前架构的麻绳菜单更新入口。'}
Write-Atomic (Join-Path $taskEngine 'Mason.Menu.dll') ([IO.File]::ReadAllBytes($taskMenuSource))
Write-Atomic (Join-Path $taskEngine 'mason-settings.json') ([Text.UTF8Encoding]::new($false).GetBytes($taskConfig))
Write-Atomic $taskEntry ([IO.File]::ReadAllBytes($taskSource))
if($taskOriginal.Count -eq 1){[Array]::Copy($taskNew,0,$taskBytes,$taskOriginal[0],$taskNew.Length);Write-Atomic $taskServer $taskBytes}
Write-Output '设置与菜单动作已连接到麻绳输入法；原生部署入口已保留。'
