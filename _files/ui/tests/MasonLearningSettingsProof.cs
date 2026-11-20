using System;
using System.Collections.Generic;
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

public static class MasonLearningSettingsProof {
  static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Read(SettingsWindow window,string name){return typeof(SettingsWindow).GetField(name,Hidden).GetValue(window);}
  static object Call(SettingsWindow window,string name,params object[] values){return typeof(SettingsWindow).GetMethod(name,Hidden).Invoke(window,values);}
  static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
  static bool ContainsText(DependencyObject node,string text){
    var label=node as TextBlock;if(label!=null && label.Text==text)return true;
    for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(ContainsText(VisualTreeHelper.GetChild(node,i),text))return true;
    return false;
  }
  static void Capture(SettingsWindow window,string path){
    var root=(Grid)Read(window,"root");window.UpdateLayout();
    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))encoder.Save(output);
  }
  [STAThread]public static int Main(string[] args){
    try{return Run(args);}catch(Exception error){if(args.Length>1)File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(new{status="failed",error=error.ToString()}),new UTF8Encoding(false));return 1;}
  }
  static int Run(string[] args){
    if(args.Length!=3)return 2;
    var result=new Dictionary<string,object>();int exit=1;
    var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
    var window=new SettingsWindow(new[]{"--backend",args[0]}) {WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000};
    window.Loaded+=async delegate{
      try{
        for(int attempt=0;attempt<80 && !(bool)Read(window,"ready");attempt++)await Task.Delay(50);
        Require((bool)Read(window,"ready"),"Private settings initialization failed.");
        Call(window,"Navigate","maintenance");window.UpdateLayout();
        var control=(SettingsSwitch)Read(window,"learningSwitch");
        var cfg=(Dictionary<string,object>)Read(window,"cfg");
        Require(control!=null && control.IsChecked==false,"The new switch did not default to off.");
        Require(control.ActualWidth==64 && control.ActualHeight==48,"The established switch hit area changed.");
        var list=(StackPanel)Read(window,"list");
        Require(ContainsText(list,"柔性词频学习"),"Switch name was not displayed.");
        Require(ContainsText(list,"开启后根据输入频次权重修改排序，关闭则默认2次选择会自动置顶"),"Requested copy was changed.");
        control.IsChecked=true;Require(Convert.ToBoolean(cfg["learning_flexible"]),"Enabling the switch did not update its configuration.");
        await (Task)Call(window,"Save");
        var payload=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(args[2],Encoding.UTF8));
        var saved=(Dictionary<string,object>)payload["cfg"];
        Require(Convert.ToBoolean(saved["learning_flexible"]),"Save lost the selected learning mode.");
        Require(((Grid)Read(window,"overlay")).Visibility==Visibility.Collapsed,"Successful saving displayed an unwanted popup.");
        Call(window,"Reset");Require(!Convert.ToBoolean(cfg["learning_flexible"]),"Reset did not restore two-choice mode.");
        var title=(TextBlock)Read(window,"title");var frame=(Grid)Read(window,"frame");
        Point position=title.TransformToAncestor(frame).Transform(new Point());
        Call(window,"Navigate","guide");window.UpdateLayout();
        Require(title.TransformToAncestor(frame).Transform(new Point())==position,"The stable page heading shifted during navigation.");
        Require(((Grid)Read(window,"footerRegion")).Visibility==Visibility.Collapsed,"Guide save/reset buttons reappeared.");
        Call(window,"Navigate","maintenance");Call(window,"Theme","light");await Task.Delay(180);window.UpdateLayout();
        ((ScrollViewer)Read(window,"scroll")).ScrollToVerticalOffset(330);await Task.Delay(250);
        Capture(window,Path.Combine(Path.GetDirectoryName(args[1]),"input-learning-light.png"));
        Call(window,"Theme","dark");await Task.Delay(180);window.UpdateLayout();((ScrollViewer)Read(window,"scroll")).ScrollToVerticalOffset(330);await Task.Delay(250);
        Capture(window,Path.Combine(Path.GetDirectoryName(args[1]),"input-learning-dark.png"));
        result["status"]="passed";result["default"]="two deliberate selections";
        result["switch_save_and_reset"]=true;result["exact_requested_copy"]=true;
        result["stable_heading_position"]=true;result["success_popup_hidden"]=true;
        exit=0;
      }catch(Exception error){result["status"]="failed";result["error"]=error.ToString();}
      finally{File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(result),new UTF8Encoding(false));window.Close();app.Shutdown(exit);}
    };
    window.Show();app.Run();return exit;
  }
}
