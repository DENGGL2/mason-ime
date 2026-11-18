using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace Mason.Common {
  public static class MasonPaths {
    public static string Program(string app){return Path.Combine(app,"程序");}
    public static string Resources(string app){return Path.Combine(Program(app),"资源");}
    public static string UserData(string app){return Path.Combine(app,"用户数据");}
    public static string Logs(string app){return Path.Combine(app,"日志");}
    public static string Backups(string app){return Path.Combine(app,"备份");}
    public static string SourceResources(string app){
      string current=Resources(app),receipt=Path.Combine(app,"mason-install.json");
      if(Directory.Exists(current) && File.Exists(receipt))try{var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt,Encoding.UTF8));object engine;if(Convert.ToString(values["product"])=="Mason" && values.TryGetValue("engine_dir",out engine) && String.Equals(Path.GetFullPath(Convert.ToString(engine)).TrimEnd('\\'),Program(app).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return current;}catch{}
      string legacy=Path.Combine(app,"_files");return Directory.Exists(legacy)?legacy:current;
    }
    public static string RuntimeResources(string app){string current=Resources(app);return Directory.Exists(current)?current:SourceResources(app);}
    public static string RuntimeFile(string app,params string[] parts){string result=RuntimeResources(app);foreach(string part in parts)result=Path.Combine(result,part);return result;}
    public static string TargetRelative(string relative){string normalized=relative.Replace('\\','/');return normalized.StartsWith("_files/",StringComparison.Ordinal)?"程序/资源/"+normalized.Substring(7):normalized;}
    public static void ConfigureLogs(ProcessStartInfo info,string engine){
      string config=Path.Combine(engine,"mason-runtime.json");if(!File.Exists(config))return;
      var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(config,Encoding.UTF8));
      string app=Path.GetFullPath(Convert.ToString(values["app_dir"])),logs=Path.GetFullPath(Convert.ToString(values["log_dir"]));
      if(Convert.ToString(values["product"])!="Mason" || !String.Equals(Path.GetFullPath(engine).TrimEnd('\\'),Program(app).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) || !String.Equals(logs.TrimEnd('\\'),Logs(app).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("运行目录配置无效。");
      Directory.CreateDirectory(logs);Directory.CreateDirectory(Path.Combine(logs,"rime.weasel"));
      info.EnvironmentVariables["TEMP"]=logs;info.EnvironmentVariables["TMP"]=logs;info.EnvironmentVariables["GLOG_log_dir"]=Path.Combine(logs,"rime.weasel");
    }
  }
}
