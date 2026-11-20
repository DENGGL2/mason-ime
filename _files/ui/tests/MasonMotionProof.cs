using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Mason;

// Settings retain the published stable layout; only candidate windows animate.
public static class MasonMotionProof {
  static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Read(SettingsWindow window,string name){return typeof(SettingsWindow).GetField(name,Hidden).GetValue(window);}
  static void Call(SettingsWindow window,string name,params object[] args){typeof(SettingsWindow).GetMethod(name,Hidden).Invoke(window,args);}
  static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
  [STAThread]public static int Main(string[] args){
    if(args.Length!=2)return 2;
    var result=new Dictionary<string,object>();int exit=1;
    var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
    var window=new SettingsWindow(new[]{"--backend",args[0]}) {WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000};
    bool closed=false;window.Closed+=delegate{closed=true;};
    window.Loaded+=async delegate{
      try{
        for(int attempt=0;attempt<80 && !(bool)Read(window,"ready");attempt++)await Task.Delay(50);
        Require((bool)Read(window,"ready"),"Private settings backend did not initialize.");
        var root=(Grid)Read(window,"root");var title=(TextBlock)Read(window,"title");var frame=(Grid)Read(window,"frame");
        window.UpdateLayout();Point position=title.TransformToAncestor(frame).Transform(new Point());
        Require(root.Opacity==1 && root.RenderTransform.Value.IsIdentity,"Settings opened at a displaced or translucent position.");
        foreach(string page in new[]{"guide","maintenance","appearance","guide","maintenance"}){
          Call(window,"Navigate",page);window.UpdateLayout();
          Require((string)Read(window,"page")==page,"Navigation ended on an old page.");
          Require(title.TransformToAncestor(frame).Transform(new Point())==position,"Page switching displaced the heading.");
          Require(root.Opacity==1 && root.RenderTransform.Value.IsIdentity,"Page switching moved or faded the whole window.");
          if(page=="guide")Require(((Grid)Read(window,"footerRegion")).Visibility==Visibility.Collapsed && Read(window,"checkUpdatesButton")!=null,"Guide controls did not update immediately.");
          if(page=="maintenance")Require(Read(window,"correctionSwitch")!=null && Read(window,"learningSwitch")!=null,"Input controls were delayed or missing.");
        }
        window.Busy=true;window.Close();Require(!closed,"Closing interrupted an active operation.");window.Busy=false;
        var notice=window.Info("提示","设置验证");await Task.Delay(300);await window.Dismiss(true);await notice;
        Require(((Grid)Read(window,"overlay")).Visibility==Visibility.Collapsed,"Dialog blocked controls after closing.");
        window.Close();Require(closed,"Stable settings did not close immediately.");
        result["status"]="passed";result["stable_heading_position"]=true;result["immediate_controls"]=true;result["busy_close"]=true;exit=0;
      }catch(Exception error){result["status"]="failed";result["error"]=error.ToString();}
      finally{File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(result),new UTF8Encoding(false));if(!closed)window.Close();app.Shutdown(exit);}
    };
    window.Show();app.Run();return exit;
  }
}
