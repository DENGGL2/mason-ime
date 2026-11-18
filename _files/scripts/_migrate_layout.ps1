param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [Parameter(Mandatory=$true)][string]$SourceDir,
    [Parameter(Mandatory=$true)][string]$PreviousEngineDir,
    [Parameter(Mandatory=$true)][string]$PreviousUserDir,
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [string]$ProgressPath,
    [string]$PreviousLogDir,
    [string]$PreviousInstallLogDir,
    [switch]$InspectOnly,
    [switch]$FileCopyOnly
)
$ErrorActionPreference='Stop'
$taskApp=[IO.Path]::GetFullPath($AppDir).TrimEnd('\')
$taskEngine=[IO.Path]::GetFullPath($PreviousEngineDir).TrimEnd('\')
$taskUser=[IO.Path]::GetFullPath($PreviousUserDir).TrimEnd('\')
$taskSource=[IO.Path]::GetFullPath($SourceDir).TrimEnd('\')
$taskProgram=Join-Path $taskApp '程序'
$taskResources=Join-Path $taskProgram '资源'
$taskUserTarget=Join-Path $taskApp '用户数据'
$taskLogs=Join-Path $taskApp '日志'
$taskBackups=Join-Path $taskApp '备份'
$taskStamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
$taskSnapshot=Join-Path $taskBackups ('目录迁移-'+$taskStamp)
$taskMoves=[Collections.Generic.List[object]]::new()
$taskSourceRecovery=[Collections.Generic.List[object]]::new()
$taskRegistry=[Collections.Generic.List[object]]::new()
$taskApplied=[Collections.Generic.List[object]]::new()
$taskProgressWatch=[Diagnostics.Stopwatch]::StartNew()
$taskStageMessage='正在检查目录';$taskStageLow=0;$taskStageHigh=5

function Report-Layout([double]$Percent,[long]$Bytes=0,[long]$Total=0,[switch]$Force){
    if(-not $ProgressPath -or (-not $Force -and $taskProgressWatch.ElapsedMilliseconds -lt 200)){return}
    $taskProgressWatch.Restart()
    $taskStatus=@{Type='progress';Phase='layout';Title='正在整理安装目录';Message=$script:taskStageMessage;Percent=$Percent;Bytes=$Bytes;TotalBytes=$Total}
    $taskFullStatus=[IO.Path]::GetFullPath($ProgressPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskFullStatus))|Out-Null
    $taskNewStatus=$taskFullStatus+'.new-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($taskNewStatus,(ConvertTo-Json -InputObject $taskStatus -Compress),[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $taskFullStatus){[IO.File]::Replace($taskNewStatus,$taskFullStatus,[NullString]::Value)}else{[IO.File]::Move($taskNewStatus,$taskFullStatus)}
}
function Set-LayoutStage([string]$Message,[double]$Low,[double]$High){$script:taskStageMessage=$Message;$script:taskStageLow=$Low;$script:taskStageHigh=$High;Report-Layout $Low -Force}

function Resolve-SafeTarget([string]$Path){
    $taskFull=[IO.Path]::GetFullPath($Path)
    if(-not $taskFull.StartsWith($taskApp+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('迁移目标超出安装目录：'+$Path)}
    $taskParent=$taskFull
    while($taskParent -and $taskParent.StartsWith($taskApp,[StringComparison]::OrdinalIgnoreCase)){
        if(Test-Path -LiteralPath $taskParent){if((Get-Item -LiteralPath $taskParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('迁移目标包含链接：'+$taskParent)}}
        $taskParent=[IO.Path]::GetDirectoryName($taskParent)
    }
    return $taskFull
}
function Get-CheckedHash([string]$Path){
    $taskInput=[IO.File]::OpenRead($Path);$taskHash=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($taskHash.ComputeHash($taskInput)).Replace('-','')}finally{$taskHash.Dispose();$taskInput.Dispose()}
}
function Copy-Checked([string]$From,[string]$To){
    $taskDest=Resolve-SafeTarget $To
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskDest))|Out-Null
    Copy-Item -LiteralPath $From -Destination $taskDest -Force
    if((Get-CheckedHash $From) -ne (Get-CheckedHash $taskDest)){throw ('迁移校验失败：'+$From)}
}
function Copy-Tree([string]$From,[string]$To,[switch]$SkipHistorical){
    if(-not(Test-Path -LiteralPath $From -PathType Container)){[IO.Directory]::CreateDirectory((Resolve-SafeTarget $To))|Out-Null;return}
    $taskRoot=[IO.Path]::GetFullPath($From).TrimEnd('\')
    if((Get-Item -LiteralPath $taskRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('源目录包含链接：'+$taskRoot)}
    $taskItems=@(Get-ChildItem -LiteralPath $taskRoot -Force -Recurse)
    [long]$taskTreeTotal=0;[long]$taskTreeDone=0
    foreach($taskSizeItem in $taskItems){if(-not $taskSizeItem.PSIsContainer){$taskTreeTotal+=$taskSizeItem.Length}}
    foreach($taskItem in $taskItems){
        if($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('源文件包含链接：'+$taskItem.FullName)}
        $taskRelative=$taskItem.FullName.Substring($taskRoot.Length+1)
        $taskDest=Resolve-SafeTarget (Join-Path $To $taskRelative)
        if($taskItem.PSIsContainer){[IO.Directory]::CreateDirectory($taskDest)|Out-Null;continue}
        if($SkipHistorical -and $taskItem.Name -match '\.(?:mason-|menu-backup-|mini-backup-)'){continue}
        Copy-Checked $taskItem.FullName $taskDest
        $taskTreeDone+=$taskItem.Length
        $taskFraction=if($taskTreeTotal -gt 0){$taskTreeDone/$taskTreeTotal}else{1}
        Report-Layout ($script:taskStageLow+($script:taskStageHigh-$script:taskStageLow)*$taskFraction) $taskTreeDone $taskTreeTotal
    }
}
function Move-LocalDirectory([string]$From,[string]$To){
    $taskFrom=Resolve-SafeTarget $From;$taskTo=Resolve-SafeTarget $To
    if(Test-Path -LiteralPath $taskTo){throw ('迁移目标已存在：'+$taskTo)}
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskTo))|Out-Null
    [IO.Directory]::Move($taskFrom,$taskTo)
    $taskMoves.Add([PSCustomObject]@{From=$taskFrom;To=$taskTo})
}
function Move-OrPreserveDirectory([string]$From,[string]$To){
    try{Move-LocalDirectory $From $To}catch{
        $taskRenameFailure=$_.Exception.Message
        Copy-Tree $From $To
        $taskSourceRecovery.Add([PSCustomObject]@{original=$From;verified_copy=$To;reason=$taskRenameFailure})
        Write-Output ('旧目录已校验复制，原件保留作恢复副本：'+$From)
    }
}
function Grant-UserWrite([string]$Path){
    $taskTarget=Resolve-SafeTarget $Path
    [IO.Directory]::CreateDirectory($taskTarget)|Out-Null
    $taskAcl=[IO.Directory]::GetAccessControl($taskTarget)
    $taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $taskRule=New-Object Security.AccessControl.FileSystemAccessRule($taskSid,[Security.AccessControl.FileSystemRights]::Modify,([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
    $taskAcl.AddAccessRule($taskRule)
    [IO.Directory]::SetAccessControl($taskTarget,$taskAcl)
}
function Set-RecordedValue([Microsoft.Win32.RegistryHive]$Hive,[Microsoft.Win32.RegistryView]$View,[string]$Path,[string]$Name,[object]$Value){
    $taskBase=[Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive,$View)
    try{
        $taskKey=$taskBase.CreateSubKey($Path)
        try{
            $taskExists=$taskKey.GetValueNames() -contains $Name
            $taskOld=if($taskExists){$taskKey.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}else{$null}
            $taskKind=if($taskExists){$taskKey.GetValueKind($Name)}else{[Microsoft.Win32.RegistryValueKind]::String}
            if($taskExists -and [string]$taskOld -eq [string]$Value){return}
            $taskEntry=[PSCustomObject]@{Hive=[string]$Hive;View=[string]$View;Path=$Path;Name=$Name;Existed=$taskExists;Before=$taskOld;Kind=[string]$taskKind;After=$Value}
            $taskRegistry.Add($taskEntry)
            [IO.File]::WriteAllText((Join-Path $taskSnapshot '注册路径备份.json'),(ConvertTo-Json -InputObject $taskRegistry.ToArray() -Depth 8),[Text.UTF8Encoding]::new($false))
            $taskApplied.Add($taskEntry)
            $taskKey.SetValue($Name,$Value,$taskKind)
            if([string]$taskKey.GetValue($Name) -ne [string]$Value){throw ('路径写入未通过验证：'+$Path+' '+$Name)}
        }finally{$taskKey.Dispose()}
    }finally{$taskBase.Dispose()}
}

if($taskApp.Length -lt 8 -or $taskApp -eq [IO.Path]::GetPathRoot($taskApp).TrimEnd('\')){throw '安装目录无效。'}
foreach($taskName in @('WeaselServer.exe','WeaselDeployer.exe','rime.dll')){if(-not(Test-Path -LiteralPath (Join-Path $taskEngine $taskName) -PathType Leaf)){throw ('现有引擎缺少文件：'+$taskName)}}
foreach($taskDir in @($taskProgram,$taskResources,$taskUserTarget,$taskLogs,$taskBackups)){[void](Resolve-SafeTarget $taskDir)}
$taskSourceResources=Join-Path $taskSource '_files'
if(-not(Test-Path -LiteralPath $taskSourceResources -PathType Container)){$taskSourceResources=Join-Path $taskSource '程序\资源'}
$taskShim=Join-Path $taskSourceResources 'ui\Mason.Server.exe'
if(-not(Test-Path -LiteralPath $taskShim -PathType Leaf)){throw '新版服务启动器不存在。'}
foreach($taskPair in @(@($taskEngine,$taskProgram),@($taskUser,$taskUserTarget))){if($taskPair[0] -ine $taskPair[1] -and ($taskPair[0].StartsWith($taskPair[1]+'\',[StringComparison]::OrdinalIgnoreCase) -or $taskPair[1].StartsWith($taskPair[0]+'\',[StringComparison]::OrdinalIgnoreCase))){throw '源目录和迁移目标不能互相包含。'}}
$taskPlan=[PSCustomObject]@{app_dir=$taskApp;engine_dir=$taskProgram;user_dir=$taskUserTarget;log_dir=$taskLogs;backup_dir=$taskBackups;previous_engine_dir=$taskEngine;previous_user_dir=$taskUser;snapshot=$taskSnapshot;source_preserved=$true;source_recovery=@()}
if($InspectOnly){$taskPlan|ConvertTo-Json -Depth 5;return}

try{
    $taskShimBytes=[IO.File]::ReadAllBytes($taskShim)
    Set-LayoutStage '正在检查现有目录' 0 5
    if(-not $FileCopyOnly){
        foreach($taskProcess in @(Get-Process -Name WeaselServer -ErrorAction SilentlyContinue)){
            try{if($taskProcess.Path -ieq (Join-Path $taskEngine 'WeaselServer.exe')){throw '请先停止麻绳服务再迁移目录。'}}catch [ComponentModel.Win32Exception] {}
        }
        foreach($taskProcess in @(Get-Process -Name Mason.Settings -ErrorAction SilentlyContinue)){
            try{if($taskProcess.Path.StartsWith($taskApp+'\',[StringComparison]::OrdinalIgnoreCase)){throw '请先关闭麻绳设置窗口再迁移目录。'}}catch [ComponentModel.Win32Exception] {}
        }
    }
    $taskOldBackups=Join-Path $taskApp 'Mason-备份'
    if(Test-Path -LiteralPath $taskOldBackups -PathType Container){
        if(-not(Test-Path -LiteralPath $taskBackups)){Move-OrPreserveDirectory $taskOldBackups $taskBackups}
        else{Move-OrPreserveDirectory $taskOldBackups (Join-Path $taskBackups ('历史安装-'+$taskStamp))}
    }
    [IO.Directory]::CreateDirectory($taskProgram)|Out-Null
    foreach($taskDir in @($taskUserTarget,$taskLogs,$taskBackups)){if($FileCopyOnly){[IO.Directory]::CreateDirectory($taskDir)|Out-Null}else{Grant-UserWrite $taskDir}}
    [IO.Directory]::CreateDirectory($taskSnapshot)|Out-Null
    if($taskEngine -ine $taskProgram){
        Set-LayoutStage '备份目标目录中已有的程序' 5 8
        Copy-Tree $taskProgram (Join-Path $taskSnapshot '目标原程序')
        Set-LayoutStage '备份迁移前的程序' 8 24
        Copy-Tree $taskEngine (Join-Path $taskSnapshot '迁移前程序')
        Set-LayoutStage '迁移程序并校验文件' 24 40
        Copy-Tree $taskEngine $taskProgram -SkipHistorical
    }
    if($taskUser -ine $taskUserTarget -and (Test-Path -LiteralPath $taskUser -PathType Container)){
        Set-LayoutStage '备份目标目录中已有的用户数据' 40 43
        Copy-Tree $taskUserTarget (Join-Path $taskSnapshot '目标原用户数据')
        Set-LayoutStage '检查个人词库和已保存设置' 43 45
        $taskBeforeFiles=@(Get-ChildItem -LiteralPath $taskUser -File -Force -Recurse | Sort-Object FullName | ForEach-Object {[PSCustomObject]@{Relative=$_.FullName.Substring($taskUser.Length+1);Hash=(Get-CheckedHash $_.FullName)}})
        Set-LayoutStage '备份个人词库和已保存设置' 45 60
        Copy-Tree $taskUser (Join-Path $taskSnapshot '迁移前用户数据')
        Set-LayoutStage '迁移用户数据并校验文件' 60 80
        Copy-Tree (Join-Path $taskSnapshot '迁移前用户数据') $taskUserTarget
        foreach($taskFile in $taskBeforeFiles){if((Get-CheckedHash (Join-Path $taskUser $taskFile.Relative)) -ne $taskFile.Hash){throw '迁移时用户数据仍在变化，原目录已保留，请关闭输入后重试。'}}
        $taskAfterFiles=@(Get-ChildItem -LiteralPath $taskUser -File -Force -Recurse)
        if($taskAfterFiles.Count -ne $taskBeforeFiles.Count){throw '迁移时用户数据文件数量变化，原目录已保留。'}
    }
    Set-LayoutStage '整理程序资源和历史备份' 80 85
    $taskOldResources=Join-Path $taskApp '_files'
    if(Test-Path -LiteralPath $taskOldResources -PathType Container){
        if(-not(Test-Path -LiteralPath $taskResources)){Move-OrPreserveDirectory $taskOldResources $taskResources}
        else{Copy-Tree $taskOldResources (Join-Path $taskSnapshot '旧版资源');Move-OrPreserveDirectory $taskOldResources (Join-Path $taskBackups ('历史程序资源-'+$taskStamp))}
    }
    $taskShimTarget=Resolve-SafeTarget (Join-Path $taskProgram 'Mason.Server.exe')
    [IO.File]::WriteAllBytes($taskShimTarget,$taskShimBytes)
    if((Get-CheckedHash $taskShimTarget) -ne [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($taskShimBytes)).Replace('-','')){throw '服务启动器校验失败。'}
    if(-not $FileCopyOnly){
        Set-LayoutStage '检查中文目录兼容性' 84 85
        $taskCompatibility=Join-Path $taskSourceResources 'ui\Mason.Compatibility.exe'
        if(-not(Test-Path -LiteralPath $taskCompatibility -PathType Leaf)){$taskCompatibility=Join-Path $taskResources 'ui\Mason.Compatibility.exe'}
        if(-not(Test-Path -LiteralPath $taskCompatibility -PathType Leaf)){throw '中文目录兼容性工具不存在。'}
        $taskCompatibilityReport=Join-Path $taskSnapshot '中文目录兼容性.json'
        $taskStart=[Diagnostics.ProcessStartInfo]::new($taskCompatibility,('"'+$taskProgram+'" "'+$taskSnapshot+'" "'+$taskCompatibilityReport+'"'))
        $taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        $taskCompatibilityProcess=[Diagnostics.Process]::Start($taskStart)
        try{if(-not $taskCompatibilityProcess.WaitForExit(60000)){throw '中文目录兼容性检查超时。'};if($taskCompatibilityProcess.ExitCode -ne 0){throw ('中文目录兼容性更新失败：'+[IO.File]::ReadAllText($taskCompatibilityReport,[Text.Encoding]::UTF8))}}finally{$taskCompatibilityProcess.Dispose()}
    }
    $taskRuntime=@{product='Mason';version='2.4.4';app_dir=$taskApp;user_dir=$taskUserTarget;log_dir=$taskLogs}
    [IO.File]::WriteAllText((Join-Path $taskProgram 'mason-runtime.json'),(ConvertTo-Json -InputObject $taskRuntime -Compress),[Text.UTF8Encoding]::new($false))
    Set-LayoutStage '归档运行日志' 85 90
    $taskPriorLogs=$PreviousLogDir
    if(-not $taskPriorLogs -and -not $FileCopyOnly){$taskPriorLogs=Join-Path $env:LOCALAPPDATA 'Temp\rime.weasel'}
    if($taskPriorLogs -and (Test-Path -LiteralPath $taskPriorLogs -PathType Container)){Copy-Tree $taskPriorLogs (Join-Path $taskLogs 'rime.weasel')}
    Set-LayoutStage '归档安装记录' 90 94
    $taskPriorInstalls=$PreviousInstallLogDir
    if(-not $taskPriorInstalls -and -not $FileCopyOnly){$taskPriorInstalls=Join-Path $env:LOCALAPPDATA 'Mason\安装记录'}
    if($taskPriorInstalls -and (Test-Path -LiteralPath $taskPriorInstalls -PathType Container)){Copy-Tree $taskPriorInstalls (Join-Path $taskLogs '安装记录')}
    Set-LayoutStage '切换麻绳的运行目录' 94 99
    if(-not $FileCopyOnly){
        Set-RecordedValue CurrentUser Default 'Software\Rime\Weasel' 'RimeUserDir' $taskUserTarget
        foreach($taskView in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
            Set-RecordedValue LocalMachine $taskView 'SOFTWARE\Rime\Weasel' 'InstallDir' $taskProgram
            Set-RecordedValue LocalMachine $taskView 'SOFTWARE\Rime\Weasel' 'WeaselRoot' $taskProgram
            Set-RecordedValue LocalMachine $taskView 'SOFTWARE\Rime\Weasel' 'ServerExecutable' 'Mason.Server.exe'
            $taskBase=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$taskView)
            try{
                $taskUninstall=$taskBase.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Weasel')
                if($taskUninstall){try{$taskCommand=[string]$taskUninstall.GetValue('UninstallString')}finally{$taskUninstall.Dispose()};if($taskCommand -and $taskCommand.IndexOf($taskEngine,[StringComparison]::OrdinalIgnoreCase) -ge 0){Set-RecordedValue LocalMachine $taskView 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Weasel' 'UninstallString' ('"'+(Join-Path $taskProgram 'uninstall.exe')+'"');Set-RecordedValue LocalMachine $taskView 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Weasel' 'InstallLocation' $taskProgram}}
            }finally{$taskBase.Dispose()}
        }
        foreach($taskHive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)){
            foreach($taskView in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
                $taskBase=[Microsoft.Win32.RegistryKey]::OpenBaseKey($taskHive,$taskView)
                try{
                    $taskRunPath='SOFTWARE\Microsoft\Windows\CurrentVersion\Run';$taskRun=$taskBase.OpenSubKey($taskRunPath)
                    if(-not $taskRun){continue}
                    try{foreach($taskName in $taskRun.GetValueNames()){$taskCommand=[string]$taskRun.GetValue($taskName);$taskOldServer=Join-Path $taskEngine 'WeaselServer.exe';if($taskCommand -and $taskCommand.IndexOf($taskOldServer,[StringComparison]::OrdinalIgnoreCase) -ge 0){Set-RecordedValue $taskHive $taskView $taskRunPath $taskName ('"'+(Join-Path $taskProgram 'Mason.Server.exe')+'"')}}}finally{$taskRun.Dispose()}
                }finally{$taskBase.Dispose()}
            }
        }
        Set-RecordedValue LocalMachine Registry64 'SOFTWARE\Mason' 'InstallDir' $taskApp
        Set-RecordedValue LocalMachine Registry64 'SOFTWARE\Mason' 'EngineDir' $taskProgram
        Set-RecordedValue LocalMachine Registry64 'SOFTWARE\Mason' 'LogDir' $taskLogs
        Set-RecordedValue LocalMachine Registry64 'SOFTWARE\Mason' 'UserDir' $taskUserTarget
    }
    $taskPlan.source_recovery=$taskSourceRecovery.ToArray()
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ResultPath)))|Out-Null
    [IO.File]::WriteAllText($ResultPath,(ConvertTo-Json -InputObject $taskPlan -Depth 6),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $taskSnapshot '目录迁移记录.json'),(ConvertTo-Json -InputObject @{plan=$taskPlan;directory_moves=$taskMoves.ToArray();registry_changes=$taskRegistry.ToArray();file_copy_only=[bool]$FileCopyOnly} -Depth 10),[Text.UTF8Encoding]::new($false))
    Set-LayoutStage '目录整理与校验完成' 100 100
    Write-Output '程序、用户数据、日志和备份目录已整理完成。'
}catch{
    $taskFailure=$_
    for($taskIndex=$taskApplied.Count-1;$taskIndex -ge 0;$taskIndex--){
        $taskChange=$taskApplied[$taskIndex]
        $taskBase=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]([Enum]::Parse([Microsoft.Win32.RegistryHive],$taskChange.Hive)),[Microsoft.Win32.RegistryView]([Enum]::Parse([Microsoft.Win32.RegistryView],$taskChange.View)))
        try{$taskKey=$taskBase.CreateSubKey($taskChange.Path);try{if($taskChange.Existed){$taskKey.SetValue($taskChange.Name,$taskChange.Before,[Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind],$taskChange.Kind)))}else{$taskKey.DeleteValue($taskChange.Name,$false)}}finally{$taskKey.Dispose()}}finally{$taskBase.Dispose()}
    }
    for($taskIndex=$taskMoves.Count-1;$taskIndex -ge 0;$taskIndex--){$taskMove=$taskMoves[$taskIndex];if((Test-Path -LiteralPath $taskMove.To) -and -not(Test-Path -LiteralPath $taskMove.From)){[IO.Directory]::Move((Resolve-SafeTarget $taskMove.To),(Resolve-SafeTarget $taskMove.From))}}
    throw $taskFailure
}
