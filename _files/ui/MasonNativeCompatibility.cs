using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("麻绳输入法 · 中文目录兼容性")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyVersion("2.4.5.0")]
[assembly: AssemblyFileVersion("2.4.5.0")]

namespace Mason.NativeUi {
  public static class CompatibilityProgram {
    public static int Main(string[] args){string report=args.Length==3?args[2]:null;try{
      if(args.Length!=3)throw new InvalidDataException("兼容性参数无效。");
      string engine=Path.GetFullPath(args[0]).TrimEnd('\\'),backup=Path.GetFullPath(args[1]),app=Path.GetDirectoryName(engine),target=Path.GetFullPath(report);
      if(Path.GetFileName(engine)!="程序" || !backup.StartsWith(Path.Combine(app,"备份")+"\\",StringComparison.OrdinalIgnoreCase) || !target.StartsWith(backup+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("兼容性目标超出安装目录。");
      var files=new List<object>();foreach(string name in new[]{"WeaselServer.exe","WeaselDeployer.exe"}){string path=Path.Combine(engine,name);bool changed=Resources.EnsureUtf8Manifest(path,Path.Combine(backup,"兼容修改前程序"));files.Add(new{file=name,changed=changed,utf8=Resources.HasUtf8Manifest(path),code_preserved=true});}
      File.WriteAllText(target,new JavaScriptSerializer().Serialize(new{ok=true,files=files}),new UTF8Encoding(false));return 0;
    }catch(Exception error){if(report!=null)try{File.WriteAllText(report,new JavaScriptSerializer().Serialize(new{ok=false,error=error.Message}),new UTF8Encoding(false));}catch{}return 1;}}
  }
}
