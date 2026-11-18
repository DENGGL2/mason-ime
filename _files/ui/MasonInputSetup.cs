using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Mason.Common;

[assembly: AssemblyTitle("麻绳输入法 · 输入配置")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyVersion("2.4.5.0")]
[assembly: AssemblyFileVersion("2.4.5.0")]

namespace Mason.InputSetup {
  public static class InputConfiguration {
    private const string Begin="# mason-input-controls-begin",End="# mason-input-controls-end";
    public static string Patch(string source){
      string text=source.Replace("\r\n","\n");
      text=Regex.Replace(text,@"(?m)^# mason-input-controls-begin\r?\n[\s\S]*?^# mason-input-controls-end\r?\n?","");
      var lines=text.TrimEnd('\n','\r').Split('\n').ToList();int start=lines.FindIndex(line=>Regex.IsMatch(line,@"^patch:\s*(?:#.*)?$"));
      if(start<0){if(lines.Any(line=>line.StartsWith("patch:")))throw new InvalidDataException("输入配置使用了内联 patch，原文件已保留。");lines.Add("patch:");start=lines.Count-1;}
      int end=lines.Count;for(int i=start+1;i<lines.Count;i++)if(lines[i].Length>0 && !Char.IsWhiteSpace(lines[i][0]) && !lines[i].StartsWith("#")){end=i;break;}
      var block=new[]{Begin,"  engine/processors/@before 0: lua_processor@*mason_modes","  engine/processors/@before 1: lua_processor@*mason_preedit","  engine/filters/@next: lua_filter@*mason_candidates",
        "  key_binder/bindings/+:",
        "    - { when: has_menu, accept: plus, send: Page_Down }",
        "    - { when: has_menu, accept: Shift+plus, send: Page_Down }",
        "    - { when: has_menu, accept: Shift+equal, send: Page_Down }",
        "    - { when: has_menu, accept: KP_Add, send: Page_Down }",
        "    - { when: has_menu, accept: KP_Subtract, send: Page_Up }",End};
      lines.InsertRange(end,block);return String.Join("\n",lines).TrimEnd()+"\n";
    }
    public static string Appearance(string source){
      string text=source.Replace("\r\n","\n");string pattern=@"(?m)^  ['"+"\""+@"]?style/display_tray_icon['"+"\""+@"]?:[^\r\n]*";
      if(Regex.IsMatch(text,pattern))return Regex.Replace(text,pattern,"  style/display_tray_icon: false");
      var match=Regex.Match(text,@"(?m)^patch:\s*(?:#.*)?$");if(!match.Success)throw new InvalidDataException("外观配置使用了内联 patch，原文件已保留。");
      return text.Insert(match.Index+match.Length,"\n  style/display_tray_icon: false");
    }
    public static string Apply(string app,string user){
      app=Path.GetFullPath(app);user=Path.GetFullPath(user);
      if(!String.Equals(user.TrimEnd('\\'),MasonPaths.UserData(app).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("输入配置目录不匹配。");
      string resources=MasonPaths.RuntimeResources(app),backup=Path.Combine(MasonPaths.Backups(app),"输入配置-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
      var changed=new List<string>();
      Action<string,byte[]> write=delegate(string relative,byte[] bytes){
        string target=Path.Combine(user,relative);if(File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes))return;
        if(File.Exists(target)){string saved=Path.Combine(backup,relative);Directory.CreateDirectory(Path.GetDirectoryName(saved));File.Copy(target,saved);}
        Directory.CreateDirectory(Path.GetDirectoryName(target));File.WriteAllBytes(target,bytes);if(!File.ReadAllBytes(target).SequenceEqual(bytes))throw new IOException("输入配置写入校验失败。");changed.Add(relative);
      };
      foreach(string name in new[]{"mason_modes.lua","mason_preedit.lua","mason_candidates.lua"})write(Path.Combine("lua",name),File.ReadAllBytes(Path.Combine(resources,"rime","lua",name)));
      string schema=Path.Combine(user,"rime_frost.custom.yaml");write("rime_frost.custom.yaml",new UTF8Encoding(false).GetBytes(Patch(File.Exists(schema)?File.ReadAllText(schema,Encoding.UTF8):"patch:\n")));
      string appearance=Path.Combine(user,"weasel.custom.yaml");write("weasel.custom.yaml",new UTF8Encoding(false).GetBytes(Appearance(File.Exists(appearance)?File.ReadAllText(appearance,Encoding.UTF8):"patch:\n")));
      return new JavaScriptSerializer().Serialize(new{ok=true,changed=changed,backup=Directory.Exists(backup)?backup:"",controls=4,candidate_marks=false});
    }
  }
  public static class Program {
    [STAThread]public static int Main(string[] args){if(args.Length!=3)return 1;try{string report=InputConfiguration.Apply(args[0],args[1]);File.WriteAllText(args[2],report,new UTF8Encoding(false));return 0;}catch(Exception error){try{File.WriteAllText(args[2],new JavaScriptSerializer().Serialize(new{ok=false,message=error.Message}),new UTF8Encoding(false));}catch{}return 1;}}
  }
}
