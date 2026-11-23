using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mason;

public static class MasonReleaseScreenshots {
  static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Read(SettingsWindow window,string name){return typeof(SettingsWindow).GetField(name,Hidden).GetValue(window);}
  static void Call(SettingsWindow window,string name,params object[] values){typeof(SettingsWindow).GetMethod(name,Hidden).Invoke(window,values);}
  static void Capture(SettingsWindow window,string path){
    window.UpdateLayout();var root=(Grid)Read(window,"root");
    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
  }
  [STAThread]public static int Main(string[] args){
    if(args.Length!=2)return 2;
    try{return Run(args);}catch(Exception error){Directory.CreateDirectory(args[1]);File.WriteAllText(Path.Combine(args[1],"capture-error.txt"),error.ToString(),new UTF8Encoding(false));return 1;}
  }
  static int Run(string[] args){
    Directory.CreateDirectory(args[1]);int exit=1;
    var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
    var window=new SettingsWindow(new[]{"--backend",args[0]}) {WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000,Height=1160,ShowInTaskbar=false,ShowActivated=false};
    window.Loaded+=async delegate{
      try{
        for(int attempt=0;attempt<80 && !(bool)Read(window,"ready");attempt++)await Task.Delay(50);
        if(!(bool)Read(window,"ready"))throw new InvalidOperationException("Settings data did not initialize.");
        foreach(string page in new[]{"appearance","maintenance","guide"}){
          Call(window,"Navigate",page);
          foreach(string theme in new[]{"light","dark"}){
            Call(window,"Theme",theme);await Task.Delay(350);window.UpdateLayout();
            ((ScrollViewer)Read(window,"scroll")).ScrollToTop();await Task.Delay(100);
            string name=(page=="maintenance"?"input":page)+"-"+theme+".png";
            Capture(window,Path.Combine(args[1],name));
          }
        }
        File.WriteAllText(Path.Combine(args[1],"capture-result.json"),new JavaScriptSerializer().Serialize(new{status="passed",renderer="actual WPF settings",backend="read-only installed state",pages=3,themes=2}),new UTF8Encoding(false));exit=0;
      }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"capture-error.txt"),error.ToString(),new UTF8Encoding(false));}
      finally{window.Close();app.Shutdown(exit);}
    };
    window.Show();app.Run();return exit;
  }
}
