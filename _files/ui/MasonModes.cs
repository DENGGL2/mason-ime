using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mason.Input {
  public sealed class ModeState {
    public string Revision="initial";
    public readonly Dictionary<string,bool> Values=new Dictionary<string,bool> {{"ascii_mode",false},{"traditionalization",false},{"full_shape",false},{"chinese_english",false}};
    public ModeState Clone(){var state=new ModeState {Revision=Revision};foreach(var pair in Values)state.Values[pair.Key]=pair.Value;return state;}
  }
  public sealed class ModeStore {
    public static readonly string[] Names={"ascii_mode","traditionalization","full_shape","chinese_english"};
    public const string CommandFile=".mason-modes-command.ini",StateFile=".mason-modes-state.ini";
    private readonly string user;
    private ModeState last=new ModeState();
    public ModeStore(string directory){user=Path.GetFullPath(directory);}
    public static ModeState Parse(string text){
      if(String.IsNullOrEmpty(text) || text.Length>2048)return null;
      var fields=new Dictionary<string,string>(StringComparer.Ordinal);
      foreach(string line in text.Replace("\r","").Split('\n')){if(line.Length==0)continue;int equal=line.IndexOf('=');if(equal<=0 || fields.ContainsKey(line.Substring(0,equal)))return null;fields[line.Substring(0,equal)]=line.Substring(equal+1);}
      string revision,end;if(!fields.TryGetValue("revision",out revision) || !fields.TryGetValue("end",out end) || revision!=end || revision.Length==0 || revision.Length>80 || revision.Any(c=>!Char.IsLetterOrDigit(c) && c!='-' && c!='_'))return null;
      var state=new ModeState {Revision=revision};
      foreach(string name in Names){string value;if(!fields.TryGetValue(name,out value) || (value!="0" && value!="1"))return null;state.Values[name]=value=="1";}
      return state;
    }
    public static string Serialize(ModeState state){var text=new StringBuilder("revision="+state.Revision+"\n");foreach(string name in Names)text.Append(name).Append('=').Append(state.Values[name]?'1':'0').Append('\n');return text.Append("end=").Append(state.Revision).Append('\n').ToString();}
    private ModeState ReadFile(string name){try{return Parse(File.ReadAllText(Path.Combine(user,name),Encoding.UTF8));}catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}}
    public ModeState Read(){
      ModeState command=ReadFile(CommandFile),observed=ReadFile(StateFile);
      ModeState state=command!=null && (observed==null || observed.Revision!=command.Revision)?command:observed;
      if(state!=null)last=state;return last.Clone();
    }
    public ModeState Toggle(string name){
      if(!Names.Contains(name))throw new ArgumentException("不支持的输入模式。");
      ModeState state=Read();state.Values[name]=!state.Values[name];state.Revision=Guid.NewGuid().ToString("N");
      Directory.CreateDirectory(user);string file=Path.Combine(user,CommandFile),temporary=Path.Combine(user,".mason-modes-new-"+state.Revision+".ini");
      using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(false))){writer.Write(Serialize(state));writer.Flush();stream.Flush(true);}
      if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);
      if(Parse(File.ReadAllText(file,Encoding.UTF8)).Revision!=state.Revision)throw new IOException("输入模式未写入完成。");
      last=state;return state.Clone();
    }
  }
}
