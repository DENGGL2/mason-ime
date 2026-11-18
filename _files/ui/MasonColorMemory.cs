using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Mason {
  public static class CustomColorMemory {
    public const string FileName=".mason-custom-colors.json";
    public static readonly string[] Keys={"custom_bg","custom_fg","custom_hl"};
    public static bool IsHex(string value){return value!=null && Regex.IsMatch(value,@"\A[0-9A-Fa-f]{6}\z");}
    public static string Normalize(string value){value=(value??"").Trim();if(value.StartsWith("#"))value=value.Substring(1);return value.ToUpperInvariant();}
    public static string[] Load(string user,string[] fallback){
      var colors=fallback.Take(3).Select(Normalize).ToArray();
      if(colors.Length!=3 || colors.Any(value=>!IsHex(value)))throw new ArgumentException("Invalid color defaults.");
      if(String.IsNullOrWhiteSpace(user))return colors;
      try{
        string path=Path.Combine(user,FileName);if(!File.Exists(path) || new FileInfo(path).Length>4096)return colors;
        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
        if(data==null)return colors;
        for(int i=0;i<Keys.Length;i++){object value;if(data.TryGetValue(Keys[i],out value)){string color=Normalize(Convert.ToString(value));if(IsHex(color))colors[i]=color;}}
      }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
      return colors;
    }
    public static void Save(string user,string[] colors){
      if(String.IsNullOrWhiteSpace(user) || colors.Length!=3 || colors.Any(value=>!IsHex(value)))throw new ArgumentException("Invalid color draft.");
      Directory.CreateDirectory(user);var data=new Dictionary<string,object>{{"version",1}};
      for(int i=0;i<Keys.Length;i++)data[Keys[i]]=colors[i].ToUpperInvariant();
      string path=Path.Combine(user,FileName),text=new JavaScriptSerializer().Serialize(data);
      if(File.Exists(path) && File.ReadAllText(path,Encoding.UTF8)==text)return;
      string temporary=Path.Combine(user,".mason-colors-new-"+Guid.NewGuid().ToString("N")+".json");
      using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){writer.Write(text);writer.Flush();stream.Flush(true);}
      if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
    }
  }
}
