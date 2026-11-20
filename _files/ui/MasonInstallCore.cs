using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Mason.Common;

namespace Mason.Install {
  public sealed class InstallPlan {
    public string SourceDir, AppDir, EngineDir, UserDir, PreviousEngineDir, PreviousUserDir, LogDir, BackupRoot, UserSid, Mode="Hans", ExistingVersion="", RunDir;
    public bool LayoutMigrated;
    public bool UpdateDictionary, InstallCloud, ExistingComplete, EngineRegistered;
    public InstallPlan(){UpdateDictionary=true;InstallCloud=true;}
  }
  public sealed class ManifestEntry { public string Path, Sha256; public long Bytes; }
  public sealed class InstallManifest { public string Product, Version; public ManifestEntry[] Files; }
  public sealed class ProgressEvent {
    public string Type="progress", Phase, OperationId, Title, Message, Error, AppDir, UserDir, BackupDir, CompletedAt;
    public double? Percent;
    public long Bytes, TotalBytes;
    public double BytesPerSecond;
    public bool CanSkip, CanCancel, NeedsAttention, Indeterminate;
    public string[] Completed, Skipped;
  }
  public sealed class ControlMessage { public string OperationId, Action, Nonce; }
  public sealed class InstallResult {
    public bool Ok, Canceled;
    public string Message, AppDir, EngineDir, UserDir, BackupDir, CompletedAt, DictionaryStatus;
    public string[] Skipped;
  }
  public interface IInstallPlatform {
    bool IsAdministrator { get; }
    string CurrentUserSid { get; }
    string FindEngine();
    bool EngineUsable(string path);
    void InstallEngine(string installer,InstallPlan plan,Action tick);
    void RegisterEngine(InstallPlan plan,Action tick);
    void StopEngine(string path,Action tick);
    void ConfigureInterface(InstallPlan plan,Action tick);
    void Deploy(InstallPlan plan,Action tick);
    void Publish(InstallPlan plan,string backupDir,string[] skipped);
    void StartEngine(string path,Action tick);
    bool EngineRunning(string path);
  }
  public interface IInstallLayoutPlatform {
    void MigrateLayout(InstallPlan plan,Action<ProgressEvent> progress,Action tick);
    bool LayoutReady(InstallPlan plan);
  }
  public static class InstallFiles {
    public const string ProductVersion="2.4.7";
    public const string EngineHash="cf509534a8f5f8af9c98ed7cbb8f135439f145a8cbe7e50ede42bb5b5ab45c29";
    public const string EngineUrl="https://github.com/rime/weasel/releases/download/0.17.4/weasel-0.17.4.0-installer.exe";
    public const string ReleaseApi="https://api.github.com/repos/gaboolic/rime-frost/releases/latest";
    public static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=8*1024*1024};
    public static string Full(string path) { return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar); }
    public static bool Same(string a,string b) { return !String.IsNullOrWhiteSpace(a) && !String.IsNullOrWhiteSpace(b) && String.Equals(Full(a),Full(b),StringComparison.OrdinalIgnoreCase); }
    public static string Source(string root,string relative){string normalized=relative.Replace('\\','/');return normalized.StartsWith("_files/",StringComparison.Ordinal)?Child(MasonPaths.SourceResources(root),normalized.Substring(7)):Child(root,relative);}
    public static string Child(string root,string relative) {
      if(String.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.IndexOf(':')>=0)throw new InvalidDataException("安装包内的文件路径无效。");
      string normalized=relative.Replace('\\','/');
      foreach(string part in normalized.Split('/')){
        if(String.IsNullOrWhiteSpace(part) || part=="." || part==".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())>=0 || Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase))throw new InvalidDataException("安装包内的文件路径无效。");
      }
      string result=Full(System.IO.Path.Combine(root,normalized.Replace('/',System.IO.Path.DirectorySeparatorChar)));
      if(!result.StartsWith(Full(root)+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装路径超出目标文件夹。");
      CheckLinks(root,result);
      return result;
    }
    public static void CheckLinks(string root,string target){
      string current=target;
      while(current!=null && (Same(current,root) || current.StartsWith(Full(root)+"\\",StringComparison.OrdinalIgnoreCase))){
        if((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("目标包含文件链接，请选择普通文件夹。");
        if(Same(current,root))break;current=System.IO.Path.GetDirectoryName(current);
      }
    }
    public static string Hash(string path) { using(var input=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant(); }
    public static string HashBytes(byte[] data) {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
    public static int Machine(string path) { using(var reader=new BinaryReader(File.OpenRead(path))){if(reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException("程序文件无效。");reader.BaseStream.Position=0x3c;int offset=reader.ReadInt32();if(offset<64 || offset>reader.BaseStream.Length-6)throw new InvalidDataException("程序文件无效。");reader.BaseStream.Position=offset;if(reader.ReadUInt32()!=0x4550)throw new InvalidDataException("程序文件无效。");return reader.ReadUInt16();} }
    public static string Quote(string value) {
      var result=new StringBuilder("\"");int slashes=0;
      foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){result.Append('\\',slashes*2+1).Append(c);}else{result.Append('\\',slashes).Append(c);}slashes=0;}
      return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public static void AtomicText(string path,string text) {Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));string temp=path+".mason-new-"+Guid.NewGuid().ToString("N");File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
    public static bool DictionaryFile(string relative) {string value=relative.Replace('\\','/');string[] parts=value.Split('/');if(parts.Length>1)return new[]{"cn_dicts","cn_dicts_cell","en_dicts","opencc"}.Contains(parts[0]);return value.EndsWith(".dict.yaml",StringComparison.OrdinalIgnoreCase) || value=="essay.txt" || value=="zh-moqi.gram";}
  }
  public static class InstallationDetection {
    public static InstallPlan Detect(string source) {
      var plan=new InstallPlan {SourceDir=InstallFiles.Full(source),UserDir=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Rime"),UserSid=WindowsIdentity.GetCurrent().User.Value,Mode=SystemOutputMode(System.Globalization.CultureInfo.CurrentUICulture.Name)};
      using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Rime\Weasel"))if(key!=null){string user=key.GetValue("RimeUserDir") as string;if(!String.IsNullOrWhiteSpace(user))plan.UserDir=InstallFiles.Full(Environment.ExpandEnvironmentVariables(user));}
      var candidates=new List<string>();
      foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var key=machine.OpenSubKey(@"SOFTWARE\Mason"))if(key!=null){string app=key.GetValue("InstallDir") as string;if(!String.IsNullOrWhiteSpace(app))candidates.Add(app);}
      foreach(var drive in DriveInfo.GetDrives().Where(x=>x.DriveType==DriveType.Fixed))foreach(string parent in new[]{"Program Files","Program Files (x86)"})candidates.Add(System.IO.Path.Combine(drive.Name,parent,"Mason"));
      foreach(string folder in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
        string settings=MasonPaths.RuntimeFile(folder,"ui","Mason.Settings.exe");if(!File.Exists(settings))continue;
        try {var version=FileVersionInfo.GetVersionInfo(settings);if(version.ProductName!="Mason")continue;plan.AppDir=InstallFiles.Full(folder);Version product;plan.ExistingVersion=Version.TryParse(version.ProductVersion,out product)?product.Major+"."+product.Minor+"."+Math.Max(0,product.Build):"2.0.0";
          string receipt=System.IO.Path.Combine(folder,"mason-install.json");if(File.Exists(receipt)){var data=InstallFiles.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt));if(data.ContainsKey("product") && Convert.ToString(data["product"])=="Mason" && data.ContainsKey("version"))plan.ExistingVersion=Convert.ToString(data["version"]);}
          if(String.IsNullOrWhiteSpace(plan.ExistingVersion))plan.ExistingVersion="2.0.0";break;
        } catch { }
      }
      if(String.IsNullOrWhiteSpace(plan.AppDir))plan.AppDir=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Mason");
      plan.EngineDir=WindowsInstallPlatform.FindRegisteredEngine();plan.EngineRegistered=!String.IsNullOrWhiteSpace(plan.EngineDir);
      plan.ExistingComplete=CoreInstaller.IndexesReady(plan.UserDir,plan.Mode,false);
      plan.PreviousUserDir=plan.UserDir;plan.PreviousEngineDir=plan.EngineDir;plan.UserDir=MasonPaths.UserData(plan.AppDir);plan.LogDir=MasonPaths.Logs(plan.AppDir);plan.BackupRoot=MasonPaths.Backups(plan.AppDir);
      return plan;
    }
    public static string SystemOutputMode(string language){return Regex.IsMatch(language??"",@"^zh-(?:Hant(?:-|$)|TW$|HK$|MO$)",RegexOptions.IgnoreCase)?"Hant":"Hans";}
  }
  public sealed class WindowsInstallPlatform : IInstallPlatform,IInstallLayoutPlatform {
    public bool IsAdministrator { get {return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);} }
    public string CurrentUserSid {get{return WindowsIdentity.GetCurrent().User.Value;}}
    public static string FindRegisteredEngine() {
      foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var key=machine.OpenSubKey(@"SOFTWARE\Rime\Weasel"))if(key!=null){
        foreach(string name in new[]{"WeaselRoot","InstallDir"}){string root=key.GetValue(name) as string;if(String.IsNullOrWhiteSpace(root) || !Directory.Exists(root))continue;
          if(File.Exists(System.IO.Path.Combine(root,"WeaselSetup.exe")) && File.Exists(System.IO.Path.Combine(root,"rime.dll")))return InstallFiles.Full(root);
          foreach(string child in Directory.GetDirectories(root,"weasel-*").OrderByDescending(x=>VersionOf(x)))if(File.Exists(System.IO.Path.Combine(child,"WeaselSetup.exe")) && File.Exists(System.IO.Path.Combine(child,"rime.dll")))return InstallFiles.Full(child);
        }
      }return null;
    }
    private static Version VersionOf(string directory){Version version;return Version.TryParse(System.IO.Path.GetFileName(directory).Replace("weasel-",""),out version)?version:new Version(0,0);}
    public string FindEngine(){return FindRegisteredEngine();}
    public bool EngineUsable(string path) {
      if(String.IsNullOrWhiteSpace(path) || !InstallFiles.Same(path,FindRegisteredEngine()))return false;
      try {int machine=InstallFiles.Machine(System.IO.Path.Combine(path,"WeaselServer.exe"));return (machine==0x14c || machine==0x8664) && InstallFiles.Machine(System.IO.Path.Combine(path,"rime.dll"))==machine && File.Exists(System.IO.Path.Combine(path,"WeaselDeployer.exe")) && File.Exists(System.IO.Path.Combine(path,"WeaselSetup.exe")) && HasRegisteredProfile(path);} catch{return false;}
    }
    private static bool HasRegisteredProfile(string path){
      using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var layouts=machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts"))if(layouts!=null)foreach(string id in layouts.GetSubKeyNames())using(var layout=layouts.OpenSubKey(id)){string ime=layout.GetValue("Ime File") as string;if(ime!=null && Regex.IsMatch(System.IO.Path.GetFileName(ime),@"^weasel(?:x64|ARM|ARM64|ARM64X)?\.ime$",RegexOptions.IgnoreCase) && File.Exists(System.IO.Path.Combine(path,System.IO.Path.GetFileName(ime))))return true;}
      foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var tips=machine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP"))if(tips!=null)foreach(string id in tips.GetSubKeyNames()){
        using(var server=machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\"+id+@"\InprocServer32")){
          if(server==null)continue;string dll=server.GetValue(null) as string;if(String.IsNullOrWhiteSpace(dll))continue;dll=Environment.ExpandEnvironmentVariables(dll.Trim('"'));string file=System.IO.Path.GetFileName(dll);
          if(!Regex.IsMatch(file,@"^weasel(?:x64|ARM|ARM64|ARM64X)?\.(?:dll|ime)$",RegexOptions.IgnoreCase) || !File.Exists(System.IO.Path.Combine(path,file)))continue;
          // Windows installs the x64 module as System32\weasel.dll, while the payload calls it weaselx64.dll.
          if(!InstallFiles.Same(System.IO.Path.GetDirectoryName(dll),path) && !Mason.NativeUi.Resources.IsOwnedModule(path,dll))continue;
          using(var profiles=machine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP\"+id+@"\LanguageProfile"))if(profiles!=null)foreach(string language in profiles.GetSubKeyNames())using(var entries=profiles.OpenSubKey(language))if(entries!=null && entries.GetSubKeyNames().Length>0)return true;
        }
      }return false;
    }
    public static int Run(string file,string arguments,Action tick,int seconds){
      var start=new ProcessStartInfo(file,arguments){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=System.IO.Path.GetDirectoryName(file),RedirectStandardOutput=true,RedirectStandardError=true};MasonPaths.ConfigureLogs(start,start.WorkingDirectory);
      if(String.Equals(System.IO.Path.GetFileName(file),"powershell.exe",StringComparison.OrdinalIgnoreCase))start.EnvironmentVariables["PSModulePath"]=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","Modules")+";"+System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsPowerShell","Modules");
      using(var process=Process.Start(start)){
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();var watch=Stopwatch.StartNew();
        // WaitForExit waits only for this process. Start-Process -Wait also waits for the resident engine it starts.
        while(!process.WaitForExit(400)){if(tick!=null)tick();if(watch.Elapsed.TotalSeconds>seconds){try{process.Kill();process.WaitForExit(5000);}catch{}throw new InvalidOperationException("此步骤等待超时，已停止本次操作。可以检查状态后重试。");}}
        // Native installers can pass redirected handles to a resident process. Do not wait forever for EOF from descendants.
        if(process.ExitCode!=0){string detail=errors.IsCompleted?errors.Result.Trim():"";if(detail.Length>1500)detail=detail.Substring(detail.Length-1500);throw new InvalidOperationException("操作未完成（代码 "+process.ExitCode+"）"+(String.IsNullOrWhiteSpace(detail)?"。":"："+detail));}
        return process.ExitCode;
      }
    }
    public void InstallEngine(string installer,InstallPlan plan,Action tick){
      string target=MasonPaths.Program(plan.AppDir);
      // NSIS requires /D= to be the last parameter and the directory to be unquoted, including spaces.
      Run(installer,"/S /D="+target,tick,180);
    }
    public void RegisterEngine(InstallPlan plan,Action tick){Run(System.IO.Path.Combine(plan.EngineDir,"WeaselSetup.exe"),plan.Mode=="Hant"?"/t":"/s",tick,90);}
    private static string RunningProcessFile(Process process){
      if(process.HasExited)return null;
      try{return process.MainModule.FileName;}
      catch(System.ComponentModel.Win32Exception){
        // A quitting process can lose its module mapping before its exit handle is signaled.
        // Ignore that race only after confirming exit; never target an unidentified process.
        if(process.WaitForExit(600))return null;throw;
      }catch(InvalidOperationException){return null;}
    }
    private static void StopOwnedProcesses(string name,string file){
      foreach(var process in Process.GetProcessesByName(name))using(process){
        string path=RunningProcessFile(process);if(path==null || !InstallFiles.Same(path,file))continue;
        if(process.WaitForExit(1800))continue;
        try{process.Kill();}catch(InvalidOperationException){}
        if(!process.WaitForExit(5000))throw new TimeoutException("输入法服务仍在退出，请重试。");
      }
    }
    public void StopEngine(string path,Action tick){
      string server=System.IO.Path.Combine(path,"WeaselServer.exe"),launcher=System.IO.Path.Combine(path,"Mason.Server.exe");Run(File.Exists(launcher)?launcher:server,"/quit",tick,25);
      StopOwnedProcesses("WeaselServer",server);
      string config=System.IO.Path.Combine(path,"mason-runtime.json");if(File.Exists(config)){
        var values=InstallFiles.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(config,Encoding.UTF8));
        string tray=MasonPaths.RuntimeFile(Convert.ToString(values["app_dir"]),"ui","Mason.Tray.exe");
        StopOwnedProcesses("Mason.Tray",tray);
      }
    }
    public void MigrateLayout(InstallPlan plan,Action<ProgressEvent> progress,Action tick){
      string script=InstallFiles.Source(plan.SourceDir,"_files/scripts/_migrate_layout.ps1"),token=Guid.NewGuid().ToString("N");
      string result=System.IO.Path.Combine(plan.RunDir,"layout-result-"+token+".json"),status=System.IO.Path.Combine(plan.RunDir,"layout-progress-"+token+".json");
      string powershell=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
      Run(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -AppDir "+InstallFiles.Quote(plan.AppDir)+" -SourceDir "+InstallFiles.Quote(plan.SourceDir)+" -PreviousEngineDir "+InstallFiles.Quote(plan.EngineDir)+" -PreviousUserDir "+InstallFiles.Quote(plan.PreviousUserDir)+" -ResultPath "+InstallFiles.Quote(result)+" -ProgressPath "+InstallFiles.Quote(status),delegate{
        if(tick!=null)tick();if(progress!=null && File.Exists(status)){try{progress(InstallFiles.Json.Deserialize<ProgressEvent>(File.ReadAllText(status,Encoding.UTF8)));}catch(IOException){}catch(ArgumentException){}}
      },900);
      var values=InstallFiles.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(result,Encoding.UTF8));
      string engine=Convert.ToString(values["engine_dir"]),user=Convert.ToString(values["user_dir"]);
      if(!InstallFiles.Same(engine,MasonPaths.Program(plan.AppDir)) || !InstallFiles.Same(user,MasonPaths.UserData(plan.AppDir)))throw new InvalidDataException("迁移结果目录不匹配。");
      plan.EngineDir=engine;plan.UserDir=user;plan.LogDir=MasonPaths.Logs(plan.AppDir);plan.BackupRoot=MasonPaths.Backups(plan.AppDir);plan.LayoutMigrated=true;
      if(!LayoutReady(plan))throw new InvalidOperationException("目录已复制，但引擎路径验证未通过，请重试。");
    }
    public bool LayoutReady(InstallPlan plan){return plan.LayoutMigrated && InstallFiles.Same(plan.EngineDir,MasonPaths.Program(plan.AppDir)) && InstallFiles.Same(plan.UserDir,MasonPaths.UserData(plan.AppDir)) && File.Exists(System.IO.Path.Combine(plan.EngineDir,"Mason.Server.exe")) && File.Exists(System.IO.Path.Combine(plan.EngineDir,"mason-runtime.json")) && Directory.Exists(plan.UserDir) && EngineUsable(plan.EngineDir);}
    public void ConfigureInterface(InstallPlan plan,Action tick){
      string powershell=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
      string script=MasonPaths.RuntimeFile(plan.AppDir,"scripts","_settings_entry.ps1");
      Run(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -WeaselDir "+InstallFiles.Quote(plan.EngineDir)+" -AppDir "+InstallFiles.Quote(plan.AppDir),tick,60);
      script=MasonPaths.RuntimeFile(plan.AppDir,"scripts","_menu_protocol.ps1");
      Run(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -AppDir "+InstallFiles.Quote(plan.AppDir),tick,30);
      script=MasonPaths.RuntimeFile(plan.AppDir,"scripts","_apply_ui.ps1");
      Run(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -WeaselDir "+InstallFiles.Quote(plan.EngineDir)+" -RimeUserDir "+InstallFiles.Quote(plan.UserDir)+" -DuringInstall",tick,60);
      script=MasonPaths.RuntimeFile(plan.AppDir,"scripts","_native_ui.ps1");
      Run(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -WeaselDir "+InstallFiles.Quote(plan.EngineDir)+" -RimeUserDir "+InstallFiles.Quote(plan.UserDir),tick,90);
      script=MasonPaths.RuntimeFile(plan.AppDir,"scripts","_brand_windows.ps1");
      Run(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -WeaselDir "+InstallFiles.Quote(plan.EngineDir)+" -RimeUserDir "+InstallFiles.Quote(plan.UserDir),tick,30);
    }
    public void Deploy(InstallPlan plan,Action tick){Run(System.IO.Path.Combine(plan.EngineDir,"WeaselDeployer.exe"),"/deploy",tick,600);}
    public void Publish(InstallPlan plan,string backupDir,string[] skipped){
      var receipt=new {product="Mason",version=InstallFiles.ProductVersion,app_dir=plan.AppDir,engine_dir=plan.EngineDir,user_dir=plan.UserDir,log_dir=MasonPaths.Logs(plan.AppDir),backup_root=MasonPaths.Backups(plan.AppDir),mode=plan.Mode,completed_at=DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),backup_dir=backupDir,skipped=skipped};
      InstallFiles.AtomicText(System.IO.Path.Combine(plan.AppDir,"mason-install.json"),InstallFiles.Json.Serialize(receipt));
      using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var key=machine.CreateSubKey(@"SOFTWARE\Mason")) {key.SetValue("InstallDir",plan.AppDir);key.SetValue("Version",InstallFiles.ProductVersion);key.SetValue("EngineDir",plan.EngineDir);}
      foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var key=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Weasel",true))if(key!=null){string command=Convert.ToString(key.GetValue("UninstallString"));if(command.IndexOf(plan.EngineDir,StringComparison.OrdinalIgnoreCase)>=0 || (!String.IsNullOrWhiteSpace(plan.PreviousEngineDir) && command.IndexOf(plan.PreviousEngineDir,StringComparison.OrdinalIgnoreCase)>=0)){key.SetValue("DisplayName","麻绳输入法");key.SetValue("DisplayIcon",MasonPaths.RuntimeFile(plan.AppDir,"ui","Mason.Settings.exe"));}}
    }
    public void StartEngine(string path,Action tick){
      string launcher=System.IO.Path.Combine(path,"Mason.Server.exe");var start=new ProcessStartInfo(File.Exists(launcher)?launcher:System.IO.Path.Combine(path,"WeaselServer.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=path};MasonPaths.ConfigureLogs(start,path);Process.Start(start);
      var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<1800){if(tick!=null)tick();Thread.Sleep(200);}
    }
    public bool EngineRunning(string path){
      string server=System.IO.Path.Combine(path,"WeaselServer.exe");
      foreach(var process in Process.GetProcessesByName("WeaselServer"))using(process){try{if(InstallFiles.Same(process.MainModule.FileName,server) && !process.HasExited)return true;}catch{}}
      return false;
    }
  }
  sealed class SkipStepException : Exception { }
  public sealed class CoreInstaller {
    private readonly InstallPlan plan;
    private readonly IInstallPlatform platform;
    private readonly Action<ProgressEvent> report;
    private readonly Func<ControlMessage> control;
    private readonly List<string> completed=new List<string>(), skipped=new List<string>();
    private readonly HashSet<string> backups=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ProgressEvent state;
    private bool writesStarted, stopped, stopCompleted;
    private string backupDir, lastNonce;
    private InstallManifest manifest;
    private Dictionary<string,object> release;
    private Dictionary<string,string> dictionaryHashes=new Dictionary<string,string>();
    private string dictionaryStage;
    private bool dictionaryReady;
    private bool dictionaryAlreadyLatest;
    private Dictionary<string,object> previousDictionaryState;
    private Dictionary<string,string> setupPreferences=new Dictionary<string,string>();
    private long copied,totalCopy;
    private Stopwatch throttle=Stopwatch.StartNew();
    public string DictionaryApi=InstallFiles.ReleaseApi, EngineDownloadUrl=InstallFiles.EngineUrl;
    public bool AllowLoopbackForVerification;
    private string OriginalUserDir {get{return platform is IInstallLayoutPlatform && !plan.LayoutMigrated?plan.PreviousUserDir:plan.UserDir;}}
    private string TargetRelative(string relative){return platform is IInstallLayoutPlatform?MasonPaths.TargetRelative(relative):relative;}
    public CoreInstaller(InstallPlan value,IInstallPlatform actions,Action<ProgressEvent> callback,Func<ControlMessage> commands){plan=value;platform=actions;report=callback;control=commands;}
    private void Emit(string title,string message,double? percent,long bytes,long total,double speed,bool force){
      if(!force && throttle.ElapsedMilliseconds<120)return;throttle.Restart();state.Title=title;state.Message=message;state.Percent=percent;state.Indeterminate=!percent.HasValue;state.Bytes=bytes;state.TotalBytes=total;state.BytesPerSecond=speed;state.Completed=completed.ToArray();state.Skipped=skipped.ToArray();state.CanCancel=!writesStarted;state.AppDir=plan.AppDir;state.UserDir=plan.UserDir;state.BackupDir=backupDir;
      state.CanCancel=!writesStarted || state.NeedsAttention;
      if(report!=null)report(InstallFiles.Json.Deserialize<ProgressEvent>(InstallFiles.Json.Serialize(state)));
    }
    private void Tick(){Emit(state.Title,state.Message,state.Percent,state.Bytes,state.TotalBytes,state.BytesPerSecond,false);}
    private string Command(){if(control==null)return null;var message=control();if(message==null || message.OperationId!=state.OperationId || message.Nonce==lastNonce)return null;lastNonce=message.Nonce;return message.Action;}
    private void CheckControl(){string action=Command();if(action=="cancel" && !writesStarted)throw new OperationCanceledException();if(action=="skip" && state.CanSkip)throw new SkipStepException();}
    private bool Step(string phase,string title,string message,bool optional,Action action,Func<bool> validate){
      while(true){state=new ProgressEvent {Phase=phase,OperationId=Guid.NewGuid().ToString("N"),CanSkip=optional && !writesStarted};Emit(title,message,null,0,0,0,true);
        try {CheckControl();action();completed.Add(phase);state.CanSkip=false;Emit(title,"已完成",100,0,0,0,true);return true;}
        catch(SkipStepException){if(!optional)throw;skipped.Add(phase);Emit(title,"已跳过，使用本地安装内容继续。",null,0,0,0,true);return false;}
        catch(OperationCanceledException){throw;}
        catch(Exception error){
          state.NeedsAttention=true;state.Error=error.Message;state.CanSkip=optional || (validate!=null && SafeValidate(validate));state.Indeterminate=false;
          Emit("这一步需要处理",error.Message,0,0,0,0,true);
          if(control==null)throw;
          while(true){Thread.Sleep(180);string choice=Command();if(choice=="cancel")throw new OperationCanceledException();
            if(choice=="retry")break;
            if(choice=="check" || choice=="skip"){
              if(optional && choice=="skip"){skipped.Add(phase);state.NeedsAttention=false;Emit(title,"已跳过；输入法基本功能不受影响。",100,0,0,0,true);return false;}
              if(validate!=null && SafeValidate(validate)){completed.Add(phase);state.NeedsAttention=false;Emit(title,"已确认可用，继续安装。",100,0,0,0,true);return true;}
              state.CanSkip=false;Emit("这一步需要处理",error.Message+"\n当前检查未通过，这一步暂不能跳过。",0,0,0,0,true);
            }
          }
        }
      }
    }
    private static bool SafeValidate(Func<bool> validate){try{return validate();}catch{return false;}}
    public InstallResult Run(){
      try {
        Step("prepare","正在准备安装","检查安装材料和当前版本",false,Prepare,null);
        string installer=InstallFiles.Source(plan.SourceDir,"_files/weasel-0.17.4.0-installer.exe");
        plan.EngineDir=platform.FindEngine();
        if(!platform.EngineUsable(plan.EngineDir)) {
          if(!File.Exists(installer)){Step("engine-download","正在下载引擎","本地缺少引擎安装包",false,delegate{installer=System.IO.Path.Combine(plan.RunDir,"weasel-installer-"+Guid.NewGuid().ToString("N")+".exe");Download(EngineDownloadUrl,installer,12431118,InstallFiles.EngineHash,24*1024*1024);},null);}
          else if(InstallFiles.Hash(installer)!=InstallFiles.EngineHash)throw new InvalidDataException("本地引擎安装包校验失败，请使用完整安装包重试。");
        }
        if(plan.UpdateDictionary) {
          bool fetched=Step("dictionary-download","正在更新词库","检查公开发布的最新词库",true,PrepareDictionary,null);
          dictionaryReady=fetched && dictionaryReady;
        }
        CheckControl();writesStarted=true;
        bool reuseEngine=platform.EngineUsable(plan.EngineDir);
        Step("engine",reuseEngine?"正在检查引擎":"正在安装引擎",reuseEngine?"复用当前已注册的引擎":"正在准备输入法引擎",false,delegate{
          plan.EngineDir=platform.FindEngine();
          if(platform.EngineUsable(plan.EngineDir)){Emit("正在检查引擎","当前引擎已可用，无需重新安装或注册",100,0,0,0,true);return;}
          platform.InstallEngine(installer,plan,Tick);plan.EngineDir=platform.FindEngine();
          // A fresh engine installer normally registers its own profile. Only repair a missing registration.
          if(!platform.EngineUsable(plan.EngineDir) && !String.IsNullOrWhiteSpace(plan.EngineDir) && File.Exists(System.IO.Path.Combine(plan.EngineDir,"WeaselSetup.exe")))platform.RegisterEngine(plan,Tick);
          if(!platform.EngineUsable(plan.EngineDir))throw new InvalidOperationException("没有检测到可用引擎，请重试此步骤。");
        },delegate{plan.EngineDir=platform.FindEngine();return platform.EngineUsable(plan.EngineDir);});
        var layout=platform as IInstallLayoutPlatform;
        if(layout!=null)Step("layout","正在整理安装目录","迁移程序、个人词库、日志和备份",false,delegate{
          StopForUpdate();layout.MigrateLayout(plan,delegate(ProgressEvent value){Emit("正在整理安装目录",value.Message,value.Percent,value.Bytes,value.TotalBytes,0,true);},Tick);
        },delegate{return layout.LayoutReady(plan);});
        Step("backup","正在备份","保存即将覆盖的文件",false,BackupAll,null);
        Step("files","正在写入文件","安装程序、词库和简繁设置",false,WriteFiles,null);
        if(plan.InstallCloud)Step("cloud","正在安装云输入","添加云输入组件",true,WriteCloud,null);
        Step("interface","正在应用界面","连接新版设置窗口和候选词外观",true,delegate{platform.ConfigureInterface(plan,Tick);},null);
        Step("deploy","正在建立词库索引","首次部署可能需要几分钟，窗口会持续显示状态",false,delegate{
          platform.Deploy(plan,Tick);if(!IndexesReady(plan.UserDir,plan.Mode,true))throw new InvalidOperationException("部署程序已退出，但麻绳词库索引或默认方案未通过验证。");
        },delegate{return IndexesReady(plan.UserDir,plan.Mode,true);});
        Step("verify","正在验证安装","确认默认方案、设置入口和输入法服务",false,delegate{
          if(!IndexesReady(plan.UserDir,plan.Mode,true))throw new InvalidOperationException("词库验证未通过，请重新部署。");
          platform.StartEngine(plan.EngineDir,Tick);if(!platform.EngineRunning(plan.EngineDir))throw new InvalidOperationException("输入法服务启动后退出了，请重试并查看日志。");stopped=false;stopCompleted=false;
          if(dictionaryReady || dictionaryAlreadyLatest)SaveDictionaryState();platform.Publish(plan,backupDir,skipped.ToArray());
        },null);
        string time=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");return new InstallResult {Ok=true,Message=skipped.Count==0?"安装完成":"安装完成，部分可选功能已跳过",AppDir=plan.AppDir,EngineDir=plan.EngineDir,UserDir=plan.UserDir,BackupDir=backupDir,CompletedAt=time,DictionaryStatus=dictionaryAlreadyLatest?"词库已是最新":dictionaryReady?DateTime.Now.ToString("yyyy-MM-dd HH:mm")+" 词库已更新到最新":null,Skipped=skipped.ToArray()};
      } catch(OperationCanceledException){return new InstallResult {Canceled=true,Message=writesStarted?"安装未完成，备份和已写入的文件已保留，可以重新安装。":"已取消安装，现有输入法保持原状。",AppDir=plan.AppDir,UserDir=plan.UserDir,BackupDir=backupDir,Skipped=skipped.ToArray()};}
      catch(Exception error){return new InstallResult {Message=error.Message,AppDir=plan.AppDir,UserDir=plan.UserDir,BackupDir=backupDir,Skipped=skipped.ToArray()};}
      finally {if(stopped && !String.IsNullOrWhiteSpace(plan.EngineDir)){try{platform.StartEngine(plan.EngineDir,null);}catch{}}}
    }
    private void Prepare(){
      if(!platform.IsAdministrator)throw new InvalidOperationException("安装需要管理员权限，请重新点击安装按钮。");
      if(platform.CurrentUserSid!=plan.UserSid)throw new InvalidOperationException("请使用当前 Windows 用户确认管理员权限，以便把词库安装到正确的用户目录。");
      if(plan.Mode!="Hans" && plan.Mode!="Hant")throw new InvalidDataException("简繁选项无效。");
      plan.SourceDir=InstallFiles.Full(plan.SourceDir);plan.AppDir=InstallFiles.Full(plan.AppDir);plan.UserDir=InstallFiles.Full(plan.UserDir);
      if(platform is IInstallLayoutPlatform){if(String.IsNullOrWhiteSpace(plan.PreviousUserDir))plan.PreviousUserDir=plan.UserDir;plan.PreviousUserDir=InstallFiles.Full(plan.PreviousUserDir);plan.UserDir=MasonPaths.UserData(plan.AppDir);plan.LogDir=MasonPaths.Logs(plan.AppDir);plan.BackupRoot=MasonPaths.Backups(plan.AppDir);}
      foreach(string target in new[]{plan.AppDir,plan.UserDir}){
        if(target.Length<5 || InstallFiles.Same(target,System.IO.Path.GetPathRoot(target)) || InstallFiles.Same(target,Environment.GetFolderPath(Environment.SpecialFolder.Windows)) || InstallFiles.Same(target,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))throw new InvalidDataException("请选择专用安装文件夹。");
        bool inPlace=InstallFiles.Same(plan.SourceDir,plan.AppDir) && (InstallFiles.Same(target,plan.AppDir) || InstallFiles.Same(target,MasonPaths.UserData(plan.AppDir)));
        if(!inPlace && (target.StartsWith(plan.SourceDir+"\\",StringComparison.OrdinalIgnoreCase) || plan.SourceDir.StartsWith(target+"\\",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("安装目标和安装包不能互相包含。");
        InstallFiles.CheckLinks(target,target);
      }
      if((InstallFiles.Same(plan.UserDir,plan.AppDir) || plan.UserDir.StartsWith(plan.AppDir+"\\",StringComparison.OrdinalIgnoreCase)) && !InstallFiles.Same(plan.UserDir,MasonPaths.UserData(plan.AppDir)))throw new InvalidDataException("请选择独立的词库目录或安装目录内的用户数据文件夹。");
      string manifestPath=InstallFiles.Source(plan.SourceDir,"_files/install-manifest.json");manifest=InstallFiles.Json.Deserialize<InstallManifest>(File.ReadAllText(manifestPath,Encoding.UTF8));
      if(manifest.Product!="Mason" || manifest.Version!=InstallFiles.ProductVersion || manifest.Files==null || manifest.Files.Length<3)throw new InvalidDataException("安装包清单无效。");
      long done=0,all=manifest.Files.Sum(x=>x.Bytes);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(var file in manifest.Files){if(!names.Add(file.Path))throw new InvalidDataException("安装清单包含重复文件。");string source=InstallFiles.Source(plan.SourceDir,file.Path);
        if(file.Path=="_files/weasel-0.17.4.0-installer.exe" && !File.Exists(source))continue;
        if(!File.Exists(source) || new FileInfo(source).Length!=file.Bytes || InstallFiles.Hash(source)!=file.Sha256)throw new InvalidDataException("安装材料校验失败："+file.Path);
        done+=file.Bytes;Emit("正在准备安装","校验安装材料",all>0?100.0*done/all:100,done,all,0,false);CheckControl();
      }
      foreach(string custom in new[]{"default.custom.yaml","rime_frost.custom.yaml"}) {string path=System.IO.Path.Combine(OriginalUserDir,custom);if(File.Exists(path))MergePatch(File.ReadAllText(path,Encoding.UTF8),new Dictionary<string,string>());}
      string user=System.IO.Path.Combine(OriginalUserDir,"user.yaml");if(File.Exists(user))SelectSchema(File.ReadAllText(user,Encoding.UTF8));
      Directory.CreateDirectory(plan.RunDir);backupDir=System.IO.Path.Combine(MasonPaths.Backups(plan.AppDir),DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
      foreach(var process in Process.GetProcessesByName("Mason.Settings"))using(process){try{if(InstallFiles.Same(process.MainModule.FileName,MasonPaths.RuntimeFile(plan.AppDir,"ui","Mason.Settings.exe")))throw new InvalidOperationException("请先关闭麻绳设置窗口，再重试安装。");}catch(System.ComponentModel.Win32Exception){}}
      // Engine setup can create or change user configuration before the payload is applied.
      // Save these specific files first, then restore preferences after setup stops its service.
      foreach(string name in new[]{"default.custom.yaml","rime_frost.custom.yaml","weasel.custom.yaml","user.yaml","custom_phrase.txt"}){
        string source=InstallFiles.Child(OriginalUserDir,name);if(!File.Exists(source))continue;string saved=InstallFiles.Child(backupDir,"安装前用户配置/"+name);CopyAtomic(source,saved,null);if(InstallFiles.Hash(source)!=InstallFiles.Hash(saved))throw new IOException("安装前配置备份未通过校验。");setupPreferences[name]=saved;
      }
    }
    private sealed class CopyItem {public string Source,Target,Group,Relative;public long Bytes;}
    private List<CopyItem> Items(){
      var result=new List<CopyItem>();
      foreach(var file in manifest.Files){string source=InstallFiles.Source(plan.SourceDir,file.Path),relativeTarget=TargetRelative(file.Path),target=InstallFiles.Child(plan.AppDir,relativeTarget);if(!File.Exists(source))continue;
        if(!InstallFiles.Same(source,target) && (!File.Exists(target) || InstallFiles.Hash(target)!=file.Sha256))result.Add(new CopyItem {Source=source,Target=target,Group="程序",Relative=relativeTarget,Bytes=file.Bytes});
        if(!file.Path.StartsWith("_files/rime/",StringComparison.Ordinal))continue;string relative=file.Path.Substring("_files/rime/".Length);target=InstallFiles.Child(plan.UserDir,relative);
        if(File.Exists(target) && (relative=="custom_phrase.txt" || relative.EndsWith(".custom.yaml",StringComparison.OrdinalIgnoreCase) || (plan.ExistingComplete && InstallFiles.DictionaryFile(relative))))continue;
        if(dictionaryReady && dictionaryHashes.ContainsKey(relative))continue;
        if(!File.Exists(target) || InstallFiles.Hash(target)!=file.Sha256)result.Add(new CopyItem {Source=source,Target=target,Group="词库",Relative=relative,Bytes=file.Bytes});
      }
      string manifestPath=InstallFiles.Source(plan.SourceDir,"_files/install-manifest.json"),manifestTarget=InstallFiles.Child(plan.AppDir,TargetRelative("_files/install-manifest.json"));if(!InstallFiles.Same(manifestPath,manifestTarget))result.Add(new CopyItem {Source=manifestPath,Target=manifestTarget,Group="程序",Relative=TargetRelative("_files/install-manifest.json"),Bytes=new FileInfo(manifestPath).Length});
      if(dictionaryReady)foreach(var file in dictionaryHashes){string source=InstallFiles.Child(dictionaryStage,file.Key),target=InstallFiles.Child(plan.UserDir,file.Key);if(!File.Exists(target) || InstallFiles.Hash(target)!=file.Value)result.Add(new CopyItem {Source=source,Target=target,Group="词库",Relative=file.Key,Bytes=new FileInfo(source).Length});}
      return result;
    }
    private List<CopyItem> CloudItems(){
      int machine=InstallFiles.Machine(System.IO.Path.Combine(plan.EngineDir,"WeaselServer.exe"));if(machine!=0x14c && machine!=0x8664)throw new InvalidOperationException("当前引擎架构不支持此云输入组件。");
      string directory=InstallFiles.Source(plan.SourceDir,"_files/cloud/"+(machine==0x8664?"out-mingw64":"out-mingw"));
      var result=new List<CopyItem>();foreach(string source in Directory.GetFiles(directory,"*.dll")){if(InstallFiles.Machine(source)!=machine)throw new InvalidDataException("云输入组件与引擎架构不一致。");string target=InstallFiles.Child(plan.EngineDir,System.IO.Path.GetFileName(source));if(!File.Exists(target) || InstallFiles.Hash(target)!=InstallFiles.Hash(source))result.Add(new CopyItem {Source=source,Target=target,Group="引擎",Relative=System.IO.Path.GetFileName(source),Bytes=new FileInfo(source).Length});}return result;
    }
    private void StopForUpdate(){
      if(stopCompleted)return;stopped=true;platform.StopEngine(plan.EngineDir,Tick);stopCompleted=true;
    }
    private void BackupAll(){
      StopForUpdate();Directory.CreateDirectory(backupDir);
      foreach(var preference in setupPreferences){string target=InstallFiles.Child(plan.UserDir,preference.Key);if(!File.Exists(target) || InstallFiles.Hash(target)!=InstallFiles.Hash(preference.Value))CopyAtomic(preference.Value,target,null);}
      var items=Items();if(plan.InstallCloud)try{items.AddRange(CloudItems());}catch{};
      foreach(string name in new[]{"default.custom.yaml","rime_frost.custom.yaml","weasel.custom.yaml","user.yaml",".rime-mini-update.json"})items.Add(new CopyItem {Target=InstallFiles.Child(plan.UserDir,name),Group="词库",Relative=name});
      foreach(string name in new[]{"WeaselServer.exe","Mason.dll","Mason64.dll","Mason32.dll","Mason.Deployer.exe","mason-settings.json","weasel.dll","weaselx64.dll","weasel.ime","weaselx64.ime"})items.Add(new CopyItem {Target=InstallFiles.Child(plan.EngineDir,name),Group="引擎",Relative=name});
      items.Add(new CopyItem {Target=InstallFiles.Child(plan.AppDir,"mason-install.json"),Group="程序",Relative="mason-install.json"});
      long total=items.Where(x=>File.Exists(x.Target)).Sum(x=>new FileInfo(x.Target).Length),done=0;var records=new List<object>();
      foreach(var item in items){if(!File.Exists(item.Target) || !backups.Add(item.Target))continue;long bytes=new FileInfo(item.Target).Length;string target=InstallFiles.Child(backupDir,item.Group+"/"+item.Relative);CopyAtomic(item.Target,target,delegate(long delta){done+=delta;Emit("正在备份","保存现有文件",total>0?100.0*done/total:100,done,total,0,false);});if(InstallFiles.Hash(item.Target)!=InstallFiles.Hash(target))throw new IOException("备份文件校验失败，尚未覆盖原文件。");records.Add(new{original=item.Target,backup=target,sha256=InstallFiles.Hash(target)});}
      InstallFiles.AtomicText(System.IO.Path.Combine(backupDir,"备份清单.json"),InstallFiles.Json.Serialize(records));
      Emit("正在备份",records.Count==0?"没有需要覆盖的旧文件":"已备份 "+records.Count+" 个文件",100,done,total,0,true);
    }
    private void WriteFiles(){
      var items=Items();totalCopy=items.Sum(x=>x.Bytes);copied=0;Directory.CreateDirectory(plan.AppDir);Directory.CreateDirectory(plan.UserDir);
      foreach(var item in items){CopyAtomic(item.Source,item.Target,delegate(long delta){copied+=delta;Emit("正在写入文件",item.Group=="词库"?"写入麻绳词库":"安装程序文件",totalCopy>0?100.0*copied/totalCopy:100,copied,totalCopy,0,false);});if(InstallFiles.Hash(item.Source)!=InstallFiles.Hash(item.Target))throw new IOException("写入文件校验失败："+item.Relative);}
      var paths=new Dictionary<string,string>{{"schema_list",SchemaList(plan.UserDir) }};string custom=InstallFiles.Child(plan.UserDir,"default.custom.yaml");InstallFiles.AtomicText(custom,MergePatch(Read(custom),paths));
      custom=InstallFiles.Child(plan.UserDir,"rime_frost.custom.yaml");InstallFiles.AtomicText(custom,MergePatch(Read(custom),new Dictionary<string,string>{{"switches/@1/reset",plan.Mode=="Hant"?"1":"0"}}));
      custom=InstallFiles.Child(plan.UserDir,"user.yaml");InstallFiles.AtomicText(custom,SelectSchema(Read(custom)));
      custom=InstallFiles.Child(plan.UserDir,"weasel.custom.yaml");if(File.Exists(custom))InstallFiles.AtomicText(custom,MergePatch(Read(custom),new Dictionary<string,string>{{"style/layout/hilite_spacing","2"}}));
      // The staged schema also receives an explicit default. Custom overrides remain separate and backed up.
      string schema=InstallFiles.Child(plan.UserDir,"rime_frost.schema.yaml");string text=Read(schema);var pattern=new Regex(@"(?m)^(  - name: traditionalization\s*\r?\n)(?:    reset:[^\r\n]*\r?\n)?");
      if(!pattern.IsMatch(text))throw new InvalidDataException("方案中没有找到简繁开关，已停止部署。");text=pattern.Replace(text,"$1    reset: "+(plan.Mode=="Hant"?"1":"0")+"\n",1);
      text=text.Replace("lua_processor@*cloud_pinyin_processor","lua_processor@cloud_pinyin_processor").Replace("lua_translator@*cloud_pinyin_translator","lua_translator@cloud_pinyin_translator");
      if(!plan.InstallCloud)text=Regex.Replace(text,@"(?m)^    - lua_(?:processor@cloud_pinyin_processor|translator@cloud_pinyin_translator)[^\r\n]*\r?\n","");
      InstallFiles.AtomicText(schema,text);
      if(!File.Exists(InstallFiles.Child(plan.UserDir,"rime_frost.dict.yaml")) || !File.Exists(InstallFiles.Child(plan.UserDir,"rime.lua")))throw new InvalidDataException("词库和扩展配置不完整，请重新下载安装包。");
      Emit("正在写入文件","程序和默认方案已写入",100,copied,totalCopy,0,true);
    }
    private void WriteCloud(){var items=CloudItems();long total=items.Sum(x=>x.Bytes),done=0;foreach(var item in items)CopyAtomic(item.Source,item.Target,delegate(long delta){done+=delta;Emit("正在安装云输入","写入云输入组件",total>0?100.0*done/total:100,done,total,0,false);});}
    private static void CopyAtomic(string source,string target,Action<long> progress){
      Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));string temp=target+".mason-new-"+Guid.NewGuid().ToString("N");byte[] buffer=new byte[128*1024];
      using(var input=File.OpenRead(source))using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){int count;while((count=input.Read(buffer,0,buffer.Length))>0){output.Write(buffer,0,count);if(progress!=null)progress(count);}output.Flush(true);}
      if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
    }
    private static string Read(string path){return File.Exists(path)?File.ReadAllText(path,Encoding.UTF8):"";}
    public static string MergePatch(string existing,Dictionary<string,string> entries){
      var lines=existing.Replace("\r\n","\n").Split('\n').ToList();int start=lines.FindIndex(x=>Regex.IsMatch(x,@"^patch:\s*(?:#.*)?$"));
      if(start<0 && lines.Any(x=>Regex.IsMatch(x,@"^patch:")))throw new InvalidDataException("现有自定义配置使用内联 patch 写法，请先整理为分行配置后重试。原文件已保留。");
      if(start<0){if(lines.Count>0 && lines[lines.Count-1]!="")lines.Add("");lines.Add("patch:");start=lines.Count-1;}
      int end=start+1;while(end<lines.Count && (!Regex.IsMatch(lines[end],@"^\S") || lines[end].StartsWith("#")))end++;
      foreach(var entry in entries){int match=-1;for(int i=start+1;i<end;i++){var key=Regex.Match(lines[i],@"^  ([^\s].*?):(?:\s|$)");if(key.Success && key.Groups[1].Value.Trim('"','\'')==entry.Key){match=i;break;}}
        if(match>=0){int last=match+1;while(last<end && !Regex.IsMatch(lines[last],@"^  \S"))last++;lines.RemoveRange(match,last-match);end-=last-match;}
        var add=new List<string>();if(entry.Value.StartsWith("\n")){add.Add("  "+entry.Key+":");add.AddRange(entry.Value.Substring(1).Split('\n').Where(x=>x.Length>0));}else add.Add("  "+entry.Key+": "+entry.Value);lines.InsertRange(end,add);end+=add.Count;
      }
      return String.Join("\n",lines).TrimEnd()+"\n";
    }
    private static string SchemaList(string userDir){
      var schemas=new List<string>{"rime_frost"};string existing=Read(System.IO.Path.Combine(userDir,"default.custom.yaml"));var block=Regex.Match(existing,@"(?m)^  ['"" ]?schema_list['"" ]?:[ \t]*\r?\n((?:    .*\r?\n|[ \t]*\r?\n)*)");
      if(block.Success)foreach(Match item in Regex.Matches(block.Groups[1].Value,@"schema:\s*['"" ]?([A-Za-z0-9_\-]+)")){string schema=item.Groups[1].Value;if(!schemas.Contains(schema))schemas.Add(schema);}
      return "\n"+String.Join("\n",schemas.Select(x=>"    - schema: "+x));
    }
    public static string SelectSchema(string existing){
      if(Regex.IsMatch(existing,@"(?m)^var:[ \t]*[^\s#]"))throw new InvalidDataException("现有用户配置的 var 写法不兼容，原文件已保留。");
      if(!Regex.IsMatch(existing,@"(?m)^var:\s*(?:#.*)?$"))return existing.TrimEnd()+"\nvar:\n  previously_selected_schema: rime_frost\n";
      var lines=existing.Replace("\r\n","\n").Split('\n').ToList();int start=lines.FindIndex(x=>Regex.IsMatch(x,@"^var:\s*(?:#.*)?$")),end=start+1;while(end<lines.Count && (!Regex.IsMatch(lines[end],@"^\S") || lines[end].StartsWith("#")))end++;
      int index=lines.FindIndex(start+1,end-start-1,x=>Regex.IsMatch(x,@"^  previously_selected_schema:"));if(index>=0)lines[index]="  previously_selected_schema: rime_frost";else lines.Insert(end,"  previously_selected_schema: rime_frost");return String.Join("\n",lines).TrimEnd()+"\n";
    }
    public static bool IndexesReady(string root,string mode,bool strict){
      foreach(string file in new[]{"build/rime_frost.prism.bin","build/rime_frost.table.bin","build/rime_frost.schema.yaml","build/default.yaml"}){string path=System.IO.Path.Combine(root,file.Replace('/',System.IO.Path.DirectorySeparatorChar));if(!File.Exists(path) || new FileInfo(path).Length==0)return false;}
      if(!strict)return true;
      string schema=Read(System.IO.Path.Combine(root,"build","rime_frost.schema.yaml")),defaults=Read(System.IO.Path.Combine(root,"build","default.yaml"));
      if(!Regex.IsMatch(schema,@"schema_id:\s*rime_frost") || !schema.Contains("麻绳输入法") || !Regex.IsMatch(defaults,@"schema:\s*rime_frost"))return false;
      var tradition=Regex.Match(schema,@"(?m)^  - name: traditionalization\s*\r?\n((?:    .*\r?\n)*)");if(!tradition.Success || !Regex.IsMatch(tradition.Groups[1].Value,@"(?m)^    reset:\s*"+(mode=="Hant"?"1":"0")+@"\s*$"))return false;
      string dictionary=System.IO.Path.Combine(root,"rime_frost.dict.yaml"),table=System.IO.Path.Combine(root,"build","rime_frost.table.bin");if(!File.Exists(dictionary))return false;DateTime newest=File.GetLastWriteTimeUtc(dictionary);
      foreach(Match import in Regex.Matches(Read(dictionary),@"(?m)^  - ([A-Za-z0-9_/-]+)[ \t]*(?:#.*)?$")){string source=System.IO.Path.Combine(root,import.Groups[1].Value.Replace('/',System.IO.Path.DirectorySeparatorChar)+".dict.yaml");if(File.Exists(source) && File.GetLastWriteTimeUtc(source)>newest)newest=File.GetLastWriteTimeUtc(source);}
      if(File.GetLastWriteTimeUtc(table).AddSeconds(2)<newest)return false;
      return true;
    }
    private void PrepareDictionary(){
      dictionaryReady=false;dictionaryAlreadyLatest=false;dictionaryHashes.Clear();state.CanSkip=true;state.CanCancel=true;CheckControl();var request=Request(DictionaryApi);string payload;
      Exception interruption=null;using(var timer=new System.Threading.Timer(delegate{try{CheckControl();}catch(Exception error){interruption=error;request.Abort();}},null,150,150))try{
        using(var response=(HttpWebResponse)request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)){var value=new StringBuilder();char[] buffer=new char[4096];int count;while((count=reader.Read(buffer,0,buffer.Length))>0){value.Append(buffer,0,count);if(value.Length>2*1024*1024)throw new InvalidDataException("词库发布信息超出预期大小。");if(interruption!=null)throw interruption;}payload=value.ToString();}
        if(interruption!=null)throw interruption;
      }catch(WebException error){if(interruption!=null)throw interruption;throw new InvalidOperationException("无法检查词库更新。可以重试，或跳过并使用本地词库。",error);}
      var data=InstallFiles.Json.Deserialize<Dictionary<string,object>>(payload);var assets=((System.Collections.IEnumerable)data["assets"]).Cast<object>().OfType<Dictionary<string,object>>();Dictionary<string,object> asset=assets.FirstOrDefault(x=>Convert.ToString(x["name"])=="rime-frost-schemas.zip");if(asset==null)throw new InvalidDataException("最新发布没有找到完整词库包。");
      string url=Convert.ToString(asset["browser_download_url"]);var address=new Uri(url);if(!(AllowLoopbackForVerification && address.IsLoopback) && (address.Scheme!="https" || address.Host!="github.com" || !address.AbsolutePath.StartsWith("/gaboolic/rime-frost/releases/download/")))throw new InvalidDataException("词库下载地址不符合预期。");
      long size=Convert.ToInt64(asset["size"]);if(size<=0 || size>160L*1024*1024)throw new InvalidDataException("词库包大小超出预期。");
      release=new Dictionary<string,object>{{"release_id",data["id"]},{"asset_id",asset["id"]},{"release_tag",data["tag_name"]}};
      string statePath=System.IO.Path.Combine(OriginalUserDir,".rime-mini-update.json");try{previousDictionaryState=InstallFiles.Json.Deserialize<Dictionary<string,object>>(Read(statePath));}catch{previousDictionaryState=null;}
      if(previousDictionaryState!=null && previousDictionaryState.ContainsKey("release_id") && previousDictionaryState.ContainsKey("asset_id") && Convert.ToString(previousDictionaryState["release_id"])==Convert.ToString(data["id"]) && Convert.ToString(previousDictionaryState["asset_id"])==Convert.ToString(asset["id"]) && previousDictionaryState.ContainsKey("file_hashes")){
        var hashes=previousDictionaryState["file_hashes"] as Dictionary<string,object>;
        if(hashes!=null && hashes.Count>0 && hashes.All(x=>InstallFiles.DictionaryFile(x.Key) && File.Exists(InstallFiles.Child(OriginalUserDir,x.Key)) && InstallFiles.Hash(InstallFiles.Child(OriginalUserDir,x.Key))==Convert.ToString(x.Value))){dictionaryAlreadyLatest=true;release=previousDictionaryState;Emit("词库已是最新","本地词库已通过完整性检查",100,0,0,0,true);return;}
      }
      string digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):"";string path=System.IO.Path.Combine(plan.RunDir,"词库下载-"+Guid.NewGuid().ToString("N")+".zip");
      Download(url,path,size,digest.StartsWith("sha256:")?digest.Substring(7):null,160L*1024*1024);
      dictionaryStage=System.IO.Path.Combine(plan.RunDir,"词库校验-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dictionaryStage);StageDictionary(path,dictionaryStage);dictionaryReady=true;
    }
    private HttpWebRequest Request(string url){var address=new Uri(url);if(address.Scheme!="https" && !(AllowLoopbackForVerification && address.IsLoopback))throw new InvalidDataException("下载地址必须使用 HTTPS。");ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;var request=(HttpWebRequest)WebRequest.Create(address);request.UserAgent="Mason/2.1";request.Timeout=20000;request.ReadWriteTimeout=15000;request.AllowAutoRedirect=true;return request;}
    public void Download(string url,string target,long expectedBytes,string expectedHash,long maximum){
      CheckControl();var request=Request(url);bool completedDownload=false;long readBytes=0,lastBytes=0;var watch=Stopwatch.StartNew();double lastTime=0,speed=0;Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
      // A timer only reads user control. It aborts network I/O so skip/cancel works even during a stalled read.
      Exception interruption=null;var commandLock=new object();
      using(var timer=new System.Threading.Timer(delegate{lock(commandLock){try{CheckControl();}catch(Exception error){interruption=error;request.Abort();}}},null,150,150))try{
        using(var response=(HttpWebResponse)request.GetResponse())using(var input=response.GetResponseStream())using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){
          long total=expectedBytes>0?expectedBytes:response.ContentLength;if(response.ContentLength>0 && expectedBytes>0 && response.ContentLength!=expectedBytes)throw new InvalidDataException("下载文件的大小与发布信息不一致。");byte[] buffer=new byte[64*1024];int count;
          while((count=input.Read(buffer,0,buffer.Length))>0){if(interruption!=null)throw interruption;output.Write(buffer,0,count);readBytes+=count;if(readBytes>maximum)throw new InvalidDataException("下载内容超出允许大小。");double seconds=watch.Elapsed.TotalSeconds;if(seconds-lastTime>=.15){double instant=(readBytes-lastBytes)/(seconds-lastTime);speed=lastTime==0?instant:.35*instant+.65*speed;lastTime=seconds;lastBytes=readBytes;Emit(state.Phase=="engine-download"?"正在下载引擎":"正在下载词库","下载完成后会自动校验",total>0?Math.Min(100,100.0*readBytes/total):(double?)null,readBytes,total,speed,true);}}
          output.Flush(true);if(expectedBytes>0 && readBytes!=expectedBytes)throw new InvalidDataException("下载未完整完成，请重试。");
        }
        if(interruption!=null)throw interruption;if(!String.IsNullOrWhiteSpace(expectedHash) && InstallFiles.Hash(target)!=expectedHash.ToLowerInvariant())throw new InvalidDataException("下载文件的 SHA-256 校验失败。");completedDownload=true;
      }catch(WebException error){if(interruption!=null)throw interruption;throw new InvalidOperationException("下载连接中断或等待超时。可以重试"+(state.CanSkip?"，或跳过并使用本地词库。":"。"),error);}
      finally{if(!completedDownload)Emit(state.Title,"下载尚未完成，未写入已安装的词库。",null,readBytes,expectedBytes,speed,true);}
      Emit(state.Title,"下载已完成，校验通过",100,readBytes,expectedBytes,speed,true);
    }
    private void StageDictionary(string archivePath,string stage){
      using(var archive=ZipFile.OpenRead(archivePath)){
        var markers=archive.Entries.Where(x=>x.Name=="rime_frost.schema.yaml").ToArray();if(markers.Length!=1)throw new InvalidDataException("词库包结构不符合预期。");string prefix=markers[0].FullName.Substring(0,markers[0].FullName.Length-"rime_frost.schema.yaml".Length);long total=0;var selected=new List<KeyValuePair<ZipArchiveEntry,string>>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries){if(String.IsNullOrEmpty(entry.Name) || !entry.FullName.StartsWith(prefix,StringComparison.Ordinal))continue;string relative=entry.FullName.Substring(prefix.Length);if(!InstallFiles.DictionaryFile(relative))continue;InstallFiles.Child(stage,relative);if(!names.Add(relative))throw new InvalidDataException("词库包中有重复文件。");total+=entry.Length;if(total>700L*1024*1024)throw new InvalidDataException("词库解压大小超出预期。");selected.Add(new KeyValuePair<ZipArchiveEntry,string>(entry,relative));}
        if(!names.Contains("rime_frost.dict.yaml") || !names.Any(x=>x.StartsWith("cn_dicts/",StringComparison.Ordinal)))throw new InvalidDataException("词库包缺少核心词典。");long done=0;
        foreach(var pair in selected){CheckControl();string path=InstallFiles.Child(stage,pair.Value);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));using(var input=pair.Key.Open())using(var output=new FileStream(path,FileMode.CreateNew)){byte[] buffer=new byte[128*1024];int count;long expanded=0;while((count=input.Read(buffer,0,buffer.Length))>0){expanded+=count;if(expanded>pair.Key.Length)throw new InvalidDataException("词库文件展开大小不一致。");output.Write(buffer,0,count);done+=count;Emit("正在校验词库","检查词库内容和文件完整性",total>0?100.0*done/total:100,done,total,0,false);CheckControl();}if(expanded!=pair.Key.Length)throw new InvalidDataException("词库文件展开不完整。");}dictionaryHashes[pair.Value]=InstallFiles.Hash(path);}
      }
    }
    private void SaveDictionaryState(){
      release["checked_at"]=DateTime.Now.ToString("yyyy-MM-dd HH:mm");
      if(dictionaryAlreadyLatest)release["status"]="latest";else{release["updated_at"]=release["checked_at"];release["status"]="updated";release["file_hashes"]=dictionaryHashes;}
      InstallFiles.AtomicText(System.IO.Path.Combine(plan.UserDir,".rime-mini-update.json"),InstallFiles.Json.Serialize(release));
    }
  }
}
