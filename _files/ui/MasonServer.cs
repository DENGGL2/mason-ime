using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Mason.Common;

[assembly: AssemblyTitle("麻绳输入法 · 服务启动器")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyVersion("2.4.7.0")]
[assembly: AssemblyFileVersion("2.4.7.0")]

namespace Mason.Service {
  public static class Program {
    private static string Quote(string value){if(value.Length>0 && value.IndexOfAny(new[]{' ','\t','\r','\n','"'})<0)return value;var b=new StringBuilder("\"");int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"')b.Append('\\',slashes*2+1).Append(c);else b.Append('\\',slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();}
    public static ProcessStartInfo StartInfo(string engine,string[] args){
      var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-runtime.json"),Encoding.UTF8));
      if(Convert.ToString(config["product"])!="Mason")throw new InvalidDataException("服务配置无效。");
      string app=Path.GetFullPath(Convert.ToString(config["app_dir"])),logs=Path.GetFullPath(Convert.ToString(config["log_dir"]));
      if(!String.Equals(Path.GetFullPath(engine).TrimEnd('\\'),MasonPaths.Program(app).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) || !String.Equals(logs,MasonPaths.Logs(app),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("服务目录不匹配。");
      string native=Path.Combine(engine,"WeaselServer.exe");if(!File.Exists(native))throw new FileNotFoundException("输入法引擎尚未就绪。");
      Directory.CreateDirectory(logs);Directory.CreateDirectory(Path.Combine(logs,"rime.weasel"));
      var arguments=new StringBuilder();foreach(string value in args){if(arguments.Length>0)arguments.Append(' ');arguments.Append(Quote(value));}
      var info=new ProcessStartInfo(native,arguments.ToString()){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=engine};
      info.EnvironmentVariables["TEMP"]=logs;info.EnvironmentVariables["TMP"]=logs;info.EnvironmentVariables["GLOG_log_dir"]=Path.Combine(logs,"rime.weasel");
      return info;
    }
    public static int Main(string[] args){try{
      string engine=AppDomain.CurrentDomain.BaseDirectory;
      var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-runtime.json"),Encoding.UTF8));
      string tray=MasonPaths.RuntimeFile(Convert.ToString(config["app_dir"]),"ui","Mason.Tray.exe");
      if(args.Length==1 && String.Equals(args[0],"/quit",StringComparison.OrdinalIgnoreCase) && File.Exists(tray)){
        using(var control=Process.Start(new ProcessStartInfo(tray,"--stop "+Quote(engine)){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){control.WaitForExit(3000);}
      }
      var native=Process.Start(StartInfo(engine,args));
      if(args.Length==1 && String.Equals(args[0],"/quit",StringComparison.OrdinalIgnoreCase)){using(native){return native.WaitForExit(15000)?native.ExitCode:1;}}
      return 0;
    }catch{return 1;}}
  }
}
