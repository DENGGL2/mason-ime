param(
    [Parameter(Mandatory=$true)][string]$WeaselDir,
    [Parameter(Mandatory=$true)][string]$RimeUserDir,
    [switch]$InspectOnly
)
$ErrorActionPreference='Stop'
$taskEngine=(Get-Item -LiteralPath $WeaselDir).FullName.TrimEnd('\')
$taskUi=Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'
$taskResources=Split-Path $PSScriptRoot -Parent
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
# 资源补丁会重排节区，先前写入的客户端路由 RVA 会失效；补丁前先把导入名还原成原始库名。
function Read-PeText([byte[]]$Data,[int]$At){
    if($At -lt 0 -or $At -ge $Data.Length){return $null}
    $taskEnd=$At
    while($taskEnd -lt $Data.Length -and ($taskEnd-$At) -lt 256 -and $Data[$taskEnd] -ne 0){$taskEnd++}
    if($taskEnd -ge $Data.Length -or ($taskEnd-$At) -ge 256){return $null}
    return [Text.Encoding]::ASCII.GetString($Data,$At,$taskEnd-$At)
}
function Find-PeText([byte[]]$Data,[string]$Text){
    $taskNeedle=[Text.Encoding]::ASCII.GetBytes($Text)
    for($taskAt=0;$taskAt -le $Data.Length-$taskNeedle.Length;$taskAt++){
        if($Data[$taskAt] -ne $taskNeedle[0]){continue}
        $taskSame=$true
        for($taskByte=1;$taskByte -lt $taskNeedle.Length;$taskByte++){if($Data[$taskAt+$taskByte] -ne $taskNeedle[$taskByte]){$taskSame=$false;break}}
        if($taskSame){return $taskAt}
    }
    return -1
}
function Get-PeLayout([byte[]]$Data){
    if($Data.Length -lt 256 -or [BitConverter]::ToUInt16($Data,0) -ne 0x5a4d){return $null}
    $taskPe=[BitConverter]::ToInt32($Data,0x3c)
    if($taskPe -lt 64 -or $taskPe -gt $Data.Length-256 -or [BitConverter]::ToUInt32($Data,$taskPe) -ne 0x4550){return $null}
    $taskCount=[BitConverter]::ToUInt16($Data,$taskPe+6)
    $taskOpt=$taskPe+24
    $taskMagic=[BitConverter]::ToUInt16($Data,$taskOpt)
    if($taskMagic -eq 0x20b){$taskPointer=8}elseif($taskMagic -eq 0x10b){$taskPointer=4}else{return $null}
    if($taskCount -lt 1 -or $taskCount -gt 64){return $null}
    $taskBase=$taskOpt+[BitConverter]::ToUInt16($Data,$taskPe+20)
    if($taskBase+40*$taskCount -gt $Data.Length){return $null}
    $taskSections=New-Object System.Collections.Generic.List[object]
    for($taskIndex=0;$taskIndex -lt $taskCount;$taskIndex++){
        $taskAt=$taskBase+40*$taskIndex
        $taskSections.Add([pscustomobject]@{
            Name=[Text.Encoding]::ASCII.GetString($Data,$taskAt,8).TrimEnd([char]0)
            VAddr=[BitConverter]::ToUInt32($Data,$taskAt+12)
            RAddr=[BitConverter]::ToUInt32($Data,$taskAt+20)
            RSize=[BitConverter]::ToUInt32($Data,$taskAt+16)
        })
    }
    return [pscustomobject]@{Pointer=$taskPointer;Sections=$taskSections;ImportDirectory=$taskOpt+$(if($taskPointer -eq 8){112}else{96})+8}
}
function Get-PeOffset($Layout,[uint32]$Rva){
    foreach($taskSection in $Layout.Sections){
        if([int64]$Rva -ge [int64]$taskSection.VAddr -and [int64]$Rva -lt ([int64]$taskSection.VAddr+[int64]$taskSection.RSize)){
            return [int]([int64]$taskSection.RAddr+([int64]$Rva-[int64]$taskSection.VAddr))
        }
    }
    return -1
}
function Get-PeRva($Layout,[int]$Offset){
    foreach($taskSection in $Layout.Sections){
        if($Offset -ge $taskSection.RAddr -and $Offset -lt ([int64]$taskSection.RAddr+[int64]$taskSection.RSize)){
            return [uint32]([int64]$taskSection.VAddr+([int64]$Offset-[int64]$taskSection.RAddr))
        }
    }
    return [uint32]0
}
function Restore-MasonClientImport([byte[]]$Data){
    $taskLayout=Get-PeLayout $Data
    if(-not $taskLayout){return $false}
    $taskImport=[BitConverter]::ToUInt32($Data,$taskLayout.ImportDirectory)
    if($taskImport -eq 0){return $false}
    $taskDescriptor=Get-PeOffset $taskLayout $taskImport
    if($taskDescriptor -lt 0){return $false}
    $taskOrdinal=[uint64]1 -shl $(if($taskLayout.Pointer -eq 8){63}else{31})
    $taskChanged=$false
    for($taskIndex=0;$taskIndex -lt 128;$taskIndex++){
        $taskAt=$taskDescriptor+20*$taskIndex
        if($taskAt+20 -gt $Data.Length){break}
        $taskNameRva=[BitConverter]::ToUInt32($Data,$taskAt+12)
        $taskThunkRva=[BitConverter]::ToUInt32($Data,$taskAt)
        if($taskThunkRva -eq 0){$taskThunkRva=[BitConverter]::ToUInt32($Data,$taskAt+16)}
        if($taskNameRva -eq 0 -and $taskThunkRva -eq 0){break}
        if($taskThunkRva -eq 0){continue}
        $taskThunkAt=Get-PeOffset $taskLayout $taskThunkRva
        if($taskThunkAt -lt 0){continue}
        $taskValue=if($taskLayout.Pointer -eq 8){[BitConverter]::ToUInt64($Data,$taskThunkAt)}else{[uint64][BitConverter]::ToUInt32($Data,$taskThunkAt)}
        if($taskValue -eq 0){continue}
        $taskCurrent=$null
        if($taskNameRva -ne 0){$taskNameAt=Get-PeOffset $taskLayout $taskNameRva;if($taskNameAt -ge 0){$taskCurrent=Read-PeText $Data $taskNameAt}}
        $taskOurs=($null -ne $taskCurrent) -and ($taskCurrent -match '^(?i)mason(?:64|32)?\.dll$')
        if(-not $taskOurs -and ($null -ne $taskCurrent) -and ($taskCurrent -match '^(?i)[A-Za-z0-9_\-\.]+\.dll$')){continue}
        $taskTarget=$null
        if(($taskValue -band $taskOrdinal) -ne 0){
            $taskNumber=[int]($taskValue -band 0xffff)
            if($taskNumber -eq 1 -and -not ($taskCurrent -match '^(?i)DWrite\.dll$')){$taskTarget='d2d1.dll'}
            elseif($taskNumber -eq 2 -and $taskOurs){$taskTarget='DWrite.dll'}
        }else{
            $taskFunctionAt=Get-PeOffset $taskLayout ([uint32]($taskValue -band 0x7fffffff))
            if($taskFunctionAt -ge 0){
                $taskFunction=Read-PeText $Data ($taskFunctionAt+2)
                if($taskFunction -eq 'DWriteCreateFactory'){$taskTarget='DWrite.dll'}elseif($taskFunction -eq 'D2D1CreateFactory'){$taskTarget='d2d1.dll'}
            }
        }
        if(-not $taskTarget){continue}
        if($taskCurrent -eq $taskTarget){continue}
        $taskNameOffset=Find-PeText $Data ($taskTarget+"`0")
        if($taskNameOffset -lt 0){continue}
        $taskNewRva=Get-PeRva $taskLayout $taskNameOffset
        if($taskNewRva -eq 0){continue}
        [Array]::Copy([BitConverter]::GetBytes($taskNewRva),0,$Data,$taskAt+12,4)
        $taskChanged=$true
    }
    return $taskChanged
}
foreach($taskName in @('weasel.dll','weaselx64.dll','weasel.ime','weaselx64.ime')){
    $taskPath=Join-Path $taskEngine $taskName
    if(Test-Path -LiteralPath $taskPath -PathType Leaf){$taskTargets[$taskPath]=if($taskName.EndsWith('.ime')){101}else{100}}
}
# Resolve only the installed Rime COM modules, including the renamed x64 DLL in System32.
foreach($taskView in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
    $taskBase=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$taskView)
    try{
        $taskInstalled=$taskBase.OpenSubKey('SOFTWARE\Rime\Weasel')
        $taskRegisteredHere=$false
        if($taskInstalled){
            try{foreach($taskRootName in @('WeaselRoot','InstallDir')){
                $taskRegisteredRoot=[string]$taskInstalled.GetValue($taskRootName)
                if($taskRegisteredRoot -and [IO.Path]::GetFullPath($taskRegisteredRoot).TrimEnd('\').Equals($taskEngine,[StringComparison]::OrdinalIgnoreCase)){$taskRegisteredHere=$true}
            }}finally{$taskInstalled.Dispose()}
        }
        if(-not $taskRegisteredHere){continue}
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
$taskBackupRoot=Join-Path (Join-Path (Split-Path $taskEngine -Parent) '备份') ('原生界面-'+$taskStamp)
$taskTargetIndex=0
foreach($taskTarget in $taskTargets.GetEnumerator()){
    $taskTargetIndex++
    $taskFile=$taskTarget.Key;$taskGroup=$taskTarget.Value;$taskMenus=[Mason.NativeUi.Resources]::Read($taskFile)
    $taskCompact=$taskMenus.Length -eq 0 -or ([Mason.NativeUi.Resources]::IsCompact($taskFile) -and [Mason.NativeUi.Resources]::HasMenuRoutes($taskFile))
    $taskMIcon=[Mason.NativeUi.Resources]::HasMIcon($taskFile,$taskIcon,$taskGroup)
    $taskSupportsText=[Mason.NativeUi.TextAdapter]::SupportsClient($taskFile)
    $taskTextReady=-not $taskSupportsText -or [Mason.NativeUi.TextAdapter]::IsClientCurrent($taskFile,$taskResources)
    if($InspectOnly){$taskRecords.Add([PSCustomObject]@{Path=$taskFile;Compact=$taskCompact;MIcon=$taskMIcon;NativeText=$taskTextReady;Menus=@($taskMenus | ForEach-Object {[PSCustomObject]@{Resource=$_.Name;Commands=$_.Commands;Ids=$_.CommandIds}})});continue}
    if($taskCompact -and $taskMIcon -and $taskTextReady){$taskRecords.Add([PSCustomObject]@{Path=$taskFile;Changed=$false;NativeText=$true;PendingReboot=$false});continue}
    $taskBackup=Join-Path $taskBackupRoot ('模块-'+$taskTargetIndex)
    $taskAdapterChanged=[Mason.NativeUi.TextAdapter]::InstallClientAdapter($taskFile,$taskResources,$taskBackup)
    if($taskCompact -and $taskMIcon -and [Mason.NativeUi.TextAdapter]::HasClientRoutes($taskFile)){
        if(-not [Mason.NativeUi.TextAdapter]::IsClientCurrent($taskFile,$taskResources)){throw ('客户端标红组件校验失败：'+$taskFile)}
        $taskRecords.Add([PSCustomObject]@{Path=$taskFile;Changed=$taskAdapterChanged;NativeText=$true;PendingReboot=$false});continue
    }
    $taskStage=$taskFile+'.mason-ui-new-'+$taskStamp
    Copy-Item -LiteralPath $taskFile -Destination $taskStage
    try{
        $taskStageData=[IO.File]::ReadAllBytes($taskStage)
        if(Restore-MasonClientImport $taskStageData){[IO.File]::WriteAllBytes($taskStage,$taskStageData)}
        [Mason.NativeUi.Resources]::Patch($taskStage,$taskIcon,$taskGroup)
        if($taskSupportsText){[IO.File]::WriteAllBytes($taskStage,[Mason.NativeUi.TextAdapter]::PatchClient([IO.File]::ReadAllBytes($taskStage)))}
    }catch{
        Remove-Item -LiteralPath $taskStage -Force -ErrorAction SilentlyContinue
        throw
    }
    [IO.Directory]::CreateDirectory($taskBackup)|Out-Null
    $taskSaved=Join-Path $taskBackup ([IO.Path]::GetFileName($taskFile))
    Copy-Item -LiteralPath $taskFile -Destination $taskSaved
    if((Get-FileHash -LiteralPath $taskSaved -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash){throw ('原生组件备份校验失败：'+$taskFile)}
    try{[IO.File]::Replace($taskStage,$taskFile,[NullString]::Value)}
    catch{
        # 目标正被运行中的程序加载时无法直接替换：先改名腾位，再由新文件顶上。
        $taskRetired=$taskFile+'.mason-old-'+$taskStamp
        [IO.File]::Move($taskFile,$taskRetired)
        try{[IO.File]::Move($taskStage,$taskFile)}catch{[IO.File]::Move($taskRetired,$taskFile);throw}
    }
    if(-not [Mason.NativeUi.Resources]::HasMIcon($taskFile,$taskIcon,$taskGroup) -or ($taskMenus.Length -gt 0 -and -not [Mason.NativeUi.Resources]::IsCompact($taskFile))){throw ('任务栏界面验收失败：'+$taskFile)}
    if($taskSupportsText -and -not [Mason.NativeUi.TextAdapter]::IsClientCurrent($taskFile,$taskResources)){throw ('客户端标红组件校验失败：'+$taskFile)}
    $taskRecords.Add([PSCustomObject]@{Path=$taskFile;Changed=$true;NativeText=$true;Backup=$taskSaved;PendingReboot=$false})
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
