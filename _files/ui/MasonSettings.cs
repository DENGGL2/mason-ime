using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using Mason.Common;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

[assembly: AssemblyTitle("麻绳输入法 · 设置")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyDescription("麻绳输入法设置")]
[assembly: AssemblyVersion("2.4.4.0")]
[assembly: AssemblyFileVersion("2.4.4.0")]

namespace Mason {
  // Data checked against FlClash v0.9.3 and its pinned prism_ui dependency.
  public static class Motion {
    public const int ThemeMs = 200, LookMs = 200, InkFadeMs = 200, TintReleaseMs = 250;
    public const int SegmentMs = 250, CheckMs = 200, DialogMs = 404;
    public static readonly CubicEase OutCubic = new CubicEase(.215,.61,.355,1);
    public static readonly CubicEase OutBack = new CubicEase(.175,.885,.32,1.275);
    public static readonly CubicEase FastOutSlowIn = new CubicEase(.4,0,.2,1);
    public static readonly CubicEase InOut = new CubicEase(.42,0,.58,1);
    public static readonly AlertEase Alert = new AlertEase();
    public static bool Enabled { get { return SystemParameters.ClientAreaAnimation; } }
    public static DoubleAnimation Number(double from,double to,int ms,IEasingFunction ease) {
      return new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(Enabled?ms:0)) { EasingFunction=ease, FillBehavior=FillBehavior.HoldEnd };
    }
    public static void Color(SolidColorBrush brush,Color target,int ms,IEasingFunction ease) {
      Color from=brush.Color;
      brush.BeginAnimation(SolidColorBrush.ColorProperty,null);
      brush.Color=target;
      if (Enabled && from!=target) brush.BeginAnimation(SolidColorBrush.ColorProperty,
        new ColorAnimation(from,target,TimeSpan.FromMilliseconds(ms)) {EasingFunction=ease,FillBehavior=FillBehavior.Stop});
    }
  }
  public sealed class CubicEase : EasingFunctionBase {
    private double x1,y1,x2,y2;
    public CubicEase(double a,double b,double c,double d) {x1=a;y1=b;x2=c;y2=d;EasingMode=EasingMode.EaseIn;}
    public CubicEase() : this(0,0,1,1) {}
    protected override Freezable CreateInstanceCore() {return new CubicEase(x1,y1,x2,y2);}
    private double Bezier(double t,double a,double b) {double s=1-t;return 3*s*s*t*a+3*s*t*t*b+t*t*t;}
    protected override double EaseInCore(double t) {
      if (t<=0 || t>=1) return t;
      double low=0,high=1,mid=.5;
      for(int i=0;i<24;i++){mid=(low+high)/2;if(Bezier(mid,x1,x2)<t)low=mid;else high=mid;}
      return Bezier(mid,y1,y2);
    }
  }
  public sealed class AlertEase : EasingFunctionBase {
    // Flutter withDurationAndBounce(275ms,bounce=0): omega=2*pi/.275.
    // prism_ui's SpringCurve adds residual*t over 404ms to finish at exactly 1.
    private const double Omega=2*Math.PI/.275;
    public AlertEase(){EasingMode=EasingMode.EaseIn;}
    private double Position(double seconds){return 1-(1+Omega*seconds)*Math.Exp(-Omega*seconds);}
    protected override double EaseInCore(double t){return Position(t*.404)+(1-Position(.404))*t;}
    protected override Freezable CreateInstanceCore(){return new AlertEase();}
  }
  public sealed class SmoothSurface : FrameworkElement {
    public static readonly DependencyProperty FillProperty=DependencyProperty.Register("Fill",typeof(Brush),typeof(SmoothSurface),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty=DependencyProperty.Register("Stroke",typeof(Brush),typeof(SmoothSurface),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeWidthProperty=DependencyProperty.Register("StrokeWidth",typeof(Thickness),typeof(SmoothSurface),new FrameworkPropertyMetadata(new Thickness(),FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RadiusProperty=DependencyProperty.Register("Radius",typeof(CornerRadius),typeof(SmoothSurface),new FrameworkPropertyMetadata(new CornerRadius(),FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill {get{return (Brush)GetValue(FillProperty);}set{SetValue(FillProperty,value);}}
    public Brush Stroke {get{return (Brush)GetValue(StrokeProperty);}set{SetValue(StrokeProperty,value);}}
    public Thickness StrokeWidth {get{return (Thickness)GetValue(StrokeWidthProperty);}set{SetValue(StrokeWidthProperty,value);}}
    public CornerRadius Radius {get{return (CornerRadius)GetValue(RadiusProperty);}set{SetValue(RadiusProperty,value);}}
    public SmoothSurface(){SnapsToDevicePixels=false;RenderOptions.SetEdgeMode(this,EdgeMode.Unspecified);}
    protected override void OnRender(DrawingContext dc){
      double stroke=Stroke==null?0:StrokeWidth.Left,half=stroke/2;
      double w=Math.Max(0,ActualWidth-stroke),h=Math.Max(0,ActualHeight-stroke);
      if(w<=0 || h<=0)return;
      double radius=Math.Max(0,Radius.TopLeft-half);
      dc.DrawRoundedRectangle(Fill,stroke>0?new Pen(Stroke,stroke):null,new Rect(half,half,w,h),radius,radius);
    }
  }
  public sealed class SettingsButton : Button {
    public const double SharedCornerRadius=22;
    public string Kind,Caption,IconKey;
    public bool Selected,Ink;
    private SettingsWindow owner;
    private Border tint,hover;
    private SolidColorBrush back,fore,line;
    private static ControlTemplate template=(ControlTemplate)XamlReader.Parse(@"
      <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:Mason;assembly=Mason.Settings' TargetType='{x:Type Button}'>
        <Grid>
          <local:SmoothSurface Fill='{TemplateBinding Background}' Stroke='{TemplateBinding BorderBrush}' StrokeWidth='{TemplateBinding BorderThickness}' Radius='{Binding Tag,RelativeSource={RelativeSource TemplatedParent}}'/>
          <Border x:Name='PART_Hover' Background='{TemplateBinding Foreground}' Opacity='0' CornerRadius='{Binding Tag,RelativeSource={RelativeSource TemplatedParent}}'/>
          <Border x:Name='PART_Tint' Background='{TemplateBinding Foreground}' Opacity='0' CornerRadius='{Binding Tag,RelativeSource={RelativeSource TemplatedParent}}'/>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}'/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>");
    private static readonly Dictionary<double,Style> focusStyles=new Dictionary<double,Style>();
    private static Style FocusStyle(double corner){
      double radius=Math.Max(0,corner-3);Style style;
      if(!focusStyles.TryGetValue(radius,out style)){
        style=(Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Control}'><Setter Property='Template'><Setter.Value><ControlTemplate><Border BorderBrush='#808080' BorderThickness='1' CornerRadius='"+radius.ToString(CultureInfo.InvariantCulture)+@"' Margin='3'/></ControlTemplate></Setter.Value></Setter></Style>");
        focusStyles[radius]=style;
      }
      return style;
    }
    public SettingsButton(SettingsWindow window,string text,Action action,double width,string kind,string icon,bool selected,double height) {
      owner=window;Caption=text;Kind=kind;IconKey=icon;Selected=selected;Ink=kind=="palette";
      double corner=kind=="palette"?Math.Min(width,height)/2:kind=="primary"?height/2:SharedCornerRadius;
      Width=width;Height=height;Template=template;Tag=new CornerRadius(corner);
      BorderThickness=new Thickness(kind=="text" || kind=="primary" || kind=="nav"?0:1.25);
      Cursor=Cursors.Hand;Focusable=true;FocusVisualStyle=FocusStyle(corner);Padding=new Thickness(12,0,12,0);
      back=new SolidColorBrush();fore=new SolidColorBrush();line=new SolidColorBrush();
      Background=back;Foreground=fore;BorderBrush=line;
      ApplyColors(false);
      if(text!=null) {
        var stack=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        if(icon!=null) stack.Children.Add(owner.Glyph(icon,18,fore,new Thickness(0,0,8,0)));
        stack.Children.Add(new TextBlock {Text=text,FontSize=14,Foreground=fore,VerticalAlignment=VerticalAlignment.Center});
        Content=stack;
      }
      AutomationProperties.SetName(this,text??kind);
      Click+=delegate {if(action!=null)action();};
      PreviewMouseLeftButtonDown+=delegate {if(tint!=null && IsEnabled){tint.BeginAnimation(OpacityProperty,null);tint.Opacity=.1;}};
      PreviewMouseLeftButtonUp+=delegate {ReleaseTint();};
      MouseLeave+=delegate {if(hover!=null)hover.Opacity=0;ReleaseTint();};
      MouseEnter+=delegate {if(hover!=null && IsEnabled)hover.Opacity=.04;};
      owner.Buttons.Add(this);
    }
    public override void OnApplyTemplate(){base.OnApplyTemplate();tint=GetTemplateChild("PART_Tint") as Border;hover=GetTemplateChild("PART_Hover") as Border;}
    private void ReleaseTint(){if(tint!=null)tint.BeginAnimation(OpacityProperty,Motion.Number(tint.Opacity,0,Ink?Motion.InkFadeMs:Motion.TintReleaseMs,Ink?null:Motion.InOut));}
    public void SetSelected(bool selected){SetSelected(selected,Motion.LookMs,Motion.FastOutSlowIn);}
    public void SetSelected(bool selected,int ms,IEasingFunction ease){Selected=selected;ApplyColors(true,ms,ease);}
    public void ApplyColors(bool animate){ApplyColors(animate,Motion.LookMs,Motion.FastOutSlowIn);}
    public void ApplyColors(bool animate,int ms,IEasingFunction ease) {
      Color b=Kind=="text"?Colors.Transparent:owner.ColorOf(Selected?"selected":"surface");
      if(Kind=="primary")b=owner.ColorOf("primary");
      if(Kind=="nav")b=Selected?owner.ColorOf("selected"):owner.ColorOf("rail");
      Color f=owner.ColorOf(Kind=="primary"?"onPrimary":"text");
      Color l=owner.ColorOf(Selected?"outline":"line");
      if(animate){Motion.Color(back,b,ms,ease);Motion.Color(fore,f,ms,ease);Motion.Color(line,l,ms,ease);}
      else{back.Color=b;fore.Color=f;line.Color=l;}
    }
  }
  public sealed class SettingsSwitch : System.Windows.Controls.Primitives.ToggleButton {
    public const double TrackWidth=52,TrackHeight=32,InteractionWidth=64,InteractionHeight=48;
    private SettingsWindow owner;
    public static readonly DependencyProperty PositionProperty=DependencyProperty.Register("Position",typeof(double),typeof(SettingsSwitch),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Position {get{return (double)GetValue(PositionProperty);}set{SetValue(PositionProperty,value);}}
    public SettingsSwitch(SettingsWindow window,bool selected){
      owner=window;Width=InteractionWidth;Height=InteractionHeight;IsThreeState=false;IsChecked=selected;
      Position=selected?1:0;OverridesDefaultStyle=true;Template=null;FocusVisualStyle=null;
      Cursor=Cursors.Hand;Focusable=true;SnapsToDevicePixels=false;RenderOptions.SetEdgeMode(this,EdgeMode.Unspecified);
      AutomationProperties.SetName(this,"模糊音");
    }
    protected override void OnChecked(RoutedEventArgs e){base.OnChecked(e);UpdatePosition(true);}
    protected override void OnUnchecked(RoutedEventArgs e){base.OnUnchecked(e);UpdatePosition(false);}
    private void UpdatePosition(bool selected){
      double current=Position;BeginAnimation(PositionProperty,null);Position=selected?1:0;
      if(IsLoaded && Motion.Enabled)BeginAnimation(PositionProperty,Motion.Number(current,selected?1:0,200,Motion.FastOutSlowIn));
      InvalidateVisual();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e){base.OnPropertyChanged(e);if(e.Property==IsPressedProperty || e.Property==IsMouseOverProperty || e.Property==IsEnabledProperty || e.Property==IsKeyboardFocusedProperty)InvalidateVisual();}
    protected override void OnRender(DrawingContext dc){
      if(owner==null)return;double p=Math.Max(0,Math.Min(1,Position));double x=(ActualWidth-TrackWidth)/2,y=(ActualHeight-TrackHeight)/2;
      dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));
      if(!IsEnabled)dc.PushOpacity(.38);
      dc.PushOpacity(1-p);dc.DrawRoundedRectangle(owner.BrushOf("switchTrack"),new Pen(owner.BrushOf("switchOutline"),2),new Rect(x+1,y+1,TrackWidth-2,TrackHeight-2),15,15);dc.Pop();
      dc.PushOpacity(p);dc.DrawRoundedRectangle(owner.BrushOf("primary"),null,new Rect(x,y,TrackWidth,TrackHeight),16,16);dc.Pop();
      double radius=8+4*p;var center=new Point(x+16+20*p,y+16);
      dc.PushOpacity(1-p);dc.DrawEllipse(owner.BrushOf("switchOutline"),null,center,radius,radius);dc.Pop();
      dc.PushOpacity(p);dc.DrawEllipse(owner.BrushOf("onPrimary"),null,center,radius,radius);dc.Pop();
      if(!IsEnabled)dc.Pop();
    }
  }

  public sealed class SettingsSlider : Slider {
    public const double InteractionHeight=72,ThumbInteractionWidth=96;
    private SettingsWindow owner;
    private double dragOffset;
    public SettingsSlider(SettingsWindow window,double min,double max,double value,string name) {
      owner=window;Minimum=min;Maximum=max;Value=value;Height=InteractionHeight;Focusable=true;
      OverridesDefaultStyle=true;Template=null;FocusVisualStyle=null;SmallChange=1;LargeChange=1;
      AutomationProperties.SetName(this,name);
      ValueChanged+=delegate {InvalidateVisual();};
    }
    protected override void OnRender(DrawingContext dc) {
      base.OnRender(dc);double width=Math.Max(12,ActualWidth);double x=6+(width-12)*(Value-Minimum)/(Maximum-Minimum),y=ActualHeight/2,half=Math.Min(16,y-2);
      dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,width,ActualHeight));
      dc.DrawRoundedRectangle(owner.BrushOf("track"),null,new Rect(0,y-4,width,8),4,4);
      dc.DrawRoundedRectangle(owner.BrushOf("primary"),null,new Rect(0,y-4,Math.Max(4,x),8),4,4);
      dc.DrawRoundedRectangle(owner.BrushOf("bg"),null,new Rect(x-6,y-half-1,12,half*2+2),6,6);
      dc.DrawRoundedRectangle(owner.BrushOf("primary"),null,new Rect(x-2,y-half,4,half*2),2,2);
      if(IsKeyboardFocused)dc.DrawRoundedRectangle(null,new Pen(owner.BrushOf("muted"),1),new Rect(x-7,y-half-2,14,half*2+4),4,4);
    }
    private void Pick(Point point){Value=Math.Round(Minimum+Math.Max(0,Math.Min(1,(point.X-6)/Math.Max(1,ActualWidth-12)))*(Maximum-Minimum));}
    private void BeginDrag(Point point){
      double center=6+(Math.Max(12,ActualWidth)-12)*(Value-Minimum)/(Maximum-Minimum);
      dragOffset=Math.Abs(point.X-center)<=ThumbInteractionWidth/2?point.X-center:0;
      MoveDrag(point);
    }
    private void MoveDrag(Point point){Pick(new Point(point.X-dragOffset,point.Y));}
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){if(owner.Busy)return;Focus();CaptureMouse();BeginDrag(e.GetPosition(this));e.Handled=true;}
    protected override void OnMouseMove(MouseEventArgs e){if(IsMouseCaptured && !owner.Busy)MoveDrag(e.GetPosition(this));}
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){if(IsMouseCaptured)ReleaseMouseCapture();dragOffset=0;e.Handled=true;}
    protected override void OnLostMouseCapture(MouseEventArgs e){dragOffset=0;base.OnLostMouseCapture(e);}
    protected override void OnKeyDown(KeyEventArgs e){if(owner.Busy)return;if(e.Key==Key.Left || e.Key==Key.Down)Value=Math.Max(Minimum,Value-1);else if(e.Key==Key.Right || e.Key==Key.Up)Value=Math.Min(Maximum,Value+1);else if(e.Key==Key.Home)Value=Minimum;else if(e.Key==Key.End)Value=Maximum;else{base.OnKeyDown(e);return;}e.Handled=true;}
  }
  public sealed class Backend {
    private string script,python;
    private readonly JavaScriptSerializer json=new JavaScriptSerializer();
    public Backend(string path){script=path;python=FindPython();}
    public static string Quote(string text){return "\""+text.Replace("\"","\\\"")+"\"";}
    private static string FindPython() {
      string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      foreach(string version in new[]{"314","313","312","311","310"}){
        string candidate=IOPath.Combine(local,"Programs","Python","Python"+version,"python.exe");if(File.Exists(candidate))return candidate;
      }
      foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine}){
        using(var key=hive.OpenSubKey(@"SOFTWARE\Python\PythonCore"))if(key!=null)foreach(string version in key.GetSubKeyNames().OrderByDescending(x=>x)){
          using(var install=key.OpenSubKey(version+@"\InstallPath"))if(install!=null){string candidate=install.GetValue("ExecutablePath") as string;
            if(String.IsNullOrEmpty(candidate)){string folder=install.GetValue(null) as string;if(folder!=null)candidate=IOPath.Combine(folder,"python.exe");}
            if(candidate!=null && File.Exists(candidate))return candidate;
          }
        }
      }
      return "python.exe";
    }
    public Task<Dictionary<string,object>> Run(string action,object payload,Action<string> progress) {
      return Task.Run(delegate {
        var info=new ProcessStartInfo(python,Quote(script)+" "+action) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        using(var process=Process.Start(info)) {
          var errors=process.StandardError.ReadToEndAsync();
          string request=json.Serialize(payload??new object());
          request=System.Text.RegularExpressions.Regex.Replace(request,@"[^\x00-\x7F]",match=>@"\u"+((int)match.Value[0]).ToString("x4"));
          process.StandardInput.Write(request);process.StandardInput.Close();
          Dictionary<string,object> result=null;string line;
          while((line=process.StandardOutput.ReadLine())!=null){
            var item=json.Deserialize<Dictionary<string,object>>(line);
            if(Convert.ToString(item["type"])=="progress"){if(progress!=null)progress(Convert.ToString(item["message"]));}
            else result=item;
          }
          process.WaitForExit();string error=errors.Result;
          if(result==null)throw new InvalidOperationException(String.IsNullOrWhiteSpace(error)?"未能读取设置，请确认 Python 3 已安装。":error);
          return result;
        }
      });
    }
  }
  public sealed class SettingsInstance : IDisposable {
    private readonly string pipeName;
    private readonly Mutex mutex;
    private bool owned;
    private volatile bool closing;
    private NamedPipeServerStream pending;
    public SettingsInstance():this(WindowsIdentity.GetCurrent().User.Value){}
    public SettingsInstance(string scope){pipeName="Mason.Settings."+scope;mutex=new Mutex(false,@"Local\"+pipeName);}
    public bool TryOwn(){try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}return owned;}
    public bool Send(string command){
      for(int attempt=0;attempt<12;attempt++){
        try{using(var client=new NamedPipeClientStream(".",pipeName,PipeDirection.Out)){client.Connect(150);using(var writer=new StreamWriter(client,new UTF8Encoding(false))){writer.WriteLine(command);writer.Flush();}return true;}}
        catch(IOException){}catch(TimeoutException){}
        Thread.Sleep(80);
      }
      return false;
    }
    public void Listen(Action<string> receive){
      if(!owned)throw new InvalidOperationException("The settings instance is not owned.");
      var listener=new Thread(delegate(){while(!closing){
        try{using(var server=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.None)){
          pending=server;if(closing)return;server.WaitForConnection();string command;using(var reader=new StreamReader(server,Encoding.UTF8))command=reader.ReadLine();pending=null;
          if(!closing && command!=null && command.Length<=80)receive(command);
        }}catch(IOException){}catch(ObjectDisposedException){}
      }});listener.IsBackground=true;listener.Start();
    }
    public void Dispose(){closing=true;var stream=pending;if(stream!=null)try{stream.Dispose();}catch{}if(owned){mutex.ReleaseMutex();owned=false;}mutex.Dispose();}
  }
  public sealed class SettingsWindow : Window {
    private const double TitleAreaHeight=72,ActionAreaHeight=80,ListEdgePadding=8;
    private const double ListFadeExtent=80;
    public readonly List<SettingsButton> Buttons=new List<SettingsButton>();
    private readonly Dictionary<string,SolidColorBrush> brushes=new Dictionary<string,SolidColorBrush>();
    private readonly Dictionary<string,string> icons=new Dictionary<string,string> {
      {"appearance","\ue790"},{"maintenance","\ue765"},{"guide","\ue82d"},{"menu","\ue700"},
      {"grid","\ue80a"},{"color","\ue790"},{"radius","\ue739"},{"font","\ue8d2"},{"preview","\ue765"},
      {"sun","\ue706"},{"moon","\ue708"},{"add","\ue710"},{"save","\ue74e"},{"update","\ue896"},
      {"check","\ue8fb"},{"folder","\ue8b7"},{"refresh","\ue72c"},{"tools","\ue713"}
    };
    private readonly string[] names={"晴空蓝","薄雾紫","青松绿","石墨灰"};
    private readonly string[] palettes={"fluent","violet","sage","graphite"};
    private readonly string[] light={"#2563EB","#7958D6","#258267","#252525"};
    private readonly string[] dark={"#89AAFF","#B7A0FF","#83D6BC","#D0D0D0"};
    private readonly Backend backend;
    private readonly string[] arguments;
    private Dictionary<string,object> cfg=new Dictionary<string,object>{{"scheme","mini_graphite_light"},{"ui_theme","system"},{"radius",10},{"fontsize",14},{"horizontal",false},{"custom_bg","#FFFFFF"},{"custom_fg","#1C2434"},{"custom_hl","#2563EB"}};
    private Grid root,frame,overlay;
    private StackPanel list;
    private ScrollViewer scroll;
    private Border fadeTop,fadeBottom;
    private TextBlock title,status,updateLabel;
    private SettingsButton saveButton,resetButton,updateButton,checkUpdatesButton,systemButton,lightButton,darkButton;
    private Grid footerRegion;
    private Dictionary<string,SettingsButton> nav=new Dictionary<string,SettingsButton>();
    private List<SettingsButton> modes=new List<SettingsButton>();
    private Canvas preview;
    private string appDir="",programDir="",logDir="",backupDir="";
    private SettingsSwitch fuzzySwitch;
    private string page="appearance",rimeDir="",weaselDir="",updateStatus="尚未检查词库更新";
    private string statusText="正在读取设置…";
    private bool ready=false;
    private bool animatePaletteCheck;
    private bool systemDark=ReadSystemDark();
    public bool Busy=false;
    private TaskCompletionSource<bool> modalResult;
    private bool modalClosing;
    private IInputElement focusBeforeModal;
    private Border modalCard;
    private ScaleTransform modalScale;
    private BlurEffect blur=new BlurEffect {Radius=0};
    private string qaFolder;
    private readonly JavaScriptSerializer json=new JavaScriptSerializer();

    public SettingsWindow(string[] args) {
      arguments=args;
      string backendPath=IOPath.GetFullPath(IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","scripts","settings_backend.py"));
      for(int i=0;i<args.Length-1;i++){if(args[i]=="--backend")backendPath=args[i+1];if(args[i]=="--qa")qaFolder=args[i+1];}
      backend=new Backend(backendPath);
      Title="麻绳输入法 · 设置";Width=908;Height=740;MinWidth=760;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterScreen;
      FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=14;UseLayoutRounding=true;
      TextOptions.SetTextRenderingMode(this,TextRenderingMode.Grayscale);
      TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
      InitBrushes();BuildShell();
      SystemEvents.UserPreferenceChanged+=SystemPreferenceChanged;
      Closed+=delegate{SystemEvents.UserPreferenceChanged-=SystemPreferenceChanged;};
      Loaded+=async delegate {await Initialize();};
      Closing+=delegate(object sender,CancelEventArgs e){if(Busy){e.Cancel=true;SetStatus("操作仍在进行，完成后可以关闭窗口。");}};
      PreviewKeyDown+=async delegate(object sender,KeyEventArgs e){
        if(e.Key==Key.Escape && modalResult!=null){e.Handled=true;await Dismiss(false);}
        if(modalResult!=null)return;
        if(e.Key==Key.S && Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;await Save();}
      };
    }
    private string ThemeMode {get{return Convert.ToString(cfg["ui_theme"]);}}
    private bool IsDark {get{return ThemeMode=="system"?systemDark:ThemeMode=="dark";}}
    private static bool ReadSystemDark(){try{using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))return key!=null && Convert.ToInt32(key.GetValue("AppsUseLightTheme",1))==0;}catch{return false;}}
    private void SystemPreferenceChanged(object sender,UserPreferenceChangedEventArgs e){if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(delegate{bool value=ReadSystemDark();if(value==systemDark)return;systemDark=value;if(ThemeMode=="system"){double offset=scroll.VerticalOffset;ApplyTheme(true);BuildPage();Dispatcher.BeginInvoke(new Action(delegate{scroll.ScrollToVerticalOffset(offset);}));}}));}
    public Color ColorOf(string key) {
      var values=IsDark?new Dictionary<string,string>{{"bg","#171717"},{"rail","#202020"},{"surface","#242424"},{"soft","#2D2D2D"},{"text","#EFEFEF"},{"muted","#BDBDBD"},{"line","#424242"},{"outline","#A8A8A8"},{"primary","#E6E6E6"},{"onPrimary","#171717"},{"selected","#454545"},{"track","#2D2D2D"},{"dialog","#303030"},{"scroll","#777777"},{"scrollHover","#A0A0A0"}}:
        new Dictionary<string,string>{{"bg","#F9F9F9"},{"rail","#EEEEEE"},{"surface","#F3F3F3"},{"soft","#EFEFEF"},{"text","#202020"},{"muted","#555555"},{"line","#E0E0E0"},{"outline","#5C5C5C"},{"primary","#202020"},{"onPrimary","#FFFFFF"},{"selected","#D5D5D5"},{"track","#F0F0F0"},{"dialog","#E8E8E8"},{"scroll","#B0B0B0"},{"scrollHover","#858585"}};
      values["switchTrack"]=IsDark?"#3A3A3A":"#E1E1E1";values["switchOutline"]=IsDark?"#A0A0A0":"#797979";
      return Parse(values[key]);
    }
    public static Color Parse(string color){return (Color)ColorConverter.ConvertFromString(color);}
    private void InitBrushes(){foreach(string key in new[]{"bg","rail","surface","soft","text","muted","line","outline","primary","onPrimary","selected","track","dialog","switchTrack","switchOutline"})brushes[key]=new SolidColorBrush(ColorOf(key));}
    public SolidColorBrush BrushOf(string key){return brushes[key];}
    public TextBlock Text(string value,double size,string color) {return new TextBlock {Text=value,FontSize=size,Foreground=BrushOf(color),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};}
    public TextBlock Glyph(string key,double size,Brush color,Thickness margin){return new TextBlock {Text=icons[key],FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=size,Foreground=color,Margin=margin,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};}
    private SettingsButton Btn(string text,Action action,double width,string kind,string icon,bool selected,double height){return new SettingsButton(this,text,action,width,kind,icon,selected,height);}
    private void BuildShell() {
      root=new Grid {Background=BrushOf("bg")};Content=root;
      frame=new Grid();root.Children.Add(frame);frame.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(80)});frame.ColumnDefinitions.Add(new ColumnDefinition());
      var rail=new Grid {Background=BrushOf("rail")};frame.Children.Add(rail);
      var navigation=new StackPanel {Margin=new Thickness(0,10,0,0)};rail.Children.Add(navigation);
      foreach(var data in new[]{new[]{"appearance","外观"},new[]{"maintenance","输入"},new[]{"guide","指南"}}){
        string id=data[0];var container=new StackPanel {Margin=new Thickness(4,2,4,2)};
        var button=Btn(null,delegate{Navigate(id);},72,"nav",null,page==id,66);button.Padding=new Thickness(0);
        var contents=new StackPanel();var iconBox=new Border {Width=56,Height=32,CornerRadius=new CornerRadius(16),Margin=new Thickness(0,3,0,4),Background=page==id?BrushOf("selected"):Brushes.Transparent};
        iconBox.Child=new NavigationIcon(id,new SolidColorBrush(Color.FromRgb(0x66,0x66,0x66)));contents.Children.Add(iconBox);contents.Children.Add(Text(data[1],12,"text"));
        ((TextBlock)contents.Children[1]).TextAlignment=TextAlignment.Center;
        button.Content=contents;container.Children.Add(button);navigation.Children.Add(container);nav[id]=button;
      }
      var mark=Text("Mason",10,"muted");mark.HorizontalAlignment=HorizontalAlignment.Center;mark.VerticalAlignment=VerticalAlignment.Bottom;mark.Margin=new Thickness(0,0,0,20);rail.Children.Add(mark);
      var main=new Grid();Grid.SetColumn(main,1);frame.Children.Add(main);
      main.RowDefinitions.Add(new RowDefinition {Height=new GridLength(TitleAreaHeight)});main.RowDefinitions.Add(new RowDefinition());main.RowDefinitions.Add(new RowDefinition {Height=new GridLength(ActionAreaHeight)});
      var headerRegion=new Grid {Background=Brushes.Transparent};Panel.SetZIndex(headerRegion,1);main.Children.Add(headerRegion);
      var head=new Grid {Margin=new Thickness(16,0,16,0),Background=Brushes.Transparent};headerRegion.Children.Add(head);
      title=Text("外观",24,"text");title.HorizontalAlignment=HorizontalAlignment.Left;head.Children.Add(title);
      scroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(16,0,16,0)};
      InstallScrollStyle();
      scroll.Loaded+=delegate{UpdateEdgeFade();};scroll.ScrollChanged+=delegate{UpdateEdgeFade();};scroll.SizeChanged+=delegate{UpdateEdgeFade();};
      var scrollingArea=new Grid {ClipToBounds=true};Grid.SetRowSpan(scrollingArea,3);main.Children.Add(scrollingArea);scrollingArea.Children.Add(scroll);
      fadeTop=new Border {Height=ListFadeExtent,Background=BrushOf("bg"),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,0,10,0),IsHitTestVisible=false,OpacityMask=new LinearGradientBrush(Colors.White,Colors.Transparent,new Point(0,0),new Point(0,1))};
      fadeBottom=new Border {Height=ListFadeExtent,Background=BrushOf("bg"),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,10,0),IsHitTestVisible=false,OpacityMask=new LinearGradientBrush(Colors.Transparent,Colors.White,new Point(0,0),new Point(0,1))};
      scrollingArea.Children.Add(fadeTop);scrollingArea.Children.Add(fadeBottom);list=new StackPanel {Margin=new Thickness(0,TitleAreaHeight+ListEdgePadding,0,ActionAreaHeight+ListEdgePadding)};scroll.Content=list;
      footerRegion=new Grid {Background=Brushes.Transparent};Grid.SetRow(footerRegion,2);Panel.SetZIndex(footerRegion,1);main.Children.Add(footerRegion);
      var foot=new Grid {Margin=new Thickness(16,8,16,16),Background=Brushes.Transparent};footerRegion.Children.Add(foot);
      foot.ColumnDefinitions.Add(new ColumnDefinition());foot.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
      status=Text(statusText,12,"muted");status.Margin=new Thickness(0,0,20,0);status.HorizontalAlignment=HorizontalAlignment.Left;foot.Children.Add(status);
      var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(actions,1);foot.Children.Add(actions);
      resetButton=Btn("恢复默认",Reset,100,"text",null,false,48);resetButton.Margin=new Thickness(0,0,12,0);actions.Children.Add(resetButton);
      saveButton=Btn("保存并应用",async delegate{await Save();},136,"primary",null,false,48);
      saveButton.Effect=new DropShadowEffect {Color=Colors.Black,BlurRadius=5,Opacity=.13,ShadowDepth=2};actions.Children.Add(saveButton);
      overlay=new Grid {Visibility=Visibility.Collapsed};root.Children.Add(overlay);
      KeyboardNavigation.SetTabNavigation(overlay,KeyboardNavigationMode.Cycle);
      KeyboardNavigation.SetControlTabNavigation(overlay,KeyboardNavigationMode.Cycle);
      FocusManager.SetIsFocusScope(overlay,true);
      BuildPage();
    }
    private void InstallScrollStyle(){
      UpdateScrollColors();
      scroll.Resources["RimeScrollTrackMargin"]=new Thickness(0,TitleAreaHeight+4,0,ActionAreaHeight+4);
      scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)]=(Style)XamlReader.Parse(@"
        <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:Mason;assembly=Mason.Settings' TargetType='{x:Type ScrollBar}'>
          <Setter Property='Width' Value='10'/><Setter Property='Background' Value='Transparent'/><Setter Property='Focusable' Value='False'/>
          <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
            <Grid Width='10' Margin='{DynamicResource RimeScrollTrackMargin}'>
              <Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
                <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb MinHeight='40' Cursor='Arrow'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>
                  <local:SmoothSurface x:Name='Shape' Fill='{DynamicResource RimeScrollThumb}' Radius='4' Margin='2,0'/>
                  <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Shape' Property='Fill' Value='{DynamicResource RimeScrollHover}'/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
              </Track>
            </Grid>
          </ControlTemplate></Setter.Value></Setter>
        </Style>");
    }
    private void UpdateScrollColors(){scroll.Resources["RimeScrollThumb"]=new SolidColorBrush(ColorOf("scroll"));scroll.Resources["RimeScrollHover"]=new SolidColorBrush(ColorOf("scrollHover"));}
    private void UpdateEdgeFade(){
      if(fadeTop==null || fadeBottom==null)return;
      // Keep the approved edge masks active, including the list's start and end.
      fadeTop.Opacity=1;
      fadeBottom.Opacity=1;
    }
    private void BuildPage() {
      list.Children.Clear();PruneButtons();preview=null;updateLabel=null;updateButton=null;fuzzySwitch=null;modes.Clear();
      title.Text=page=="appearance"?"外观":page=="maintenance"?"输入":"使用指南";
      footerRegion.Visibility=page=="guide"?Visibility.Collapsed:Visibility.Visible;
      scroll.Resources["RimeScrollTrackMargin"]=new Thickness(0,TitleAreaHeight+4,0,page=="guide"?4:ActionAreaHeight+4);
      checkUpdatesButton=null;
      if(page=="appearance")BuildAppearance();else if(page=="maintenance")BuildMaintenance();else BuildGuide();
      animatePaletteCheck=false;
      foreach(var item in nav){item.Value.Selected=false;item.Value.ApplyColors(false);var stack=item.Value.Content as StackPanel;var pill=stack.Children[0] as Border;pill.Background=item.Key==page?BrushOf("selected"):Brushes.Transparent;}
    }
    private void PruneButtons(){Buttons.RemoveAll(button=>!button.IsDescendantOf(root));}
    private DockPanel Section(string caption,double top) {
      var row=new DockPanel {Margin=new Thickness(0,top,0,8),Height=26};
      var label=Text(caption,18,"text");label.FontWeight=FontWeights.Bold;row.Children.Add(label);list.Children.Add(row);return row;
    }
    private string EffectiveScheme(){string scheme=Convert.ToString(cfg["scheme"]);if(ThemeMode=="system")foreach(string name in palettes)if(scheme.StartsWith("mini_"+name+"_"))return "mini_"+name+"_"+(IsDark?"dark":"light");return scheme;}
    private string[] CandidateColors(string scheme) {
      if(scheme=="__custom__"){string hl=Convert.ToString(cfg["custom_hl"]);return new[]{Convert.ToString(cfg["custom_bg"]),Convert.ToString(cfg["custom_fg"]),hl,Luminance(hl)>.179?"#111827":"#FFFFFF"};}
      for(int i=0;i<palettes.Length;i++){if(scheme=="mini_"+palettes[i]+"_light")return new[]{"#FFFFFF","#243047",light[i],"#FFFFFF"};if(scheme=="mini_"+palettes[i]+"_dark")return new[]{"#333333","#EDF1FA",dark[i],"#111827"};}
      switch(scheme){
        case "aqua":return new[]{"#EEEEEC","#000000","#0A3AFA","#FFFFFF"};
        case "luna":case "ink":return new[]{"#FFFFFF","#000000","#000000","#FFFFFF"};
        case "google":return new[]{"#FFFFFF","#000000","#3975CE","#FFFFFF"};
        case "azure":return new[]{"#014E8B","#EEF8FF","#015EA9","#FFFF7F"};
        case "dark_temple":return new[]{"#222222","#E6E3D8","#333333","#DAF692"};
      }
      return new[]{"#FFFFFF","#243047","#252525","#FFFFFF"};
    }
    private static double Luminance(string hex){Color c=Parse(hex);Func<byte,double> f=delegate(byte n){double v=n/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);};return .2126*f(c.R)+.7152*f(c.G)+.0722*f(c.B);}
    private void BuildAppearance() {
      Section("预览",0);
      var previewCard=new Border {CornerRadius=new CornerRadius(16),Background=BrushOf("soft"),Padding=new Thickness(14,12,14,12)};
      var previewContent=new StackPanel();previewCard.Child=previewContent;list.Children.Add(previewCard);
      var themes=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,10)};previewContent.Children.Add(themes);
      systemButton=Btn("跟随系统",delegate{Theme("system");},114,"secondary","refresh",ThemeMode=="system",40);themes.Children.Add(systemButton);
      lightButton=Btn("浅色",delegate{Theme("light");},88,"secondary","sun",ThemeMode=="light",40);lightButton.Margin=new Thickness(8,0,0,0);themes.Children.Add(lightButton);
      darkButton=Btn("深色",delegate{Theme("dark");},88,"secondary","moon",ThemeMode=="dark",40);darkButton.Margin=new Thickness(8,0,0,0);themes.Children.Add(darkButton);
      preview=new Canvas {Height=136,Background=Brushes.Transparent};preview.SizeChanged+=delegate{DrawPreview();};previewContent.Children.Add(preview);DrawPreview();
      Section("排列",18);
      var modeRow=new StackPanel {Orientation=Orientation.Horizontal};list.Children.Add(modeRow);
      for(int i=0;i<2;i++){bool horizontal=i==1;var mode=Btn(horizontal?"横排":"竖排",delegate{SetOrientation(horizontal);},120,"secondary",null,Convert.ToBoolean(cfg["horizontal"])==horizontal,44);mode.Margin=new Thickness(0,0,12,0);modes.Add(mode);modeRow.Children.Add(mode);}
      Section("配色",18);
      var colorsRow=new WrapPanel {Orientation=Orientation.Horizontal};list.Children.Add(colorsRow);
      for(int index=0;index<palettes.Length;index++) {
        int i=index;string scheme="mini_"+palettes[i]+"_"+(IsDark?"dark":"light");bool chosen=EffectiveScheme()==scheme;
        var column=new StackPanel {Margin=new Thickness(0,0,14,0)};
        var button=Btn(null,delegate{SelectPalette(i);},72,"palette",null,chosen,72);button.Padding=new Thickness(0);AutomationProperties.SetName(button,names[i]);
        string[] colors=CandidateColors(scheme);var canvas=new Canvas {Width=58,Height=58};
        // Draw curved sectors directly. Geometry clipping itself can leave a jagged edge in WPF.
        canvas.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M29,0 A29,29 0 0 0 29,58 L29,0 Z"),Fill=new SolidColorBrush(Parse(colors[2]))});
        canvas.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M29,0 A29,29 0 0 1 58,29 L29,29 Z"),Fill=new SolidColorBrush(Parse(i==3?"#606060":colors[1]))});
        canvas.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M58,29 A29,29 0 0 1 29,58 L29,29 Z"),Fill=new SolidColorBrush(Parse(i==3?"#999999":colors[0]))});
        if(chosen){var badge=new Grid {Width=24,Height=24,RenderTransformOrigin=new Point(.5,.5)};badge.Children.Add(new Ellipse {Fill=new SolidColorBrush(Parse("#D0D0D0"))});badge.Children.Add(Glyph("check",14,new SolidColorBrush(Parse("#222222")),new Thickness()));Canvas.SetLeft(badge,17);Canvas.SetTop(badge,17);canvas.Children.Add(badge);
          var scale=new ScaleTransform(1,1);badge.RenderTransform=scale;if(ready && animatePaletteCheck){scale.BeginAnimation(ScaleTransform.ScaleXProperty,Motion.Number(0,1,Motion.CheckMs,Motion.OutBack));scale.BeginAnimation(ScaleTransform.ScaleYProperty,Motion.Number(0,1,Motion.CheckMs,Motion.OutBack));}}
        button.Content=canvas;column.Children.Add(button);var label=Text(names[i],12,"muted");label.HorizontalAlignment=HorizontalAlignment.Center;label.Margin=new Thickness(0,7,0,0);column.Children.Add(label);colorsRow.Children.Add(column);
      }
      var customColumn=new StackPanel();var custom=Btn(null,async delegate{await CustomColors();},64,"text",null,false,72);custom.Padding=new Thickness(0);
      var plus=new Grid {Width=64,Height=64};plus.Children.Add(new Ellipse {Fill=BrushOf("selected")});plus.Children.Add(Glyph("add",26,BrushOf("text"),new Thickness()));custom.Content=plus;AutomationProperties.SetName(custom,"自定义配色");customColumn.Children.Add(custom);
      var customLabel=Text("自定义",12,"muted");customLabel.Margin=new Thickness(0,7,0,0);customLabel.HorizontalAlignment=HorizontalAlignment.Center;customColumn.Children.Add(customLabel);colorsRow.Children.Add(customColumn);
      AddSlider("圆角","radius",0,20,"px",18);AddSlider("字号","fontsize",11,24,"pt",14);
    }
    private void AddSlider(string name,string key,int low,int high,string unit,double top){
      Section(name,top);
      var controls=new Grid();controls.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});controls.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
      var slider=new SettingsSlider(this,low,high,Convert.ToDouble(cfg[key]),name);controls.Children.Add(slider);
      var value=Text(Convert.ToString(cfg[key])+" "+unit,14,"text");value.Margin=new Thickness(16,0,0,0);value.HorizontalAlignment=HorizontalAlignment.Left;Grid.SetColumn(value,1);controls.Children.Add(value);list.Children.Add(controls);
      slider.ValueChanged+=delegate{if(Busy)return;cfg[key]=(int)Math.Round(slider.Value);value.Text=Convert.ToString(cfg[key])+" "+unit;Changed();DrawPreview();};
    }
    private void DrawPreview(){
      if(preview==null)return;preview.Children.Clear();double width=Math.Max(300,preview.ActualWidth);
      string[] colors=CandidateColors(EffectiveScheme());double fs=Convert.ToDouble(cfg["fontsize"])*4/3,corner=Convert.ToDouble(cfg["radius"]);
      string muted=Luminance(colors[0])<.3?"#A3B0C5":"#66758C",line=Luminance(colors[0])<.3?"#555555":"#DFE5EF";
      var popup=new Border {CornerRadius=new CornerRadius(corner),Background=new SolidColorBrush(Parse(colors[0])),BorderBrush=new SolidColorBrush(Parse(line)),BorderThickness=new Thickness(1),Padding=new Thickness(6,8,6,8),Effect=new DropShadowEffect {BlurRadius=5,ShadowDepth=2,Opacity=.1,Color=Colors.Black}};
      var stack=new StackPanel();popup.Child=stack;
      var pre=new TextBlock {Text="ni hao",FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=Math.Max(10,Convert.ToDouble(cfg["fontsize"])-2)*4/3,Foreground=new SolidColorBrush(Parse(muted)),Margin=new Thickness(6,0,6,8)};stack.Children.Add(pre);
      var candidates=new StackPanel {Orientation=Convert.ToBoolean(cfg["horizontal"])?Orientation.Horizontal:Orientation.Vertical};stack.Children.Add(candidates);
      string[] words={"你好","您好","你好呀"};double boxWidth=Math.Ceiling(Measure(words[2],fs)+Measure("3",Math.Max(10,Convert.ToDouble(cfg["fontsize"])-3)*4/3)+16);
      for(int i=0;i<words.Length;i++){
        var row=new StackPanel {Orientation=Orientation.Horizontal};
        row.Children.Add(new TextBlock {Text=(i+1).ToString(),FontSize=Math.Max(10,Convert.ToDouble(cfg["fontsize"])-3)*4/3,Foreground=new SolidColorBrush(Parse(i==0?colors[3]:muted)),Margin=new Thickness(0,0,2,0),VerticalAlignment=VerticalAlignment.Center});
        row.Children.Add(new TextBlock {Text=words[i],FontSize=fs,FontFamily=new FontFamily("Microsoft YaHei UI"),Foreground=new SolidColorBrush(Parse(i==0?colors[3]:colors[1]))});
        var selection=new Border {CornerRadius=new CornerRadius(Math.Max(0,corner-3)),Padding=new Thickness(6,4,8,4),Margin=new Thickness(0,0,0,3),Background=i==0?new SolidColorBrush(Parse(colors[2])):Brushes.Transparent,Child=row};
        if(!Convert.ToBoolean(cfg["horizontal"]))selection.MinWidth=boxWidth;
        else selection.Margin=new Thickness(0,0,6,0);
        candidates.Children.Add(selection);
      }
      Canvas.SetLeft(popup,0);Canvas.SetTop(popup,0);preview.Children.Add(popup);
      popup.Measure(new Size(width,Double.PositiveInfinity));preview.Height=Math.Max(78,popup.DesiredSize.Height+4);
    }
    private double Measure(string text,double size){return new FormattedText(text,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;}
    private Border Card(string heading){var card=new Border {CornerRadius=new CornerRadius(20),Background=BrushOf("surface"),BorderBrush=BrushOf("line"),BorderThickness=new Thickness(1),Padding=new Thickness(18),Margin=new Thickness(0,0,0,16)};var content=new StackPanel();card.Child=content;var titleBlock=Text(heading,16,"text");titleBlock.Margin=new Thickness(0,0,0,16);content.Children.Add(titleBlock);list.Children.Add(card);return card;}
    private static readonly string[][] FuzzyOptions={
      new[]{"nl","n ↔ l","声母"},new[]{"lr","l ↔ r","声母"},new[]{"fh","f ↔ h","声母"},new[]{"gk","g ↔ k","声母"},
      new[]{"z_zh","z ↔ zh","声母"},new[]{"c_ch","c ↔ ch","声母"},new[]{"s_sh","s ↔ sh","声母"},
      new[]{"an_ang","an ↔ ang","韵母"},new[]{"en_eng","en ↔ eng","韵母"},new[]{"in_ing","in ↔ ing","韵母"},
      new[]{"ian_iang","ian ↔ iang","韵母"},new[]{"uan_uang","uan ↔ uang","韵母"}
    };
    private bool Fuzzy(string key){object value;return cfg.TryGetValue(key,out value) && value is bool && (bool)value;}
    private void BuildFuzzy(){
      var heading=new DockPanel {Margin=new Thickness(0,0,0,8)};
      var title=Text("模糊音",20,"text");title.FontWeight=FontWeights.Bold;
      bool enabled=Fuzzy("fuzzy_enabled");fuzzySwitch=new SettingsSwitch(this,enabled);
      DockPanel.SetDock(fuzzySwitch,Dock.Right);heading.Children.Add(fuzzySwitch);heading.Children.Add(title);list.Children.Add(heading);
      var content=new StackPanel();list.Children.Add(new Border {Background=BrushOf("surface"),BorderBrush=BrushOf("line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(20),Padding=new Thickness(18),Margin=new Thickness(0,0,0,16),Child=content});
      var fuzzyChoices=new List<SettingsButton>();
      RoutedEventHandler changed=delegate{if(Busy)return;cfg["fuzzy_enabled"]=fuzzySwitch.IsChecked==true;foreach(var choice in fuzzyChoices)choice.IsEnabled=fuzzySwitch.IsChecked==true;Changed();};
      fuzzySwitch.Checked+=changed;fuzzySwitch.Unchecked+=changed;
      foreach(string type in new[]{"声母","韵母"}){
        var caption=Text(type,12,"muted");caption.Margin=new Thickness(0,type=="韵母"?8:0,0,10);content.Children.Add(caption);var choices=new WrapPanel();content.Children.Add(choices);
        foreach(var option in FuzzyOptions.Where(x=>x[2]==type)){
          string key="fuzzy_"+option[0];SettingsButton choice=null;
          choice=Btn(option[1],delegate{if(Busy || !Fuzzy("fuzzy_enabled"))return;cfg[key]=!Fuzzy(key);choice.SetSelected(Fuzzy(key));Changed();},type=="声母"?110:142,"secondary",null,Fuzzy(key),40);
          choice.Margin=new Thickness(0,0,10,10);choice.IsEnabled=enabled;fuzzyChoices.Add(choice);AutomationProperties.SetName(choice,"模糊音 "+option[1]);choices.Children.Add(choice);
        }
      }
    }
    private void BuildMaintenance(){
      BuildFuzzy();
      Section("词库",6);
      var location=Card("词库");var content=(StackPanel)location.Child;content.Children.Clear();
      updateLabel=Text(updateStatus,14,"muted");updateLabel.Margin=new Thickness(0,0,0,14);content.Children.Add(updateLabel);
      updateButton=Btn("更新词库",async delegate{await UpdateDictionary();},142,"primary",null,false,44);updateButton.HorizontalAlignment=HorizontalAlignment.Left;updateButton.IsEnabled=!Busy;updateButton.Margin=new Thickness(0,0,0,20);content.Children.Add(updateButton);
      content.Children.Add(new Border {Height=1,Background=BrushOf("line"),Margin=new Thickness(0,0,0,16)});
      content.Children.Add(Text("词库位置",14,"text"));var path=Text(rimeDir,12,"muted");path.Margin=new Thickness(0,8,0,16);content.Children.Add(path);
      var folders=new WrapPanel();content.Children.Add(folders);var open=Btn("用户数据",delegate{OpenFolder(rimeDir);},146,"secondary",null,false,44);open.Margin=new Thickness(0,0,12,0);folders.Children.Add(open);folders.Children.Add(Btn("迁移词库",async delegate{await Migrate();},120,"secondary",null,false,44));
    }
    private void BuildGuide(){
      var card=Card("常用快捷键");var content=(StackPanel)card.Child;
      foreach(var item in new[]{new[]{"Win + 空格","切换到麻绳输入法"},new[]{"Shift","切换中文 / 英文"},new[]{"Ctrl + T","查询云候选"},new[]{";","选择第 2 个候选"}})GuideRow(content,item[0],item[1]);
      card=Card("实用输入");content=(StackPanel)card.Child;
      foreach(var item in new[]{new[]{"rj / sj / xq","日期 / 时间 / 星期"},new[]{"nl","农历"},new[]{"v","符号与 Emoji"}})GuideRow(content,item[0],item[1]);
      Section("关于",6);card=Card("关于");content=(StackPanel)card.Child;content.Children.Clear();
      var product=ReadProjectInfo();var productTitle=Text("麻绳输入法 · Mason "+Convert.ToString(product["version"]),16,"text");productTitle.FontWeight=FontWeights.SemiBold;productTitle.Margin=new Thickness(0,0,0,20);content.Children.Add(productTitle);
      GuideRow(content,"开源协议",Convert.ToString(product["license"]));
      string github=Convert.ToString(product["github_url"]);GuideLinkRow(content,"GitHub 地址",github);
      content.Children.Add(Text("鸣谢",14,"text"));var projects=product["thanks"] as object[];string names=projects==null?"Rime / 小狼毫、librime、librime-lua、白霜拼音、雾凇拼音、librime-cloud、OpenCC、FlClash":String.Join("、",projects.Select(Convert.ToString));
      var thanks=Text(names,13,"muted");thanks.Margin=new Thickness(0,6,0,12);content.Children.Add(thanks);
      var licenses=Text("第三方组件保留各自的开源协议和版权说明。",12,"muted");licenses.Margin=new Thickness(0,0,0,18);content.Children.Add(licenses);
      checkUpdatesButton=Btn("检查更新",async delegate{await CheckUpdates();},136,"primary",null,false,44);checkUpdatesButton.HorizontalAlignment=HorizontalAlignment.Right;checkUpdatesButton.IsEnabled=!Busy;content.Children.Add(checkUpdatesButton);
    }
    private Dictionary<string,object> ReadProjectInfo(){
      string file=IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory,"project-info.json");
      return json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file,Encoding.UTF8));
    }
    private async Task CheckUpdates(){
      if(Busy)return;Busy=true;if(checkUpdatesButton!=null)checkUpdatesButton.IsEnabled=false;string message;
      try{var result=await backend.Run("check_update",null,null);message=Convert.ToString(result["message"]);}
      catch(Exception error){message=error.Message;}
      finally{Busy=false;if(checkUpdatesButton!=null)checkUpdatesButton.IsEnabled=true;}
      await Info("检查更新",message);
    }
    private void GuideRow(StackPanel content,string code,string meaning){var row=new DockPanel {Margin=new Thickness(0,0,0,20)};var key=Text(code,14,"text");key.Width=154;row.Children.Add(key);row.Children.Add(Text(meaning,14,"muted"));content.Children.Add(row);}
    private void GuideLinkRow(StackPanel content,string heading,string address){
      Uri uri;
      if(!Uri.TryCreate(address,UriKind.Absolute,out uri) || uri.Scheme!=Uri.UriSchemeHttps || !String.Equals(uri.Host,"github.com",StringComparison.OrdinalIgnoreCase)){GuideRow(content,heading,"待填写");return;}
      var row=new DockPanel {Margin=new Thickness(0,0,0,20)};var label=Text(heading,14,"text");label.Width=154;row.Children.Add(label);
      var value=Text("",14,"muted");var link=new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(address)) {NavigateUri=uri,Foreground=BrushOf("muted"),Cursor=Cursors.Hand,ToolTip="打开 Mason 的 GitHub 项目"};
      link.RequestNavigate+=delegate(object sender,System.Windows.Navigation.RequestNavigateEventArgs e){e.Handled=true;try{Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true});}catch(Exception error){ShowInfo(error.Message);}};
      AutomationProperties.SetName(link,"打开 Mason 的 GitHub 项目");value.Inlines.Add(link);row.Children.Add(value);content.Children.Add(row);
    }
    private void Navigate(string target){page=target;BuildPage();scroll.ScrollToTop();}
    public async void ReceiveLaunch(string command){
      for(int attempt=0;!ready && attempt<40;attempt++)await Task.Delay(100);
      if(!ready)return;if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Show();Activate();
      if(command=="update" && !Busy){Navigate("maintenance");await UpdateDictionary();}
      if(command=="check_updates" && !Busy){Navigate("guide");await CheckUpdates();}
    }
    private void SetOrientation(bool value){if(Busy)return;cfg["horizontal"]=value;for(int i=0;i<modes.Count;i++)modes[i].SetSelected(value==(i==1),Motion.SegmentMs,Motion.OutCubic);Changed();DrawPreview();}
    private void SelectPalette(int i){if(Busy)return;string target="mini_"+palettes[i]+"_"+(IsDark?"dark":"light");if(EffectiveScheme()==target)return;cfg["scheme"]=target;animatePaletteCheck=true;double offset=scroll.VerticalOffset;Changed();BuildPage();Dispatcher.BeginInvoke(new Action(delegate{scroll.ScrollToVerticalOffset(offset);}));}
    private void Theme(string value){if(Busy || ThemeMode==value)return;cfg["ui_theme"]=value;systemDark=ReadSystemDark();foreach(string name in palettes)if(Convert.ToString(cfg["scheme"]).StartsWith("mini_"+name+"_")){cfg["scheme"]="mini_"+name+"_"+(IsDark?"dark":"light");break;}ApplyTheme(true);double offset=scroll.VerticalOffset;Changed();BuildPage();Dispatcher.BeginInvoke(new Action(delegate{scroll.ScrollToVerticalOffset(offset);}));}
    private void ApplyTheme(bool animate){foreach(var pair in brushes)if(animate)Motion.Color(pair.Value,ColorOf(pair.Key),Motion.ThemeMs,null);else pair.Value.Color=ColorOf(pair.Key);UpdateScrollColors();if(systemButton!=null)systemButton.Selected=ThemeMode=="system";if(lightButton!=null)lightButton.Selected=ThemeMode=="light";if(darkButton!=null)darkButton.Selected=ThemeMode=="dark";foreach(var button in Buttons)button.ApplyColors(animate,Motion.ThemeMs,null);SetNativeTheme();}
    private void Changed(){SetStatus("");}
    private void Reset(){if(Busy)return;if(page=="maintenance"){cfg["fuzzy_enabled"]=false;foreach(var option in FuzzyOptions)cfg["fuzzy_"+option[0]]=false;}else{cfg["scheme"]="mini_graphite_"+(IsDark?"dark":"light");cfg["radius"]=10;cfg["fontsize"]=14;cfg["horizontal"]=false;}Changed();BuildPage();scroll.ScrollToTop();}
    private void SetStatus(string message){statusText=message;status.Text=message;}
    private async Task Initialize(){
      string failure=null;
      try{var result=await backend.Run("state",null,null);if(!Convert.ToBoolean(result["ok"]))throw new InvalidOperationException(Convert.ToString(result["message"]));LoadData((Dictionary<string,object>)result["data"],true);SetStatus(String.IsNullOrEmpty(weaselDir)?"尚未安装 · 可以预览外观":"");ready=true;}
      catch(Exception error){failure=error.Message;SetStatus("设置读取未完成");}
      if(failure!=null){if(qaFolder!=null){WriteQaError(failure);Close();return;}await Info("提示",failure);}
      if(qaFolder!=null){await QualityCapture();return;}
      if(arguments.Contains("--update-dictionary")){Navigate("maintenance");await UpdateDictionary();}
      if(arguments.Contains("--check-updates")){Navigate("guide");await CheckUpdates();}
    }
    private void LoadData(Dictionary<string,object> data,bool loadCfg){appDir=data.ContainsKey("app_dir")?Convert.ToString(data["app_dir"]):"";programDir=data.ContainsKey("program_dir")?Convert.ToString(data["program_dir"]):Convert.ToString(data["weasel_dir"]);logDir=data.ContainsKey("log_dir")?Convert.ToString(data["log_dir"]):"";backupDir=data.ContainsKey("backup_dir")?Convert.ToString(data["backup_dir"]):"";rimeDir=Convert.ToString(data["rime_dir"]);weaselDir=Convert.ToString(data["weasel_dir"]);updateStatus=Convert.ToString(data["update_status"]);if(loadCfg)cfg=(Dictionary<string,object>)data["cfg"];ApplyTheme(false);BuildPage();}
    private bool Installed(){return !String.IsNullOrEmpty(weaselDir) && Directory.Exists(rimeDir);}
    private async Task<bool> RequireInstalled(){if(Busy)return false;if(Installed())return true;await Info("提示","请先运行 Mason.Install.exe 安装麻绳输入法。\n当前可预览和调整外观。");return false;}
    private async Task Save(){if(!await RequireInstalled())return;var snapshot=new Dictionary<string,object>(cfg);bool ok=await RunJob("save",new Dictionary<string,object>{{"cfg",snapshot}},"正在保存并应用…",false);if(ok)SetStatus("");}
    private async Task UpdateDictionary(){if(!await RequireInstalled())return;await RunJob("update",null,"正在检查最新词库…",true);}
    private async Task<bool> RunJob(string action,object payload,string pending,bool notify){
      if(Busy)return false;if(!Installed()){await Info("提示","请先安装麻绳输入法。");return false;}
      Busy=true;SetStatus(pending);saveButton.IsEnabled=false;resetButton.IsEnabled=false;if(updateButton!=null)updateButton.IsEnabled=false;if(fuzzySwitch!=null)fuzzySwitch.IsEnabled=false;
      bool ok=false;string message=null;
      try{var result=await backend.Run(action,payload,delegate(string progressMessage){Dispatcher.BeginInvoke(new Action(delegate{SetStatus(progressMessage);if(updateLabel!=null)updateLabel.Text=progressMessage;}));});
        ok=Convert.ToBoolean(result["ok"]);message=Convert.ToString(result["message"]);Busy=false;
        if(ok && result.ContainsKey("data"))LoadData((Dictionary<string,object>)result["data"],false);
        SetStatus(message.Replace("\n"," · "));
      }catch(Exception error){Busy=false;SetStatus("操作未完成");message=error.Message;}
      finally{Busy=false;saveButton.IsEnabled=true;resetButton.IsEnabled=true;if(updateButton!=null)updateButton.IsEnabled=true;if(fuzzySwitch!=null)fuzzySwitch.IsEnabled=true;}
      if(action=="update" && !ok && updateLabel!=null)updateLabel.Text=message;
      if(!ok || notify)await Info(action=="update"?"词库更新":"提示",message);
      return ok;
    }
    private void OpenFolder(string path){if(Busy)return;if(Directory.Exists(path)){try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception error){ShowInfo(error.Message);}}else ShowInfo("目录尚不存在，请先安装输入法。");}
    private async void ShowInfo(string text){await Info("提示",text);}
    private async Task Migrate(){if(!await RequireInstalled())return;using(var picker=new System.Windows.Forms.FolderBrowserDialog {Description="选择词库的新位置（将创建 RimeData 文件夹）",ShowNewFolderButton=true}){
      if(picker.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;string target=IOPath.Combine(picker.SelectedPath,"RimeData");
      if(!await Confirm("迁移词库","将词库复制到：\n"+target+"\n\n完成后切换到新位置，原目录完整保留。"))return;
      await RunJob("migrate",new Dictionary<string,object>{{"target",target}},"正在复制词库并切换位置…",true);
    }}
    private void RememberCustomColors(List<TextBox> fields){
      if(fields.Count!=3 || fields.Any(field=>!CustomColorMemory.IsHex(field.Text)) || String.IsNullOrWhiteSpace(rimeDir))return;
      try{CustomColorMemory.Save(rimeDir,fields.Select(field=>field.Text).ToArray());}catch(IOException){SetStatus("暂时无法记录颜色，请稍后再试。");}catch(UnauthorizedAccessException){SetStatus("暂时无法记录颜色，请稍后再试。");}
    }
    private async Task CustomColors(){if(Busy)return;string[] current=CustomColorMemory.Load(rimeDir,CandidateColors(EffectiveScheme()));string[] keys=CustomColorMemory.Keys;string[] labels={"候选窗底色","文字颜色","选中高亮"};var content=new StackPanel();var fields=new List<TextBox>();
      for(int i=0;i<3;i++){var row=new DockPanel {Margin=new Thickness(0,0,0,14)};var label=Text(labels[i],14,"text");label.Width=104;row.Children.Add(label);var chip=new Border {Width=30,Height=30,CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Parse("#"+current[i])),BorderBrush=BrushOf("line"),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,12,0)};row.Children.Add(chip);
        var field=new Grid();field.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});field.ColumnDefinitions.Add(new ColumnDefinition());
        var prefix=Text("#",14,"text");prefix.Margin=new Thickness(0,0,8,0);field.Children.Add(prefix);
        var input=new TextBox {Text=current[i],MaxLength=6,FontSize=14,Height=32,Padding=new Thickness(8,4,8,4),Foreground=BrushOf("text"),Background=Brushes.Transparent,BorderThickness=new Thickness(0),VerticalContentAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(input,labels[i]+"，6位十六进制颜色");InputMethod.SetIsInputMethodEnabled(input,false);
        var inputFrame=new Border {Height=34,CornerRadius=new CornerRadius(17),Background=BrushOf("surface"),BorderBrush=BrushOf("line"),BorderThickness=new Thickness(1),Child=input};Grid.SetColumn(inputFrame,1);field.Children.Add(inputFrame);fields.Add(input);
        input.PreviewTextInput+=delegate(object sender,TextCompositionEventArgs e){e.Handled=!System.Text.RegularExpressions.Regex.IsMatch(e.Text,@"\A[0-9A-Fa-f]+\z");};
        DataObject.AddPastingHandler(input,delegate(object sender,DataObjectPastingEventArgs e){string value=e.DataObject.GetDataPresent(DataFormats.UnicodeText)?Convert.ToString(e.DataObject.GetData(DataFormats.UnicodeText)):e.DataObject.GetDataPresent(DataFormats.Text)?Convert.ToString(e.DataObject.GetData(DataFormats.Text)):null;value=CustomColorMemory.Normalize(value);if(!System.Text.RegularExpressions.Regex.IsMatch(value,@"\A[0-9A-Fa-f]{1,6}\z") || input.Text.Length-input.SelectionLength+value.Length>6){e.CancelCommand();return;}e.DataObject=new DataObject(DataFormats.UnicodeText,value);});
        input.TextChanged+=delegate{if(CustomColorMemory.IsHex(input.Text))chip.Background=new SolidColorBrush(Parse("#"+input.Text));RememberCustomColors(fields);};row.Children.Add(field);content.Children.Add(row);
      }
      var hint=Text("输入6位十六进制颜色值，或选择下方常用颜色。",12,"muted");hint.Margin=new Thickness(0,0,0,12);content.Children.Add(hint);
      for(int i=0;i<fields.Count;i++){int fieldIndex=i;var picks=new WrapPanel {Margin=new Thickness(0,0,0,10)};var caption=Text(labels[i],12,"muted");caption.Width=104;picks.Children.Add(caption);
        foreach(string color in new[]{"#FFFFFF","#333333","#252525","#2563EB","#7958D6","#258267"}){string chosen=color;var swatch=Btn(null,delegate{fields[fieldIndex].Text=chosen.Substring(1);},26,"text",null,false,26);swatch.Padding=new Thickness(0);swatch.Content=new Ellipse {Width=22,Height=22,Fill=new SolidColorBrush(Parse(color)),Stroke=BrushOf("line"),StrokeThickness=1};AutomationProperties.SetName(swatch,"选择颜色 "+color);picks.Children.Add(swatch);}content.Children.Add(picks);}
      bool accepted=await ShowModal("自定义配色",content,true,delegate{return fields.All(field=>CustomColorMemory.IsHex(field.Text));});
      if(accepted){for(int i=0;i<keys.Length;i++)cfg[keys[i]]="#"+fields[i].Text.ToUpperInvariant();cfg["scheme"]="__custom__";Changed();BuildPage();}
    }
    public Task<bool> Info(string heading,string message){return ShowModal(heading,Text(message,14,"text"),false,null);}
    private Task<bool> Confirm(string heading,string message){return ShowModal(heading,Text(message,14,"text"),true,null);}
    private Task<bool> ShowModal(string heading,UIElement body,bool cancelable,Func<bool> validate){
      if(modalResult!=null)return modalResult.Task;
      focusBeforeModal=qaFolder==null?Keyboard.FocusedElement:null;
      modalResult=new TaskCompletionSource<bool>();overlay.Children.Clear();overlay.Visibility=Visibility.Visible;
      var veil=new Border {Background=new SolidColorBrush(Color.FromArgb(86,0,0,0)),Opacity=0};overlay.Children.Add(veil);
      modalCard=new Border {Width=348,MinHeight=172,CornerRadius=new CornerRadius(28),Background=BrushOf("dialog"),Padding=new Thickness(24,22,24,16),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,RenderTransformOrigin=new Point(.5,.5),Opacity=0};overlay.Children.Add(modalCard);
      var content=new StackPanel();modalCard.Child=content;var label=Text(heading,22,"text");label.Margin=new Thickness(0,0,0,16);content.Children.Add(label);content.Children.Add(body);
      var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};content.Children.Add(actions);
      if(cancelable){var cancel=Btn("取消",async delegate{await Dismiss(false);},64,"text",null,false,40);cancel.Background=Brushes.Transparent;actions.Children.Add(cancel);}
      var confirm=Btn("确定",async delegate{if(validate!=null && !validate()){SetStatus("请输入6位十六进制颜色值。");return;}await Dismiss(true);},64,"text",null,false,40);confirm.Background=Brushes.Transparent;actions.Children.Add(confirm);
      modalScale=new ScaleTransform(1.2,1.2);modalCard.RenderTransform=modalScale;frame.Effect=blur;
      veil.BeginAnimation(OpacityProperty,Motion.Number(0,1,Motion.DialogMs,Motion.Alert));modalCard.BeginAnimation(OpacityProperty,Motion.Number(0,1,Motion.DialogMs,Motion.Alert));
      modalScale.BeginAnimation(ScaleTransform.ScaleXProperty,Motion.Number(1.2,1,Motion.DialogMs,Motion.Alert));modalScale.BeginAnimation(ScaleTransform.ScaleYProperty,Motion.Number(1.2,1,Motion.DialogMs,Motion.Alert));
      blur.BeginAnimation(BlurEffect.RadiusProperty,Motion.Number(0,6,Motion.DialogMs,Motion.Alert));if(qaFolder==null)confirm.Focus();return modalResult.Task;
    }
    public async Task Dismiss(bool result){if(modalResult==null || modalClosing)return;modalClosing=true;var task=modalResult;
      double sx=modalScale.ScaleX,sy=modalScale.ScaleY;modalScale.BeginAnimation(ScaleTransform.ScaleXProperty,null);modalScale.BeginAnimation(ScaleTransform.ScaleYProperty,null);modalScale.ScaleX=sx;modalScale.ScaleY=sy;
      modalCard.BeginAnimation(OpacityProperty,Motion.Number(modalCard.Opacity,0,Motion.DialogMs,Motion.Alert));var veil=overlay.Children[0] as Border;veil.BeginAnimation(OpacityProperty,Motion.Number(veil.Opacity,0,Motion.DialogMs,Motion.Alert));blur.BeginAnimation(BlurEffect.RadiusProperty,Motion.Number(blur.Radius,0,Motion.DialogMs,Motion.Alert));
      if(Motion.Enabled)await Task.Delay(Motion.DialogMs);overlay.Visibility=Visibility.Collapsed;overlay.Children.Clear();PruneButtons();frame.Effect=null;modalResult=null;modalClosing=false;
      if(focusBeforeModal!=null)Keyboard.Focus(focusBeforeModal);task.TrySetResult(result);
    }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int length);
    private void SetNativeTheme(){try{IntPtr hwnd=new System.Windows.Interop.WindowInteropHelper(this).Handle;int value=IsDark?1:0;DwmSetWindowAttribute(hwnd,20,ref value,4);int corner=2;DwmSetWindowAttribute(hwnd,33,ref corner,4);}catch{}}
    private void Capture(string name){Capture(name,1);}
    private void Capture(string name,double scale){UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*scale),(int)Math.Ceiling(root.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(IOPath.Combine(qaFolder,name+".png")))encoder.Save(file);}
    private void WriteQaError(string error){Directory.CreateDirectory(qaFolder);File.WriteAllText(IOPath.Combine(qaFolder,"qa-error.txt"),error,Encoding.UTF8);}
    private async Task QualityCapture(){
      Directory.CreateDirectory(qaFolder);Theme("light");await Task.Delay(300);Capture("settings-light");Capture("settings-light-125",1.25);Capture("settings-light-150",1.5);Capture("settings-light-200",2);Theme("dark");await Task.Delay(350);Capture("settings-dark");
      Height=900;await Task.Delay(160);Capture("settings-full-dark");Theme("light");await Task.Delay(250);Capture("settings-full-light");Height=740;await Task.Delay(140);
      Theme("system");await Task.Delay(300);Capture("settings-follow-system");Theme("dark");await Task.Delay(300);
      cfg["fuzzy_enabled"]=false;Navigate("maintenance");await Task.Delay(250);Capture("input-dark-disabled");fuzzySwitch.IsChecked=true;cfg["fuzzy_nl"]=true;cfg["fuzzy_z_zh"]=true;cfg["fuzzy_an_ang"]=true;BuildPage();await Task.Delay(250);Capture("input-dark");Theme("light");await Task.Delay(250);Capture("input-light");fuzzySwitch.IsChecked=false;await Task.Delay(250);Capture("input-light-disabled");fuzzySwitch.IsChecked=true;await Task.Delay(250);scroll.ScrollToVerticalOffset(260);await Task.Delay(180);Capture("input-dictionary-light");
      Navigate("guide");Theme("light");scroll.ScrollToTop();await Task.Delay(250);Capture("guide-light");scroll.ScrollToEnd();await Task.Delay(200);Capture("about-light");Theme("dark");await Task.Delay(250);Capture("about-dark");
      Navigate("appearance");cfg["horizontal"]=true;cfg["fontsize"]=24;cfg["radius"]=20;BuildPage();scroll.ScrollToTop();await Task.Delay(220);Capture("candidate-max-font");
      Reset();Theme("light");await Task.Delay(250);var shown=Info("提示","界面提示测试。\n圆角与文字使用矢量抗锯齿绘制。");await Task.Delay(460);Capture("dialog-light");await Dismiss(true);await shown;
      Width=908;Height=640;await Task.Delay(180);scroll.ScrollToTop();Capture("settings-reference-size");
      scroll.ScrollToVerticalOffset(120);await Task.Delay(160);Capture("list-edge-fade");
      File.WriteAllText(IOPath.Combine(qaFolder,"qa-result.json"),json.Serialize(new{renderer="WPF vector",pages=3,light_dark=true,dialog_spring_ms=Motion.DialogMs,segment_ms=Motion.SegmentMs,check_ms=Motion.CheckMs,layout="single column",status="captured for inspection"}),Encoding.UTF8);
      Close();
    }
  }
  public static class Program {
    private static bool ReportError(string[] args,Exception error){
      for(int i=0;i<args.Length-1;i++)if(args[i]=="--qa"){
        Directory.CreateDirectory(args[i+1]);File.WriteAllText(IOPath.Combine(args[i+1],"qa-error.txt"),error.ToString(),Encoding.UTF8);return true;
      }
      MessageBox.Show(error.Message,"麻绳输入法 · 设置");return false;
    }
    [STAThread] public static void Main(string[] args){try{
      using(var instance=args.Contains("--qa")?null:new SettingsInstance()){
      if(instance!=null && !instance.TryOwn()){instance.Send(args.Contains("--update-dictionary")?"update":args.Contains("--check-updates")?"check_updates":"activate");return;}
      var app=new Application();app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e){bool qa=ReportError(args,e.Exception);e.Handled=true;if(qa)app.Shutdown(1);};
      var window=new SettingsWindow(args);if(instance!=null)instance.Listen(command=>window.Dispatcher.BeginInvoke(new Action(delegate{window.ReceiveLaunch(command);})));
      app.Run(window);}
    }catch(Exception error){ReportError(args,error);}}
  }
}
