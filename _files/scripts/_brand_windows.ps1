param(
    [Parameter(Mandatory = $true)][string]$WeaselDir,
    [Parameter(Mandatory = $true)][string]$RimeUserDir,
    [switch]$InspectOnly
)
$ErrorActionPreference = 'Stop'
$taskUi=Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'
if(-not ('Mason.NativeUi.Resources' -as [type])){Add-Type -Path (Join-Path $taskUi 'Mason.NativeTools.dll')}
# 只修改此安装的显示名称，不修改输入法 GUID、注册路径或用户布局。
$taskProgramDir = (Get-Item -LiteralPath $WeaselDir -ErrorAction Stop).FullName.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $taskProgramDir 'WeaselServer.exe'))) {
    throw '未找到麻绳输入法引擎，名称应用未执行。'
}
$taskChanges = [Collections.Generic.List[object]]::new()
$taskBases = [Collections.Generic.List[object]]::new()
try {
    foreach ($taskView in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $taskBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $taskView)
        $taskBases.Add($taskBase)
        $taskTip = $taskBase.OpenSubKey('SOFTWARE\Microsoft\CTF\TIP')
        if (-not $taskTip) { continue }
        try {
            foreach ($taskId in $taskTip.GetSubKeyNames()) {
                $taskClass = $taskBase.OpenSubKey("SOFTWARE\Classes\CLSID\$taskId\InprocServer32")
                if (-not $taskClass) { continue }
                try { $taskDll = [string]$taskClass.GetValue('') } finally { $taskClass.Dispose() }
                if (-not $taskDll) { continue }
                $taskDll = [Environment]::ExpandEnvironmentVariables($taskDll.Trim('"'))
                $taskDllDir = [IO.Path]::GetDirectoryName($taskDll)
                if (-not $taskDllDir -or
                    $taskDllDir.TrimEnd('\') -notin @($taskProgramDir,(Join-Path $env:WINDIR 'System32'),(Join-Path $env:WINDIR 'SysWOW64')) -or
                    -not [Mason.NativeUi.Resources]::IsOwnedModule($taskProgramDir,$taskDll)) { continue }
                $taskLanguages = $taskBase.OpenSubKey("SOFTWARE\Microsoft\CTF\TIP\$taskId\LanguageProfile")
                if (-not $taskLanguages) { continue }
                try {
                    foreach ($taskLang in $taskLanguages.GetSubKeyNames()) {
                        $taskLanguage = $taskLanguages.OpenSubKey($taskLang)
                        try {
                            foreach ($taskProfileId in $taskLanguage.GetSubKeyNames()) {
                                $taskPath = "SOFTWARE\Microsoft\CTF\TIP\$taskId\LanguageProfile\$taskLang\$taskProfileId"
                                $taskProfile = $taskBase.OpenSubKey($taskPath)
                                try {
                                    if ($taskProfile.GetValueNames() -notcontains 'Description') { continue }
                                    $taskOld = [string]$taskProfile.GetValue('Description'); $taskKind = $taskProfile.GetValueKind('Description')
                                } finally { $taskProfile.Dispose() }
                                $taskName = if ($taskLang -match '(?i)0409$') { 'Mason' } else { '麻绳输入法' }
                                if ($taskOld -eq $taskName) { continue }
                                if ($taskOld -notmatch '^(小狼毫(?:输入法)?|Weasel(?: Input Method)?|麻绳输入法|Mason)$') { continue }
                                $taskChanges.Add([PSCustomObject]@{ Base = $taskBase; View = [string]$taskView; Path = $taskPath; Name = 'Description'; Before = $taskOld; After = $taskName; Kind = $taskKind })
                            }
                        } finally { $taskLanguage.Dispose() }
                    }
                } finally { $taskLanguages.Dispose() }
            }
        } finally { $taskTip.Dispose() }
    }
    # 旧式 IME 与 TSF 使用同一个产品显示名。
    $taskBase = $taskBases[0]
    $taskLayouts = $taskBase.OpenSubKey('SYSTEM\CurrentControlSet\Control\Keyboard Layouts')
    if ($taskLayouts) {
        try {
            foreach ($taskId in $taskLayouts.GetSubKeyNames()) {
                $taskPath = "SYSTEM\CurrentControlSet\Control\Keyboard Layouts\$taskId"
                $taskLayout = $taskBase.OpenSubKey($taskPath)
                try {
                    $taskIme = [string]$taskLayout.GetValue('Ime File')
                    $taskOld = [string]$taskLayout.GetValue('Layout Text')
                    if ($taskIme -notmatch '^weasel(?:x64|ARM|ARM64|ARM64X)?\.ime$') { continue }
                    if ($taskOld -notmatch '^(小狼毫(?:输入法)?|Weasel(?: Input Method)?|麻绳输入法|Mason)$') { continue }
                    foreach ($taskName in @('Layout Text', 'Layout Display Name')) {
                        if ($taskLayout.GetValueNames() -notcontains $taskName) { continue }
                        $taskBefore = [string]$taskLayout.GetValue($taskName)
                        if ($taskBefore -eq '麻绳输入法') { continue }
                        $taskChanges.Add([PSCustomObject]@{ Base = $taskBase; View = 'Registry64'; Path = $taskPath; Name = $taskName; Before = $taskBefore; After = '麻绳输入法'; Kind = $taskLayout.GetValueKind($taskName) })
                    }
                } finally { $taskLayout.Dispose() }
            }
        } finally { $taskLayouts.Dispose() }
    }
    $taskRecords = @($taskChanges | ForEach-Object { [PSCustomObject]@{ View = $_.View; Path = $_.Path; Name = $_.Name; Before = $_.Before; After = $_.After; Kind = [string]$_.Kind } })
    if ($InspectOnly) { ConvertTo-Json -InputObject $taskRecords -Depth 5; return }
    if ($taskChanges.Count -eq 0) { Write-Output 'Windows 输入法名称已是最新，或没有需要修改的注册项。'; return }
    if (-not (Test-Path -LiteralPath $RimeUserDir -PathType Container)) { throw '用户词库目录不存在，无法备份名称注册项。' }
    $taskBackup = Join-Path $RimeUserDir ('.mason-brand-names-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
    [IO.File]::WriteAllText($taskBackup, (ConvertTo-Json -InputObject $taskRecords -Depth 5), [Text.UTF8Encoding]::new($false))
    $taskApplied = [Collections.Generic.List[object]]::new()
    try {
        foreach ($taskChange in $taskChanges) {
            $taskKey = $taskChange.Base.OpenSubKey($taskChange.Path, $true)
            if (-not $taskKey) { throw ('无法写入名称注册项：' + $taskChange.Path) }
            try {
                # Some Windows versions share TIP keys between the 32-bit and 64-bit views.
                # Accept the same planned value when an earlier view has already applied it.
                $taskCurrent=[string]$taskKey.GetValue($taskChange.Name)
                if ($taskCurrent -eq $taskChange.After) { continue }
                if ($taskCurrent -ne $taskChange.Before) { throw '名称注册项已变化，请重新应用。' }
                $taskKey.SetValue($taskChange.Name, $taskChange.After, $taskChange.Kind)
                $taskApplied.Add($taskChange)
                if ([string]$taskKey.GetValue($taskChange.Name) -ne $taskChange.After) { throw '名称写入验证未通过。' }
            } finally { $taskKey.Dispose() }
        }
    } catch {
        foreach ($taskChange in $taskApplied) {
            $taskKey = $taskChange.Base.OpenSubKey($taskChange.Path, $true)
            try { $taskKey.SetValue($taskChange.Name, $taskChange.Before, $taskChange.Kind) } finally { $taskKey.Dispose() }
        }
        throw
    }
    Write-Output 'Windows 输入法显示名称已统一为麻绳输入法 / Mason；如列表尚未刷新，请注销后重试。'
} finally {
    foreach ($taskBase in $taskBases) { $taskBase.Dispose() }
}
