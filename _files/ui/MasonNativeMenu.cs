using System;

namespace Mason.Input {
  public sealed class NativeMenuAction {
    public string Kind,Value;
    public NativeMenuAction(string kind,string value){Kind=kind;Value=value;}
  }
  public static class NativeMenu {
    public static NativeMenuAction Resolve(string[] args){
      if(args.Length==1){
        if(String.Equals(args[0],"/dict",StringComparison.OrdinalIgnoreCase))return new NativeMenuAction("mode","ascii_mode");
        if(String.Equals(args[0],"/sync",StringComparison.OrdinalIgnoreCase))return new NativeMenuAction("mode","traditionalization");
      }
      if(args.Length==0 || !String.Equals(args[0],"--menu",StringComparison.OrdinalIgnoreCase))return null;
      if(args.Length!=2)throw new ArgumentException("菜单入口参数无效。");
      if(String.Equals(args[1],"mason:full-shape",StringComparison.OrdinalIgnoreCase))return new NativeMenuAction("mode","full_shape");
      if(String.Equals(args[1],"mason:translation",StringComparison.OrdinalIgnoreCase))return new NativeMenuAction("mode","chinese_english");
      if(String.Equals(args[1],"mason:dict",StringComparison.OrdinalIgnoreCase))return new NativeMenuAction("settings","--update-dictionary");
      throw new ArgumentException("不支持的菜单动作。");
    }
    public static void Execute(NativeMenuAction action,ModeStore modes,Action<string> settings){
      if(action.Kind=="mode"){modes.Toggle(action.Value);return;}
      if(action.Kind=="settings"){settings(action.Value);return;}
      throw new ArgumentException("不支持的菜单动作。");
    }
  }
}
