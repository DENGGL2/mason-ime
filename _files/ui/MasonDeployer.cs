using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using Mason.Common;
using Mason.Input;
using System.Windows.Forms;

[assembly: AssemblyTitle("麻绳输入法 · 设置入口")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyVersion("2.4.4.0")]
[assembly: AssemblyFileVersion("2.4.4.0")]

namespace Mason.Entry {
  public static class Program {
    public static string Quote(string value) {
      if(value.Length>0 && value.IndexOfAny(new[]{' ','\t','\r','\n','"'})<0)return value;
      var result=new StringBuilder("\"");int slashes=0;
      foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"')result.Append('\\',slashes*2+1).Append(c);else result.Append('\\',slashes).Append(c);slashes=0;}
      return result.Append('\\',slashes*2).Append('"').ToString();
    }
    [STAThread] public static int Main(string[] args){
      string engine=AppDomain.CurrentDomain.BaseDirectory;
      try {
        var menuAction=NativeMenu.Resolve(args);
        if(menuAction!=null){
          var runtime=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-runtime.json"),Encoding.UTF8));
          string app=Path.GetFullPath(Convert.ToString(runtime["app_dir"])),user=Path.GetFullPath(Convert.ToString(runtime["user_dir"]));
          if(Convert.ToString(runtime["product"])!="Mason" || !String.Equals(engine.TrimEnd('\\'),MasonPaths.Program(app).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) || !String.Equals(user,MasonPaths.UserData(app),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("菜单运行目录不匹配。");
          NativeMenu.Execute(menuAction,new ModeStore(user),delegate(string menuArguments){
            string file=MasonPaths.RuntimeFile(app,"ui","Mason.Settings.exe");
            if(!File.Exists(file) || FileVersionInfo.GetVersionInfo(file).ProductName!="Mason")throw new FileNotFoundException("未找到麻绳设置程序。");
            Process.Start(new ProcessStartInfo(file,menuArguments){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(file)});
          });return 0;
        }
        if(args.Length==1 && (String.Equals(args[0],"/update",StringComparison.OrdinalIgnoreCase) || String.Equals(args[0],"/updates",StringComparison.OrdinalIgnoreCase))){
          var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-settings.json"),Encoding.UTF8));
          string updateSettings=MasonPaths.RuntimeFile(Convert.ToString(config["app_dir"]),"ui","Mason.Settings.exe");
          Process.Start(new ProcessStartInfo(updateSettings,"--check-updates"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(updateSettings)});return 0;
        }
        bool settings=args.Length==0 || (args.Length==1 && (String.Equals(args[0],"/settings",StringComparison.OrdinalIgnoreCase) || String.Equals(args[0],"/config",StringComparison.OrdinalIgnoreCase)));
        if(settings){
          var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-settings.json"),Encoding.UTF8));
          if(Convert.ToString(data["product"])!="Mason")throw new InvalidDataException("设置入口配置无效，请重新运行麻绳安装程序。");
          string app=Path.GetFullPath(Convert.ToString(data["app_dir"])),exe=MasonPaths.RuntimeFile(app,"ui","Mason.Settings.exe");
          if(!File.Exists(exe) || FileVersionInfo.GetVersionInfo(exe).ProductName!="Mason")throw new FileNotFoundException("未找到新版设置窗口，请重新运行麻绳安装程序。");
          Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)});return 0;
        }
        string native=Path.Combine(engine,"WeaselDeployer.exe");if(!File.Exists(native))throw new FileNotFoundException("未找到输入法部署程序。");
        var arguments=new StringBuilder();foreach(string arg in args){if(arguments.Length>0)arguments.Append(' ');arguments.Append(Quote(arg));}
        var start=new ProcessStartInfo(native,arguments.ToString()){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=engine};MasonPaths.ConfigureLogs(start,engine);
        using(var process=Process.Start(start)){process.WaitForExit();return process.ExitCode;}
      }catch(Exception error){if(args.Length==0 || (args.Length==1 && (args[0]=="/settings" || args[0]=="/config")))MessageBox.Show(error.Message,"麻绳输入法",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}
    }
  }
}
