using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mason;

public static class MasonUpdateSettingsProof {
  static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Read(SettingsWindow window,string name){return typeof(SettingsWindow).GetField(name,Hidden).GetValue(window);}
  static object Call(SettingsWindow window,string name,params object[] values){return typeof(SettingsWindow).GetMethod(name,Hidden).Invoke(window,values);}
  static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
  static void Capture(SettingsWindow window,string path){window.UpdateLayout();var root=(Grid)Read(window,"root");var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);}
  [STAThread]public static int Main(string[] args){
    if(args.Length!=2)return 2;Directory.CreateDirectory(args[1]);int exit=1;var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
    var window=new SettingsWindow(new[]{"--backend",args[0]}) {WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000,Width=908,Height=740,ShowInTaskbar=false};
    window.Loaded+=async delegate{
      try{
        for(int attempt=0;attempt<80 && !(bool)Read(window,"ready");++attempt)await Task.Delay(50);Require((bool)Read(window,"ready"),"Fixture settings did not initialize.");
        Call(window,"LocateUpdateButton","guide");await Task.Delay(500);Capture(window,Path.Combine(args[1],"01-指南更新红点.png"));
        var flags=(UpdateFlags)Read(window,"updateFlags");Require(flags.Application && flags.Dictionary,"Both update badges must be available.");
        var check=(Task)Call(window,"CheckUpdates");await Task.Delay(700);Require(window.Buttons.Any(button=>AutomationProperties.GetName(button)=="立即更新"),"Immediate update action missing.");Capture(window,Path.Combine(args[1],"02-发现新版本.png"));await window.Dismiss(false);await check;
        string log=Path.Combine(args[1],"actions.jsonl");Require(!File.ReadAllText(log).Contains("download_update"),"Canceling the check prompt started a download.");
        Call(window,"LocateUpdateButton","maintenance");await Task.Delay(450);Capture(window,Path.Combine(args[1],"03-词库更新红点.png"));
        Call(window,"Theme","dark");Call(window,"LocateUpdateButton","guide");await Task.Delay(500);Capture(window,Path.Combine(args[1],"04-深色指南更新红点.png"));
        Call(window,"Theme","light");await Task.Delay(350);
        var download=(Task)Call(window,"UpdateApplication","2.4.10");await Task.Delay(800);Capture(window,Path.Combine(args[1],"05-窗口内下载.png"));await window.Dismiss(false);await download;
        Require(!File.ReadAllText(log).Contains("launch_update"),"Canceling download started the installer.");
        var complete=(Task)Call(window,"UpdateApplication","2.4.10");await Task.Delay(1650);Capture(window,Path.Combine(args[1],"06-校验更新.png"));await complete;
        string actions=File.ReadAllText(log);Require(actions.Split(new[]{"launch_update"},StringSplitOptions.None).Length==2,"Successful download must hand off exactly once.");Require(!window.IsVisible,"Settings must release its files before overwrite installation.");
        File.WriteAllText(Path.Combine(args[1],"result.json"),new JavaScriptSerializer().Serialize(new{status="passed",renderer="actual WPF",badges=true,cancel_before_download=true,cancel_download=true,progress_and_speed=true,verified_handoff_once=true,real_installation_changed=false}),new UTF8Encoding(false));exit=0;
      }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"error.txt"),error.ToString(),new UTF8Encoding(false));}
      finally{window.Busy=false;window.Close();app.Shutdown(exit);}
    };
    window.Show();app.Run();return exit;
  }
}
