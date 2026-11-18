param(
    [string]$WeaselDir,
    [string]$RimeUserDir,
    [switch]$MenuOnly,
    [switch]$DuringInstall,
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

function Find-Weasel {
    foreach ($taskKey in @('HKLM:\SOFTWARE\WOW6432Node\Rime\Weasel', 'HKLM:\SOFTWARE\Rime\Weasel')) {
        $taskRoot = (Get-ItemProperty -LiteralPath $taskKey -Name InstallDir -ErrorAction SilentlyContinue).InstallDir
        if (-not $taskRoot -or -not (Test-Path -LiteralPath $taskRoot)) { continue }
        if (Test-Path -LiteralPath (Join-Path $taskRoot 'WeaselDeployer.exe')) { return $taskRoot }
        $taskChildren = @(Get-ChildItem -LiteralPath $taskRoot -Directory -Filter 'weasel-*' | Sort-Object Name -Descending)
        foreach ($taskChild in $taskChildren) {
            if (Test-Path -LiteralPath (Join-Path $taskChild.FullName 'WeaselDeployer.exe')) { return $taskChild.FullName }
        }
    }
    foreach ($taskBase in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $taskBase) { continue }
        $taskRimeBase = Join-Path $taskBase 'Rime'
        if (-not (Test-Path -LiteralPath $taskRimeBase)) { continue }
        foreach ($taskChild in @(Get-ChildItem -LiteralPath $taskRimeBase -Directory -Filter 'weasel-*' | Sort-Object Name -Descending)) {
            if (Test-Path -LiteralPath (Join-Path $taskChild.FullName 'WeaselDeployer.exe')) { return $taskChild.FullName }
        }
    }
    return $null
}

function Merge-Appearance([string]$Existing, [string]$Template) {
    if ($Existing -match '(?m)^patch:[ \t]*[^\s#]') {
        throw '现有配置使用了内联 patch 写法，已保留原文件；请在设置窗口中应用外观。'
    }
    $taskManaged = @($Template -split '\r?\n' | Where-Object { $_ -match '^  \S' })
    $taskKeys = @{}
    foreach ($taskLine in $taskManaged) { $taskKeys[($taskLine.Trim() -split ':', 2)[0]] = $true }
    $taskKeys['preset_color_schemes/custom'] = $true
    $taskLines = @($Template -split '\r?\n' | Where-Object { $_ -match '^  ' })
    $taskResult = New-Object 'System.Collections.Generic.List[string]'
    $taskResult.Add('# rime-mini-ui: system')
    $taskInPatch = $false
    $taskFoundPatch = $false
    $taskSkip = $false
    foreach ($taskLine in ($Existing -split '\r?\n')) {
        if ($taskLine -match '^# rime-mini-ui:') { continue }
        if ($taskLine -match '^patch:\s*(?:#.*)?$') {
            $taskInPatch = $true; $taskFoundPatch = $true; $taskSkip = $false
            $taskResult.Add($taskLine)
            continue
        }
        $taskContent = $taskLine.Trim()
        if ($taskInPatch -and $taskContent -and $taskLine -match '^\S' -and -not $taskLine.StartsWith('#')) {
            foreach ($taskNewLine in $taskLines) { $taskResult.Add($taskNewLine) }
            $taskInPatch = $false; $taskSkip = $false
        }
        if ($taskInPatch -and $taskLine -match '^  \S') {
            $taskEntryKey = (($taskContent -split ':', 2)[0]).Trim('"', "'")
            $taskSkip = $taskKeys.ContainsKey($taskEntryKey)
        }
        if ($taskInPatch -and $taskSkip) { continue }
        $taskResult.Add($taskLine)
    }
    if ($taskInPatch) { foreach ($taskLine in $taskLines) { $taskResult.Add($taskLine) } }
    if (-not $taskFoundPatch) {
        $taskResult.Add('patch:')
        foreach ($taskLine in $taskLines) { $taskResult.Add($taskLine) }
    }
    return ($taskResult -join "`n").Trim() + "`n"
}

try {
    if (-not $WeaselDir) { $WeaselDir = Find-Weasel }
    if (-not $WeaselDir -or -not (Test-Path -LiteralPath (Join-Path $WeaselDir 'WeaselServer.exe'))) {
        throw '未检测到麻绳输入法，请先运行 1_安装.bat。'
    }
    if (-not $RimeUserDir) {
        $RimeUserDir = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Rime\Weasel' -Name RimeUserDir -ErrorAction SilentlyContinue).RimeUserDir
        if (-not $RimeUserDir) { $RimeUserDir = Join-Path $env:APPDATA 'Rime' }
    }
    $taskAppearance = $null
    if (-not $MenuOnly) {
        if (-not (Test-Path -LiteralPath $RimeUserDir)) { throw '用户词库目录不存在，请先安装输入法。' }
        $taskTemplatePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'ui\default-appearance.yaml'
        $taskTemplate = [IO.File]::ReadAllText($taskTemplatePath, [Text.Encoding]::UTF8)
        $taskConfigPath = Join-Path $RimeUserDir 'weasel.custom.yaml'
        $taskExisting = if (Test-Path -LiteralPath $taskConfigPath) { [IO.File]::ReadAllText($taskConfigPath, [Text.Encoding]::UTF8) } else { '' }
        # 已经在本设置中保存过主题时保留用户选择。
        if ($taskExisting -notmatch '(?m)^# rime-mini-ui:\s*(light|dark|system)') {
            $taskAppearance = Merge-Appearance $taskExisting $taskTemplate
        }
    }
    $taskServer = Join-Path $WeaselDir 'WeaselServer.exe'
    if ($DuringInstall) {
        & (Join-Path $PSScriptRoot '_tray_menu.ps1') -ServerPath $taskServer
    } else {
        & (Join-Path $PSScriptRoot '_tray_menu.ps1') -ServerPath $taskServer -Restart
    }
    if ($taskAppearance) {
        if (Test-Path -LiteralPath $taskConfigPath) {
            $taskConfigBackup = $taskConfigPath + '.mini-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
            Copy-Item -LiteralPath $taskConfigPath -Destination $taskConfigBackup
        }
        [IO.File]::WriteAllText($taskConfigPath, $taskAppearance, (New-Object Text.UTF8Encoding($false)))
    }
    if(-not $MenuOnly){
        $taskInputHelper=Join-Path (Split-Path $PSScriptRoot -Parent) 'ui\Mason.InputSetup.exe'
        $taskRuntimePath=Join-Path $WeaselDir 'mason-runtime.json'
        if((Test-Path -LiteralPath $taskInputHelper) -and (Test-Path -LiteralPath $taskRuntimePath)){
            $taskRuntime=ConvertFrom-Json ([IO.File]::ReadAllText($taskRuntimePath,[Text.Encoding]::UTF8))
            $taskInputLog=Join-Path $taskRuntime.log_dir ('输入配置-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.json')
            $taskInputStart=[Diagnostics.ProcessStartInfo]::new($taskInputHelper,('"'+$taskRuntime.app_dir+'" "'+$RimeUserDir+'" "'+$taskInputLog+'"'))
            $taskInputStart.UseShellExecute=$false;$taskInputStart.CreateNoWindow=$true;$taskInputStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
            $taskInputProcess=[Diagnostics.Process]::Start($taskInputStart)
            try{if(-not $taskInputProcess.WaitForExit(30000)){throw '输入界面配置暂未完成。'};if($taskInputProcess.ExitCode -ne 0){throw ('输入界面配置失败：'+[IO.File]::ReadAllText($taskInputLog,[Text.Encoding]::UTF8))}}finally{$taskInputProcess.Dispose()}
        }
    }
    # 只为注册表中的实际安装同步 Windows 名称；测试副本不写系统注册表。
    if (-not $DuringInstall) {
        $taskRegistered = Find-Weasel
        if ($taskRegistered -and [IO.Path]::GetFullPath($taskRegistered).TrimEnd('\') -ieq [IO.Path]::GetFullPath($WeaselDir).TrimEnd('\')) {
            & (Join-Path $PSScriptRoot '_brand_windows.ps1') -WeaselDir $WeaselDir -RimeUserDir $RimeUserDir
        }
    }
    if (-not $DuringInstall -and -not $MenuOnly) {
        $taskDeploy = Start-Process -FilePath (Join-Path $WeaselDir 'WeaselDeployer.exe') -ArgumentList '/deploy' -WindowStyle Hidden -PassThru
        if (-not $taskDeploy.WaitForExit(300000)) { throw '外观已保存，部署仍在进行，可稍后在设置窗口中重试。' }
        if ($taskDeploy.ExitCode -ne 0) { throw ('部署返回错误：' + $taskDeploy.ExitCode) }
    }
    if (-not $DuringInstall -and -not $Quiet) {
        Add-Type -AssemblyName System.Windows.Forms
        $taskMessage = if ($MenuOnly) { '菜单与托盘界面已应用。' } else { '双主题外观与托盘界面已应用。' }
        [Windows.Forms.MessageBox]::Show($taskMessage, '麻绳输入法', 'OK', 'Information') | Out-Null
    }
} catch {
    if (-not $DuringInstall -and -not $Quiet) {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show($_.Exception.Message, '麻绳输入法 · 界面应用未完成', 'OK', 'Warning') | Out-Null
    }
    throw
}
