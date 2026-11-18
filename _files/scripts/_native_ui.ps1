param(
    [Parameter(Mandatory=$true)][string]$WeaselDir,
    [Parameter(Mandatory=$true)][string]$RimeUserDir,
    [switch]$InspectOnly
)
$ErrorActionPreference='Stop'
$taskEngine=(Get-Item -LiteralPath $WeaselDir).FullName.TrimEnd('\')
$taskUi=Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'
if(-not ('Mason.NativeUi.Resources' -as [type])){Add-Type -Path (Join-Path $taskUi 'Mason.NativeTools.dll')}
if(-not ('Mason.Diagnostics.RemoteMenuProbe' -as [type])){Add-Type -Path (Join-Path $taskUi 'Mason.NativeTools.dll')}
if(-not $InspectOnly){& (Join-Path $PSScriptRoot '_menu_protocol.ps1') -AppDir (Split-Path $taskEngine -Parent)}
$taskIcon=Join-Path $taskUi 'Mason.ico'
$taskTargets=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::OrdinalIgnoreCase)
function Get-Machine([string]$File){$taskData=[IO.File]::ReadAllBytes($File);$taskPe=[BitConverter]::ToInt32($taskData,0x3c);return [BitConverter]::ToUInt16($taskData,$taskPe+4)}
function Is-OwnedModule([string]$File){
    if(-not (Test-Path -LiteralPath $File -PathType Leaf)){return $false}
    $taskName=[IO.Path]::GetFileName($File)
    if($taskName -notmatch '^weasel(?:x64|ARM|ARM64|ARM64X)?\.(dll|ime)$'){return $false}
    $taskSuffix=[IO.Path]::GetExtension($File)
    $taskMachine=Get-Machine $File
    $taskPrefix=if($taskMachine -eq 0x8664){'weaselx64'}elseif($taskMachine -eq 0x14c){'weasel'}else{return $false}
    $taskSource=Join-Path $taskEngine ($taskPrefix+$taskSuffix)
    if(-not (Test-Path -LiteralPath $taskSource -PathType Leaf)){return $false}
    return [Mason.NativeUi.Resources]::IsOwnedModule($taskEngine,$File)
}
foreach($taskName in @('weasel.dll','weaselx64.dll','weasel.ime','weaselx64.ime')){
    $taskPath=Join-Path $taskEngine $taskName
    if(Test-Path -LiteralPath $taskPath -PathType Leaf){$taskTargets[$taskPath]=if($taskName.EndsWith('.ime')){101}else{100}}
}
# Resolve only the installed Rime COM modules, including the renamed x64 DLL in System32.
foreach($taskView in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
    $taskBase=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$taskView)
    try{
        $taskTips=$taskBase.OpenSubKey('SOFTWARE\Microsoft\CTF\TIP')
        if(-not $taskTips){continue}
        try{foreach($taskId in $taskTips.GetSubKeyNames()){
            $taskClass=$taskBase.OpenSubKey("SOFTWARE\Classes\CLSID\$taskId\InprocServer32")
            if(-not $taskClass){continue}
            try{$taskDll=[string]$taskClass.GetValue('')}finally{$taskClass.Dispose()}
            if(-not $taskDll){continue}
            $taskDll=[Environment]::ExpandEnvironmentVariables($taskDll.Trim('"'))
            $taskParent=[IO.Path]::GetDirectoryName($taskDll)
            if($taskParent -notin @((Join-Path $env:WINDIR 'System32'),(Join-Path $env:WINDIR 'SysWOW64'),$taskEngine)){continue}
            if(Is-OwnedModule $taskDll){$taskTargets[$taskDll]=100}
        }}finally{$taskTips.Dispose()}
    }finally{$taskBase.Dispose()}
}
$taskRecords=[Collections.Generic.List[object]]::new()
$taskStamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
foreach($taskTarget in $taskTargets.GetEnumerator()){
    $taskFile=$taskTarget.Key;$taskGroup=$taskTarget.Value;$taskMenus=[Mason.NativeUi.Resources]::Read($taskFile)
    $taskCompact=$taskMenus.Length -eq 0 -or ([Mason.NativeUi.Resources]::IsCompact($taskFile) -and [Mason.NativeUi.Resources]::HasMenuRoutes($taskFile))
    $taskMIcon=[Mason.NativeUi.Resources]::HasMIcon($taskFile,$taskIcon,$taskGroup)
    if($InspectOnly){$taskRecords.Add([PSCustomObject]@{Path=$taskFile;Compact=$taskCompact;MIcon=$taskMIcon;Menus=@($taskMenus | ForEach-Object {[PSCustomObject]@{Resource=$_.Name;Commands=$_.Commands;Ids=$_.CommandIds}})});continue}
    if($taskCompact -and $taskMIcon){$taskRecords.Add([PSCustomObject]@{Path=$taskFile;Changed=$false;PendingReboot=$false});continue}
    $taskStage=$taskFile+'.mason-ui-new-'+$taskStamp
    Copy-Item -LiteralPath $taskFile -Destination $taskStage
    [Mason.NativeUi.Resources]::Patch($taskStage,$taskIcon,$taskGroup)
    Copy-Item -LiteralPath $taskFile -Destination ($taskFile+'.mason-ui-backup-'+$taskStamp)
    [IO.File]::Replace($taskStage,$taskFile,($taskFile+'.mason-ui-replaced-'+$taskStamp))
    if(-not [Mason.NativeUi.Resources]::HasMIcon($taskFile,$taskIcon,$taskGroup) -or ($taskMenus.Length -gt 0 -and -not [Mason.NativeUi.Resources]::IsCompact($taskFile))){throw ('任务栏界面验收失败：'+$taskFile)}
    $taskRecords.Add([PSCustomObject]@{Path=$taskFile;Changed=$true;PendingReboot=$false})
}
$taskHosts=[Collections.Generic.List[object]]::new()
foreach($taskHostProcess in @(Get-Process)){
    try{
        foreach($taskModule in @($taskHostProcess.Modules | Where-Object {$_.ModuleName -match '^weasel(?:x64)?\.dll$'})){
            if(-not [Mason.NativeUi.Resources]::IsOwnedModule($taskEngine,$taskModule.FileName)){continue}
            try{
                $taskLive=[Mason.Diagnostics.RemoteMenuProbe]::Inspect($taskHostProcess.Id,$taskModule.BaseAddress.ToInt64())
                $taskStale=$taskLive.Menus.Count -eq 0
                foreach($taskLiveMenu in $taskLive.Menus){if($taskLiveMenu.Error -or (($taskLiveMenu.Ids -join ',') -ne ([Mason.NativeUi.Resources]::MenuCommandIds -join ','))){$taskStale=$true}}
                $taskHosts.Add([pscustomobject]@{Process=$taskHostProcess.ProcessName;PID=$taskHostProcess.Id;Module=$taskModule.FileName;PendingHostRefresh=$taskStale;Menus=$taskLive.Menus;Error=$null})
            }catch{$taskHosts.Add([pscustomobject]@{Process=$taskHostProcess.ProcessName;PID=$taskHostProcess.Id;Module=$taskModule.FileName;PendingHostRefresh=$null;Error=$_.Exception.Message})}
        }
    }catch{if($taskHostProcess.ProcessName -eq 'explorer'){$taskHosts.Add([pscustomobject]@{Process='explorer';PID=$taskHostProcess.Id;PendingHostRefresh=$null;Error=$_.Exception.Message})}}
}
$taskPending=@($taskHosts.ToArray() | Where-Object {$_.PendingHostRefresh -eq $true}).Count -gt 0
if($InspectOnly){ConvertTo-Json -InputObject @{files=$taskRecords.ToArray();hosts=$taskHosts.ToArray();pending_host_refresh=$taskPending} -Depth 9;return}
$taskRecordPath=Join-Path $RimeUserDir '.mason-native-ui.json'
[IO.File]::WriteAllText($taskRecordPath,(ConvertTo-Json -InputObject @{updated_at=(Get-Date -Format 'yyyy-MM-dd HH:mm:ss');files=$taskRecords.ToArray();menus=@('切换中 / 英','切换简 / 繁','切换半 / 全角','翻译开 / 关','设置','更新词库','检查更新');icon='M';hosts=$taskHosts.ToArray();pending_host_refresh=$taskPending;host_verification_complete=@($taskHosts.ToArray() | Where-Object {$_.Error}).Count -eq 0} -Depth 9),[Text.UTF8Encoding]::new($false))
if($taskPending){Write-Output '菜单文件已更新；部分已打开的程序仍加载旧菜单，重新打开这些程序后生效。'}elseif(@($taskHosts.ToArray() | Where-Object {$_.Error}).Count -gt 0){Write-Output '菜单文件已更新；部分已打开的程序暂未完成验证。'}else{Write-Output '系统任务栏与托盘的七项菜单已应用，M 图标已验证。'}
