using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Mason {
  public sealed class UpdateFlags {
    public bool Application,Dictionary;
    public string ApplicationVersion="",DictionaryVersion="";
  }
  public static class UpdateIndicators {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern uint GetPrivateProfileString(string section,string key,string fallback,StringBuilder text,uint length,string path);
    private static string Read(string path,string key){var text=new StringBuilder(96);GetPrivateProfileString("updates",key,"",text,96,path);return text.ToString();}
    public static UpdateFlags Load(string user,string currentVersion){
      var result=new UpdateFlags();if(String.IsNullOrWhiteSpace(user))return result;string file=Path.Combine(user,"mason-update-status.ini");if(!File.Exists(file))return result;
      result.ApplicationVersion=Read(file,"app_version");result.DictionaryVersion=Read(file,"dictionary_version");
      Version current,latest;result.Application=Read(file,"app_available")=="1" && Version.TryParse(currentVersion,out current) && Version.TryParse(result.ApplicationVersion,out latest) && latest>current;
      result.Dictionary=Read(file,"dictionary_available")=="1";return result;
    }
  }
  public static class UpdateWatcher {
    private static string Scope {get{return "Mason.UpdateWatcher."+WindowsIdentity.GetCurrent().User.Value;}}
    public static void Stop(){try{using(var signal=EventWaitHandle.OpenExisting(@"Local\"+Scope+".Stop"))signal.Set();}catch(WaitHandleCannotBeOpenedException){}}
    private static bool EngineRunning(string engine){
      if(String.IsNullOrWhiteSpace(engine))return false;string expected=Path.GetFullPath(Path.Combine(engine,"WeaselServer.exe"));
      foreach(var process in Process.GetProcessesByName("WeaselServer"))using(process){try{if(String.Equals(Path.GetFullPath(process.MainModule.FileName),expected,StringComparison.OrdinalIgnoreCase))return true;}catch{}}
      return false;
    }
    public static void Run(string backendPath,string engine){
      using(var owner=new Mutex(false,@"Local\"+Scope)){
        bool owned=false;try{try{owned=owner.WaitOne(3000);}catch(AbandonedMutexException){owned=true;}if(!owned)return;
          using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,@"Local\"+Scope+".Stop"))using(var stopped=new EventWaitHandle(false,EventResetMode.ManualReset,@"Local\"+Scope+".Stopped")){
            stopped.Reset();try{stop.Reset();var backend=new Backend(backendPath);DateTime next=DateTime.MinValue;
            while(!stop.WaitOne(0) && EngineRunning(engine)){
              if(DateTime.UtcNow>=next){
                using(var cancel=new CancellationTokenSource()){
                  var task=backend.RunEvents("watch_updates",null,null,cancel.Token);
                  while(!task.IsCompleted){if(stop.WaitOne(100)){cancel.Cancel();break;}}
                  try{task.GetAwaiter().GetResult();}catch(OperationCanceledException){return;}catch{}
                }
                next=DateTime.UtcNow.AddHours(6);
              }
              if(stop.WaitOne(TimeSpan.FromSeconds(30)))break;
            }
            }finally{stopped.Set();}
          }
        }finally{if(owned)owner.ReleaseMutex();}
      }
    }
  }
}
