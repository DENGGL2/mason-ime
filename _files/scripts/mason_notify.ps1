param([Parameter(Mandatory=$true)][string]$RequestPath)
$ErrorActionPreference='Stop'
$taskNotification=Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskApp=[IO.Path]::GetFullPath([string]$taskNotification.app_dir).TrimEnd('\')
$taskSettings=[IO.Path]::GetFullPath([string]$taskNotification.settings)
$taskExpected=Join-Path $taskApp '程序\资源\ui\Mason.Settings.exe'
if($taskSettings -ine $taskExpected -or -not(Test-Path -LiteralPath $taskSettings -PathType Leaf) -or -not(Test-Path -LiteralPath (Join-Path $taskApp 'mason-install.json') -PathType Leaf)){throw '通知入口不属于已安装的 Mason。'}
if([string]$taskNotification.kind -notin @('application','dictionary')){throw '通知类型无效。'}
if([string]$taskNotification.version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$'){throw '通知版本无效。'}
$taskAppId='Mason.InputMethod'
$taskProtocol='HKCU:\Software\Classes\mason-ime'
[void](New-Item -Path $taskProtocol -Force)
Set-Item -Path $taskProtocol -Value 'URL:Mason input method'
New-ItemProperty -Path $taskProtocol -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
$taskCommandKey=Join-Path $taskProtocol 'shell\open\command'
[void](New-Item -Path $taskCommandKey -Force)
Set-Item -Path $taskCommandKey -Value ('"'+$taskSettings+'" --update-notification "%1"')
$taskIdentity='HKCU:\Software\Classes\AppUserModelId\'+$taskAppId
[void](New-Item -Path $taskIdentity -Force)
New-ItemProperty -Path $taskIdentity -Name DisplayName -Value '麻绳输入法' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $taskIdentity -Name IconUri -Value (Join-Path (Split-Path $taskSettings -Parent) 'Mason.ico') -PropertyType String -Force | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace Mason.Notification {
  [ComImport,Guid("00021401-0000-0000-C000-000000000046")] class ShellLink {}
  [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IShellLink {
    void GetPath(IntPtr text,int count,IntPtr find,uint flags);void GetIDList(out IntPtr value);void SetIDList(IntPtr value);
    void GetDescription(IntPtr text,int count);void SetDescription([MarshalAs(UnmanagedType.LPWStr)]string value);
    void GetWorkingDirectory(IntPtr text,int count);void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)]string value);
    void GetArguments(IntPtr text,int count);void SetArguments([MarshalAs(UnmanagedType.LPWStr)]string value);
    void GetHotkey(out short value);void SetHotkey(short value);void GetShowCmd(out int value);void SetShowCmd(int value);
    void GetIconLocation(IntPtr text,int count,out int index);void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)]string value,int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)]string value,uint flags);void Resolve(IntPtr hwnd,uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)]string value);
  }
  [StructLayout(LayoutKind.Sequential,Pack=4)] struct PropertyKey {public Guid Format;public uint Id;}
  [StructLayout(LayoutKind.Explicit,Size=24)] struct PropertyValue {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Text;}
  [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyStore {
    uint GetCount();void GetAt(uint index,out PropertyKey key);void GetValue(ref PropertyKey key,out PropertyValue value);
    void SetValue(ref PropertyKey key,ref PropertyValue value);void Commit();
  }
  public static class Shortcut {
    public static void Register(string settings,string path,string appId){
      object shell=new ShellLink();
      try {var link=(IShellLink)shell;link.SetPath(settings);link.SetWorkingDirectory(Path.GetDirectoryName(settings));link.SetDescription("麻绳输入法设置");link.SetIconLocation(settings,0);
        var key=new PropertyKey {Format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),Id=5};
        var value=new PropertyValue {Type=31,Text=Marshal.StringToCoTaskMemUni(appId)};
        try{var properties=(IPropertyStore)shell;properties.SetValue(ref key,ref value);properties.Commit();}finally{Marshal.FreeCoTaskMem(value.Text);}
        Directory.CreateDirectory(Path.GetDirectoryName(path));((IPersistFile)shell).Save(path,true);
      }finally{Marshal.FinalReleaseComObject(shell);}
    }
  }
}
'@
$taskShortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'Mason\麻绳输入法.lnk'
[Mason.Notification.Shortcut]::Register($taskSettings,$taskShortcut,$taskAppId)
[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification,Windows.UI.Notifications,ContentType=WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime] | Out-Null
$taskXml=New-Object Windows.Data.Xml.Dom.XmlDocument
$taskTitle=[Security.SecurityElement]::Escape([string]$taskNotification.title)
$taskMessage=[Security.SecurityElement]::Escape([string]$taskNotification.message)
$taskTarget='mason-ime://'+[string]$taskNotification.kind
$taskXml.LoadXml('<toast activationType="protocol" launch="'+$taskTarget+'" duration="short"><visual><binding template="ToastGeneric"><text>'+$taskTitle+'</text><text>'+$taskMessage+'</text></binding></visual></toast>')
$taskToast=[Windows.UI.Notifications.ToastNotification]::new($taskXml)
$taskToast.Group='MasonUpdates'
$taskToast.Tag=([string]$taskNotification.kind).Substring(0,3)+'-'+[string]$taskNotification.version
$taskToast.ExpirationTime=[DateTimeOffset]::Now.AddDays(2)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($taskAppId).Show($taskToast)
