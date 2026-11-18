param(
    [Parameter(Mandatory=$true)][string]$AppDir,
    [switch]$InspectOnly
)
$ErrorActionPreference='Stop'
$taskApp=[IO.Path]::GetFullPath($AppDir).TrimEnd('\')
$taskEntry=Join-Path $taskApp '程序\Mason.Deployer.exe'
if($taskApp.Contains('"') -or -not(Test-Path -LiteralPath $taskEntry -PathType Leaf)){throw '未找到麻绳菜单入口。'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($taskEntry).ProductName -ne 'Mason'){throw '菜单入口产品标识不匹配。'}
$taskExpected='"'+$taskEntry+'" --menu "%1"'
$taskRegistry=[Microsoft.Win32.Registry]::CurrentUser
$taskPath='Software\Classes\mason'
$taskExisting=$taskRegistry.OpenSubKey($taskPath)
$taskBefore=$null
$taskReady=$false
if($taskExisting){
    try{
        $taskOwner=[string]$taskExisting.GetValue('Mason.MenuOwner','')
        $taskOldApp=[string]$taskExisting.GetValue('Mason.InstallDir','')
        $taskCommandKey=$taskExisting.OpenSubKey('shell\open\command')
        try{$taskOldCommand=if($taskCommandKey){[string]$taskCommandKey.GetValue('')}else{''}}finally{if($taskCommandKey){$taskCommandKey.Dispose()}}
        $taskBefore=@{owner=$taskOwner;app_dir=$taskOldApp;command=$taskOldCommand}
        $taskReady=$taskOwner -eq 'Mason' -and $taskOldApp -ieq $taskApp -and $taskOldCommand -ceq $taskExpected -and $null -ne $taskExisting.GetValue('URL Protocol',$null)
        if(-not $InspectOnly -and $taskOwner -ne 'Mason'){throw 'mason 协议已由其他程序占用，未修改其注册信息。'}
    }finally{$taskExisting.Dispose()}
}
if($InspectOnly){[pscustomobject]@{Ready=$taskReady;ExpectedCommand=$taskExpected;Existing=$taskBefore}|ConvertTo-Json -Depth 4;return}
if($taskReady){Write-Output '麻绳菜单动作入口已是最新。';return}
$taskBackup=Join-Path $taskApp ('备份\菜单入口-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($taskBackup)|Out-Null
[IO.File]::WriteAllText((Join-Path $taskBackup '注册前.json'),(ConvertTo-Json -InputObject @{key='HKCU\Software\Classes\mason';previous=$taskBefore;next_command=$taskExpected} -Depth 4),[Text.UTF8Encoding]::new($false))
$taskKey=$taskRegistry.CreateSubKey($taskPath)
try{
    $taskKey.SetValue('','URL:Mason input menu')
    $taskKey.SetValue('URL Protocol','')
    $taskKey.SetValue('Mason.MenuOwner','Mason')
    $taskKey.SetValue('Mason.InstallDir',$taskApp)
    $taskCommandKey=$taskKey.CreateSubKey('shell\open\command')
    try{$taskCommandKey.SetValue('',$taskExpected)}finally{$taskCommandKey.Dispose()}
}finally{$taskKey.Dispose()}
Write-Output '原生菜单动作已连接到麻绳输入法。'
