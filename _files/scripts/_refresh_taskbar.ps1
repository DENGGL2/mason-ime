param([switch]$RestartExplorer)
$ErrorActionPreference='Stop'
if(-not $RestartExplorer){throw '请在确认刷新任务栏后明确指定 RestartExplorer。'}
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class MasonShellIdentity {
  [DllImport("user32.dll")]static extern IntPtr GetShellWindow();
  [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
  public static int ProcessId(){uint process;IntPtr shell=GetShellWindow();if(shell==IntPtr.Zero)return 0;GetWindowThreadProcessId(shell,out process);return (int)process;}
}
'@
$taskShellId=[MasonShellIdentity]::ProcessId()
if($taskShellId -le 0){throw '未找到当前 Windows 任务栏进程。'}
$taskShell=Get-Process -Id $taskShellId
$taskExpected=[IO.Path]::GetFullPath((Join-Path $env:WINDIR 'explorer.exe'))
if($taskShell.ProcessName -ne 'explorer' -or $taskShell.Path -ine $taskExpected -or $taskShell.SessionId -ne (Get-Process -Id $PID).SessionId){throw '任务栏进程的所属会话验证未通过。'}
try{
    Stop-Process -Id $taskShellId -ErrorAction Stop
    [void]$taskShell.WaitForExit(10000)
}finally{
    if([MasonShellIdentity]::ProcessId() -le 0){Start-Process -FilePath $taskExpected -WindowStyle Hidden}
}
Write-Output '已请求 Windows 重新加载任务栏。'
