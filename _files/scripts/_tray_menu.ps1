param(
    [Parameter(Mandatory=$true)][string]$ServerPath,
    [switch]$InspectOnly,
    [switch]$Restart
)
$ErrorActionPreference='Stop'
$taskUi=Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'
if(-not ('Mason.NativeUi.Resources' -as [type])){Add-Type -Path (Join-Path $taskUi 'Mason.NativeTools.dll')}
$taskServer=(Get-Item -LiteralPath $ServerPath).FullName
if([IO.Path]::GetFileName($taskServer) -notmatch '^WeaselServer(?:_1)?\.exe$'){throw '请指定麻绳输入法的 WeaselServer.exe。'}
if(-not $InspectOnly -and -not(Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($taskServer)) 'Mason.Menu.dll') -PathType Leaf)){throw '请先运行麻绳安装小窗更新菜单入口，原服务和菜单未修改。'}
$taskMenus=[Mason.NativeUi.Resources]::Read($taskServer)
if($InspectOnly){ConvertTo-Json -InputObject @($taskMenus | ForEach-Object { [PSCustomObject]@{Resource=$_.Name;Language=$_.Language;Commands=$_.Commands;CommandIds=$_.CommandIds}}) -Depth 5;return}
$taskIcon=Join-Path $taskUi 'Mason.ico'
if([Mason.NativeUi.Resources]::IsCompact($taskServer) -and [Mason.NativeUi.Resources]::HasMenuRoutes($taskServer) -and [Mason.NativeUi.Resources]::HasMIcon($taskServer,$taskIcon,100)){Write-Output '菜单与 M 图标已是最新。';return}
$taskParent=[IO.Path]::GetDirectoryName($taskServer)
$taskStamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$taskStage=Join-Path $taskParent ('.mason-tray-new-'+$taskStamp+'.exe')
$taskBackup=$taskServer+'.mason-tray-backup-'+$taskStamp
Copy-Item -LiteralPath $taskServer -Destination $taskStage
[Mason.NativeUi.Resources]::Patch($taskStage,$taskIcon,100)
$taskWasRunning=$false
try{
    if($Restart){
        $taskProcesses=@(Get-Process -Name WeaselServer -ErrorAction SilentlyContinue | Where-Object {try{$_.Path -ieq $taskServer}catch{$false}})
        $taskWasRunning=$taskProcesses.Count -gt 0
        if($taskWasRunning){
            $taskQuit=Start-Process -FilePath $taskServer -ArgumentList '/quit' -WindowStyle Hidden -PassThru
            if(-not $taskQuit.WaitForExit(15000)){throw '麻绳服务暂未退出，原程序已保留。'}
            foreach($taskProcess in $taskProcesses){if(-not $taskProcess.HasExited -and -not $taskProcess.WaitForExit(6000)){Stop-Process -Id $taskProcess.Id -Force}}
        }
    }
    Copy-Item -LiteralPath $taskServer -Destination $taskBackup
    [IO.File]::Replace($taskStage,$taskServer,($taskServer+'.mason-tray-replaced-'+$taskStamp))
    if(-not [Mason.NativeUi.Resources]::IsCompact($taskServer) -or -not [Mason.NativeUi.Resources]::HasMenuRoutes($taskServer) -or -not [Mason.NativeUi.Resources]::HasMIcon($taskServer,$taskIcon,100)){throw '菜单与图标验收未通过；原文件备份已保留。'}
    Write-Output '托盘菜单已统一为七项，产品图标已验证为 M。'
}finally{
    if($Restart -and $taskWasRunning){Start-Process -FilePath $taskServer -WindowStyle Hidden}
}
