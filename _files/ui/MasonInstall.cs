using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Mason.Common;
using IOPath=System.IO.Path;

[assembly: TargetFramework(".NETFramework,Version=v4.8",FrameworkDisplayName=".NET Framework 4.8")]
[assembly: AssemblyTitle("麻绳输入法 · 安装")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyDescription("麻绳输入法安装与更新")]
[assembly: AssemblyVersion("2.4.6.0")]
[assembly: AssemblyFileVersion("2.4.6.0")]

namespace Mason.Install {
  // The same parameters used by Mason.Settings, checked against FlClash v0.9.3.
  public sealed class InstallEase : EasingFunctionBase {
    private double x1,y1,x2,y2;
    public InstallEase(double a,double b,double c,double d){x1=a;y1=b;x2=c;y2=d;EasingMode=EasingMode.EaseIn;}
    public InstallEase():this(.4,0,.2,1){}
    private double B(double t,double a,double b){double s=1-t;return 3*s*s*t*a+3*s*t*t*b+t*t*t;}
    protected override double EaseInCore(double t){if(t<=0 || t>=1)return t;double low=0,high=1,mid=.5;for(int i=0;i<24;i++){mid=(low+high)/2;if(B(mid,x1,x2)<t)low=mid;else high=mid;}return B(mid,y1,y2);}
    protected override Freezable CreateInstanceCore(){return new InstallEase(x1,y1,x2,y2);}
  }
  public static class InstallMotion {
    public const int ThemeMs=200,LookMs=200,ReleaseMs=250;
    public static readonly InstallEase FastOutSlowIn=new InstallEase(.4,0,.2,1),InOut=new InstallEase(.42,0,.58,1);
    public const int DialogMs=404;
    public static readonly InstallAlertEase Alert=new InstallAlertEase();
    public static bool Enabled {get{return SystemParameters.ClientAreaAnimation;}}
    public static DoubleAnimation Number(double from,double to,int ms,IEasingFunction curve){return new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(Enabled?ms:0)){EasingFunction=curve,FillBehavior=FillBehavior.Stop};}
    public static void Color(SolidColorBrush brush,Color target,bool animate){Color previous=brush.Color;brush.BeginAnimation(SolidColorBrush.ColorProperty,null);brush.Color=target;if(animate && Enabled)brush.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(previous,target,TimeSpan.FromMilliseconds(ThemeMs)){FillBehavior=FillBehavior.Stop});}
  }
  public sealed class InstallAlertEase : EasingFunctionBase {
    private const double Omega=2*Math.PI/.275;
    public InstallAlertEase(){EasingMode=EasingMode.EaseIn;}
    private double Position(double seconds){return 1-(1+Omega*seconds)*Math.Exp(-Omega*seconds);}
    protected override double EaseInCore(double t){return Position(t*.404)+(1-Position(.404))*t;}
    protected override Freezable CreateInstanceCore(){return new InstallAlertEase();}
  }
  public sealed class InstallSurface : FrameworkElement {
    public static readonly DependencyProperty FillProperty=DependencyProperty.Register("Fill",typeof(Brush),typeof(InstallSurface),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineProperty=DependencyProperty.Register("Line",typeof(Brush),typeof(InstallSurface),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RadiusProperty=DependencyProperty.Register("Radius",typeof(double),typeof(InstallSurface),new FrameworkPropertyMetadata(22.0,FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill {get{return (Brush)GetValue(FillProperty);}set{SetValue(FillProperty,value);}}
    public Brush Line {get{return (Brush)GetValue(LineProperty);}set{SetValue(LineProperty,value);}}
    public double Radius {get{return (double)GetValue(RadiusProperty);}set{SetValue(RadiusProperty,value);}}
    public InstallSurface(){SnapsToDevicePixels=false;RenderOptions.SetEdgeMode(this,EdgeMode.Unspecified);}
    protected override void OnRender(DrawingContext drawing){double stroke=Line==null?0:1.25;double half=stroke/2;if(ActualWidth<=stroke || ActualHeight<=stroke)return;drawing.DrawRoundedRectangle(Fill,Line==null?null:new Pen(Line,stroke),new Rect(half,half,ActualWidth-stroke,ActualHeight-stroke),Math.Max(0,Radius-half),Math.Max(0,Radius-half));}
  }
  public sealed class InstallButton : Button {
    private InstallerWindow owner;
    private string kind;
    public bool Selected;
    private SolidColorBrush back=new SolidColorBrush(),fore=new SolidColorBrush(),line=new SolidColorBrush();
    private InstallSurface hover,tint;
    private static ControlTemplate template=(ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:Mason.Install;assembly=Mason.Install' TargetType='{x:Type Button}'>
      <Grid><local:InstallSurface Fill='{TemplateBinding Background}' Line='{TemplateBinding BorderBrush}' Radius='{Binding Tag,RelativeSource={RelativeSource TemplatedParent}}'/>
      <local:InstallSurface x:Name='PART_Hover' Fill='{TemplateBinding Foreground}' Opacity='0' Radius='{Binding Tag,RelativeSource={RelativeSource TemplatedParent}}'/>
      <local:InstallSurface x:Name='PART_Tint' Fill='{TemplateBinding Foreground}' Opacity='0' Radius='{Binding Tag,RelativeSource={RelativeSource TemplatedParent}}'/>
      <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Grid>
      <ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.42'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
    public InstallButton(InstallerWindow window,string caption,double width,double height,string type,Action action){
      owner=window;kind=type;Width=width;Height=height;Tag=height/2;Template=template;Background=back;Foreground=fore;BorderBrush=type=="primary" || type=="text"?null:line;Cursor=Cursors.Hand;FontSize=type=="theme"?12:14;FontWeight=FontWeights.Medium;Focusable=true;
      var label=new TextBlock {Text=caption,FontSize=FontSize,FontWeight=FontWeight,Foreground=fore};Content=label;AutomationProperties.SetName(this,caption);
      Click+=delegate{if(action!=null)action();};MouseEnter+=delegate{if(hover!=null)hover.Opacity=.04;};MouseLeave+=delegate{if(hover!=null)hover.Opacity=0;Release();};PreviewMouseLeftButtonDown+=delegate{if(tint!=null)tint.Opacity=.1;};PreviewMouseLeftButtonUp+=delegate{Release();};
      var focus=new Style(typeof(Control));var focusTemplate=(ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border BorderBrush='#808080' BorderThickness='1' Margin='3' CornerRadius='"+(height/2-3).ToString(System.Globalization.CultureInfo.InvariantCulture)+@"'/></ControlTemplate>");focus.Setters.Add(new Setter(Control.TemplateProperty,focusTemplate));FocusVisualStyle=focus;
      owner.Buttons.Add(this);Apply(false);
    }
    private void Release(){if(tint!=null){double from=tint.Opacity;tint.BeginAnimation(OpacityProperty,null);tint.Opacity=0;tint.BeginAnimation(OpacityProperty,InstallMotion.Number(from,0,InstallMotion.ReleaseMs,InstallMotion.InOut));}}
    public override void OnApplyTemplate(){base.OnApplyTemplate();hover=GetTemplateChild("PART_Hover") as InstallSurface;tint=GetTemplateChild("PART_Tint") as InstallSurface;}
    public void Apply(bool animate){InstallMotion.Color(back,owner.Color(kind=="primary"?"primary":Selected?"selected":kind=="text"?"transparent":"surface"),animate);InstallMotion.Color(fore,owner.Color(kind=="primary"?"onPrimary":"text"),animate);InstallMotion.Color(line,owner.Color(Selected?"outline":"line"),animate);}
  }
  public sealed class InstallProgressBar : FrameworkElement {
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register("Value",typeof(double),typeof(InstallProgressBar),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OffsetProperty=DependencyProperty.Register("Offset",typeof(double),typeof(InstallProgressBar),new FrameworkPropertyMetadata(-.3,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value{get{return (double)GetValue(ValueProperty);}set{SetValue(ValueProperty,value);}}
    public double Offset{get{return (double)GetValue(OffsetProperty);}set{SetValue(OffsetProperty,value);}}
    public Brush Track,Fill;public bool Indeterminate;
    public InstallProgressBar(){Height=8;SnapsToDevicePixels=false;RenderOptions.SetEdgeMode(this,EdgeMode.Unspecified);AutomationProperties.SetName(this,"当前步骤进度");}
    public void SetProgress(double? value){bool unknown=!value.HasValue;if(unknown!=Indeterminate){Indeterminate=unknown;BeginAnimation(OffsetProperty,null);if(unknown && InstallMotion.Enabled)BeginAnimation(OffsetProperty,new DoubleAnimation(-.3,1,TimeSpan.FromSeconds(1.15)){RepeatBehavior=RepeatBehavior.Forever});}
      if(value.HasValue){double old=Value;BeginAnimation(ValueProperty,null);Value=Math.Max(0,Math.Min(1,value.Value/100));if(InstallMotion.Enabled)BeginAnimation(ValueProperty,InstallMotion.Number(old,Value,InstallMotion.LookMs,InstallMotion.FastOutSlowIn));}InvalidateVisual();}
    protected override void OnRender(DrawingContext d){double h=ActualHeight,w=ActualWidth;d.DrawRoundedRectangle(Track,null,new Rect(0,0,w,h),h/2,h/2);d.PushClip(new RectangleGeometry(new Rect(0,0,w,h),h/2,h/2));if(Indeterminate)d.DrawRoundedRectangle(Fill,null,new Rect(w*(InstallMotion.Enabled?Offset:.35),0,w*.3,h),h/2,h/2);else if(Value>0)d.DrawRoundedRectangle(Fill,null,new Rect(0,0,w*Value,h),h/2,h/2);d.Pop();}
  }
  public sealed class InstallerWindow : Window {
    public readonly List<InstallButton> Buttons=new List<InstallButton>();
    private readonly Dictionary<string,SolidColorBrush> brushes=new Dictionary<string,SolidColorBrush>();
    private readonly string[] args;
    public InstallPlan Plan;
    private Grid shell,body,footer,contentFrame,overlay;
    private StackPanel themes,stepList;
    private TextBlock heading,description,measure,speed,elapsed,errorHint;
    private InstallProgressBar progress;
    private TextBox address;
    private InstallButton primary,secondary,skipButton;
    private string theme="system",view="welcome",phase="",logDir,captureDir,actionState="";
    private bool dark=ReadSystemDark(),busy,closeAfterCancel;
    private ProgressEvent current;
    private InstallResult result;
    private Process worker;
    private DispatcherTimer poll;
    private int seenLines;
    private Stopwatch watch=new Stopwatch();
    private HashSet<string> captures=new HashSet<string>();
    public InstallerWindow(string[] arguments):this(arguments,null){}
    public InstallerWindow(string[] arguments,InstallPlan customPlan){
      args=arguments;Plan=customPlan??InstallationDetection.Detect(AppDomain.CurrentDomain.BaseDirectory);
      for(int i=0;i<args.Length-1;i++)if(args[i]=="--capture")captureDir=IOPath.GetFullPath(args[i+1]);
      if(args.Contains("--update-dictionary"))Plan.UpdateDictionary=true;
      if(args.Contains("--traditional"))Plan.Mode="Hant";
      Title="麻绳输入法 · 安装";Width=620;Height=430;MinWidth=580;MinHeight=380;ResizeMode=ResizeMode.CanMinimize;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=14;UseLayoutRounding=true;if(args.Contains("--no-activate"))ShowActivated=false;
      TextOptions.SetTextRenderingMode(this,TextRenderingMode.Grayscale);TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
      foreach(string key in new[]{"bg","surface","soft","text","muted","line","outline","primary","onPrimary","selected","error","transparent"})brushes[key]=new SolidColorBrush(Color(key));
      BuildShell();BuildWelcome();SystemEvents.UserPreferenceChanged+=SystemThemeChanged;
      Closed+=delegate{SystemEvents.UserPreferenceChanged-=SystemThemeChanged;if(poll!=null)poll.Stop();if(worker!=null)worker.Dispose();};
      Loaded+=delegate{SetNativeTheme();Capture("01-准备安装");if(args.Contains("--start"))Dispatcher.BeginInvoke(new Action(StartInstall));};
      Closing+=OnClosing;
    }
    public Color Color(string name){bool isDark=theme=="dark" || (theme=="system" && dark);var values=isDark?new Dictionary<string,string>{{"bg","#171717"},{"surface","#242424"},{"soft","#2D2D2D"},{"text","#EFEFEF"},{"muted","#BDBDBD"},{"line","#424242"},{"outline","#A8A8A8"},{"primary","#E6E6E6"},{"onPrimary","#171717"},{"selected","#454545"},{"error","#FFB4AB"},{"transparent","#00000000"}}:new Dictionary<string,string>{{"bg","#F9F9F9"},{"surface","#F3F3F3"},{"soft","#EFEFEF"},{"text","#202020"},{"muted","#555555"},{"line","#E0E0E0"},{"outline","#5C5C5C"},{"primary","#202020"},{"onPrimary","#FFFFFF"},{"selected","#D5D5D5"},{"error","#B3261E"},{"transparent","#00000000"}};return (Color)ColorConverter.ConvertFromString(values[name]);}
    private static bool ReadSystemDark(){try{using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))return key!=null && Convert.ToInt32(key.GetValue("AppsUseLightTheme",1))==0;}catch{return false;}}
    private void SystemThemeChanged(object sender,UserPreferenceChangedEventArgs e){Dispatcher.BeginInvoke(new Action(delegate{dark=ReadSystemDark();if(theme=="system")ApplyTheme(true);}));}
    public void SetTheme(string value){theme=value;ApplyTheme(true);}
    private void ApplyTheme(bool animate){foreach(var pair in brushes)InstallMotion.Color(pair.Value,Color(pair.Key),animate);foreach(var button in Buttons){if(themes.Children.Contains(button))button.Selected=Convert.ToString(button.DataContext)==theme;button.Apply(animate);}SetNativeTheme();if(progress!=null)progress.InvalidateVisual();}
    private Brush B(string key){return brushes[key];}
    private TextBlock T(string text,double size,string color,bool bold){return new TextBlock {Text=text,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=B(color),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};}
    private InstallButton Button(string text,double width,string kind,Action action){return new InstallButton(this,text,width,48,kind,action);}
    private void BuildShell(){
      shell=new Grid {Background=B("bg")};Content=shell;contentFrame=new Grid();shell.Children.Add(contentFrame);contentFrame.RowDefinitions.Add(new RowDefinition {Height=new GridLength(66)});contentFrame.RowDefinitions.Add(new RowDefinition());contentFrame.RowDefinitions.Add(new RowDefinition {Height=new GridLength(86)});
      var top=new Grid {Margin=new Thickness(24,14,24,10)};contentFrame.Children.Add(top);var name=T("Mason",19,"text",true);name.HorizontalAlignment=HorizontalAlignment.Left;top.Children.Add(name);var version=T(InstallFiles.ProductVersion,12,"muted",false);version.HorizontalAlignment=HorizontalAlignment.Right;top.Children.Add(version);
      themes=new StackPanel();
      body=new Grid {Margin=new Thickness(24,6,24,0)};Grid.SetRow(body,1);contentFrame.Children.Add(body);
      footer=new Grid {Margin=new Thickness(24,16,24,22)};Grid.SetRow(footer,2);contentFrame.Children.Add(footer);
      overlay=new Grid {Visibility=Visibility.Collapsed};Panel.SetZIndex(overlay,5);shell.Children.Add(overlay);KeyboardNavigation.SetTabNavigation(overlay,KeyboardNavigationMode.Cycle);FocusManager.SetIsFocusScope(overlay,true);
    }
    private StackPanel FooterActions(){var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(actions);return actions;}
    private void ClearBody(){body.Children.Clear();Buttons.RemoveAll(x=>!themes.Children.Contains(x));}
    private Border Card(UIElement content){return new Border {Background=B("surface"),CornerRadius=new CornerRadius(22),Padding=new Thickness(20,16,20,16),Child=content};}
    private StackPanel Column(){return new StackPanel {Orientation=Orientation.Vertical};}
    private void BuildWelcome(){
      view="welcome";Height=430;ClearBody();var column=Column();body.Children.Add(column);heading=T("选择安装位置",26,"text",true);column.Children.Add(heading);
      heading.Margin=new Thickness(0,0,0,24);
      var location=new Grid();location.ColumnDefinitions.Add(new ColumnDefinition());location.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(12)});location.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(92)});column.Children.Add(location);
      address=new TextBox {Text=Plan.AppDir,Height=48,FontSize=14,Foreground=B("text"),CaretBrush=B("text"),Background=B("surface"),BorderBrush=B("line"),BorderThickness=new Thickness(1.25),VerticalContentAlignment=VerticalAlignment.Center,Padding=new Thickness(16,0,16,0),SelectionBrush=B("selected")};
      address.Template=(ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:Mason.Install;assembly=Mason.Install' TargetType='{x:Type TextBox}'><Grid><local:InstallSurface Fill='{TemplateBinding Background}' Line='{TemplateBinding BorderBrush}' Radius='24'/><ScrollViewer x:Name='PART_ContentHost' Margin='16,0,16,0' VerticalAlignment='Center'/></Grid></ControlTemplate>");AutomationProperties.SetName(address,"安装地址");location.Children.Add(address);var browse=Button("浏览",92,"secondary",BrowseAddress);Grid.SetColumn(browse,2);location.Children.Add(browse);

      footer.Children.Clear();secondary=Button("取消",88,"secondary",Close);var actions=FooterActions();actions.Children.Add(secondary);primary=Button("开始安装",136,"primary",StartInstall);primary.Margin=new Thickness(12,0,0,0);actions.Children.Add(primary);
      Capture("01-准备安装");
    }
    private void BrowseAddress(){using(var picker=new System.Windows.Forms.FolderBrowserDialog {Description="选择麻绳输入法的安装文件夹",SelectedPath=Directory.Exists(address.Text)?address.Text:Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),ShowNewFolderButton=true}){if(picker.ShowDialog()==System.Windows.Forms.DialogResult.OK){string folder=picker.SelectedPath;address.Text=String.Equals(IOPath.GetFileName(folder.TrimEnd('\\')),"Mason",StringComparison.OrdinalIgnoreCase)?folder:IOPath.Combine(folder,"Mason");}}}
    public void ShowProgress(ProgressEvent value){
      current=value;busy=true;if(view!="progress"){view="progress";BuildProgress();}
      bool changed=phase!=value.Phase;phase=value.Phase;heading.Text=value.Title;description.Text=value.Message;description.Foreground=B(value.NeedsAttention?"error":"muted");
      progress.SetProgress(value.NeedsAttention?0:value.Percent);
      measure.Text=value.TotalBytes>0?Bytes(value.Bytes)+" / "+Bytes(value.TotalBytes):value.NeedsAttention?"操作暂停":value.Percent.HasValue?(value.Percent.Value>=100?"已完成":Math.Floor(value.Percent.Value)+"%"):"正在处理";
      speed.Text=value.BytesPerSecond>0?Bytes((long)value.BytesPerSecond)+"/s":value.TotalBytes>0 && value.Percent.HasValue?Math.Floor(value.Percent.Value)+"%":"";
      elapsed.Text="已用时 "+Time(watch.Elapsed);
      string actions=value.Phase+value.NeedsAttention+value.CanSkip+value.CanCancel+value.OperationId;if(actions!=actionState){actionState=actions;BuildProgressActions(value);}
      if(changed && InstallMotion.Enabled && String.IsNullOrWhiteSpace(captureDir)){body.BeginAnimation(OpacityProperty,InstallMotion.Number(.35,1,InstallMotion.LookMs,InstallMotion.FastOutSlowIn));}
      if(!String.IsNullOrWhiteSpace(captureDir)){string key=value.Phase+(value.NeedsAttention?"-需要处理":value.BytesPerSecond>0 && value.Percent>=25?"-下载进度":value.Phase=="layout" && value.Percent>=45?"-迁移进度":"");Capture(key);}
    }
    private void BuildProgress(){
      Height=400;ClearBody();actionState="";var column=Column();body.Children.Add(column);heading=T("正在准备安装",25,"text",true);column.Children.Add(heading);description=T("检查安装材料",14,"muted",false);description.Margin=new Thickness(0,8,0,18);description.MinHeight=24;description.MaxHeight=40;description.TextTrimming=TextTrimming.CharacterEllipsis;heading.Margin=new Thickness(0,0,0,24);
      var data=Column();var labels=new Grid();measure=T("正在处理",14,"text",true);labels.Children.Add(measure);speed=T("",13,"muted",false);speed.HorizontalAlignment=HorizontalAlignment.Right;labels.Children.Add(speed);data.Children.Add(labels);
      progress=new InstallProgressBar {Fill=B("primary"),Track=B("line"),Margin=new Thickness(0,15,0,12)};data.Children.Add(progress);elapsed=T("已用时 0 秒",12,"muted",false);data.Children.Add(elapsed);column.Children.Add(Card(data));
      stepList=Column();errorHint=T("",12,"muted",false);
    }
    private void UpdateSteps(ProgressEvent data){
      stepList.Children.Clear();var steps=new List<string[]> {new[]{"prepare","检查安装材料"}};
      if((data.Completed??new string[0]).Contains("engine-download") || data.Phase=="engine-download")steps.Add(new[]{"engine-download","下载引擎"});
      if(Plan.UpdateDictionary)steps.Add(new[]{"dictionary-download","下载最新词库"});
      steps.AddRange(new[]{new[]{"engine","检查输入法引擎"},new[]{"layout","整理安装目录"},new[]{"backup","备份现有文件"},new[]{"files","写入程序和词库"},new[]{"cloud","添加云输入"},new[]{"interface","应用新版界面"},new[]{"deploy","建立词库索引"},new[]{"verify","验证并启动"}});
      foreach(var step in steps){bool done=(data.Completed??new string[0]).Contains(step[0]),omitted=(data.Skipped??new string[0]).Contains(step[0]),active=data.Phase==step[0];var row=new Grid {Height=29};var icon=new Canvas {Width=20,Height=20,HorizontalAlignment=HorizontalAlignment.Left};row.Children.Add(icon);
        icon.Children.Add(new System.Windows.Shapes.Ellipse {Width=16,Height=16,Margin=new Thickness(2),Stroke=B(done || active?"primary":"line"),StrokeThickness=1.25,Fill=active || done?B("primary"):Brushes.Transparent});
        if(done)icon.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M 5,10 L 8,13 L 14,6"),Stroke=B("onPrimary"),StrokeThickness=1.35,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
        else if(active)icon.Children.Add(new System.Windows.Shapes.Ellipse {Width=4,Height=4,Fill=B("onPrimary"),Margin=new Thickness(8)});
        var label=T(step[1],13,active?"text":"muted",active);label.Margin=new Thickness(30,0,0,0);row.Children.Add(label);var status=T(omitted?"已跳过":done?"已完成":active?(data.NeedsAttention?"等待处理":"进行中"):"",12,"muted",false);status.HorizontalAlignment=HorizontalAlignment.Right;row.Children.Add(status);stepList.Children.Add(row);
      }
    }
    private void BuildProgressActions(ProgressEvent data){
      footer.Children.Clear();Buttons.RemoveAll(x=>!themes.Children.Contains(x));var actions=FooterActions();
      if(data.CanSkip && !data.NeedsAttention){skipButton=Button("跳过此步",112,"secondary",delegate{Send("skip");});actions.Children.Add(skipButton);}
      secondary=Button(data.CanCancel?"取消安装":"安装中",112,data.CanCancel?"secondary":"text",delegate{Send("cancel");});secondary.Margin=new Thickness(actions.Children.Count>0?12:0,0,0,0);secondary.IsEnabled=data.CanCancel;actions.Children.Add(secondary);
      if(data.NeedsAttention)ShowAttention(data);else HideAttention();
    }
    private void HideAttention(){overlay.Visibility=Visibility.Collapsed;overlay.Children.Clear();contentFrame.Effect=null;contentFrame.IsHitTestVisible=true;}
    private void ShowAttention(ProgressEvent data){
      overlay.Children.Clear();overlay.Visibility=Visibility.Visible;contentFrame.IsHitTestVisible=false;var blur=new BlurEffect {Radius=6};contentFrame.Effect=blur;
      overlay.Children.Add(new Border {Background=new SolidColorBrush(Colors.Black),Opacity=.24});var content=Column();content.Children.Add(T(data.CanSkip?"是否跳过当前步骤？":"此步骤还不能跳过",21,"text",true));

      var note=T(data.CanSkip?"跳过后基本输入功能不受影响。":"这一步关系到输入法可用性，可重新检查后继续。",12,"muted",false);note.Margin=new Thickness(0,12,0,0);content.Children.Add(note);var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0)};content.Children.Add(actions);
      var exit=Button("退出",72,"text",delegate{closeAfterCancel=true;Send("cancel");});actions.Children.Add(exit);skipButton=Button(data.CanSkip?"跳过":"检查后继续",data.CanSkip?84:120,"secondary",delegate{Send(data.CanSkip?"skip":"check");});skipButton.Margin=new Thickness(6,0,0,0);actions.Children.Add(skipButton);primary=Button("重试",84,"primary",delegate{Send("retry");});primary.Margin=new Thickness(8,0,0,0);actions.Children.Add(primary);
      var card=new Border {Width=470,Background=B("surface"),CornerRadius=new CornerRadius(22),Padding=new Thickness(24),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Child=content};overlay.Children.Add(card);var scale=new ScaleTransform(1,1);card.RenderTransform=scale;card.RenderTransformOrigin=new Point(.5,.5);
      if(InstallMotion.Enabled && String.IsNullOrWhiteSpace(captureDir)){card.BeginAnimation(OpacityProperty,InstallMotion.Number(0,1,InstallMotion.DialogMs,InstallMotion.Alert));scale.BeginAnimation(ScaleTransform.ScaleXProperty,InstallMotion.Number(1.2,1,InstallMotion.DialogMs,InstallMotion.Alert));scale.BeginAnimation(ScaleTransform.ScaleYProperty,InstallMotion.Number(1.2,1,InstallMotion.DialogMs,InstallMotion.Alert));}if(String.IsNullOrWhiteSpace(captureDir))primary.Focus();
    }
    public void ShowResult(InstallResult value){
      busy=false;result=value;view="result";Height=340;HideAttention();ClearBody();if(poll!=null)poll.Stop();watch.Stop();var column=Column();body.Children.Add(column);
      heading=T(value.Ok?"安装完成":value.Canceled?"安装已取消":"安装未完成",26,"text",true);column.Children.Add(heading);var message=T(value.Ok?(Plan.Mode=="Hant"?"繁体中文":"简体中文")+" · "+((value.Skipped??new string[0]).Contains("interface")?"词库已应用":"词库与界面已应用"):value.Message,14,value.Ok?"muted":"error",false);description=message;message.Margin=new Thickness(0,10,0,20);if(!value.Ok)column.Children.Add(message);
      var details=Column();if(value.Ok){details.Children.Add(T("Mason "+InstallFiles.ProductVersion,16,"text",true));var time=T(value.CompletedAt+" 安装完成",13,"muted",false);time.Margin=new Thickness(0,6,0,0);details.Children.Add(time);if(!String.IsNullOrWhiteSpace(value.DictionaryStatus)){var words=T(value.DictionaryStatus,13,"muted",false);words.Margin=new Thickness(0,6,0,0);details.Children.Add(words);}}
      foreach(var path in new[]{new[]{"安装位置",value.AppDir},new[]{"词库位置",value.UserDir},new[]{"备份位置",value.BackupDir}}){if(String.IsNullOrWhiteSpace(path[1]))continue;var label=T(path[0],12,"muted",false);label.Margin=new Thickness(0,16,0,4);details.Children.Add(label);details.Children.Add(T(path[1],13,"text",false));}
      if(value.Skipped!=null && value.Skipped.Length>0){var omitted=T("已跳过："+String.Join("、",value.Skipped.Select(PhaseLabel)),13,"muted",false);omitted.Margin=new Thickness(0,18,0,0);column.Children.Add(omitted);}
      footer.Children.Clear();secondary=Button(value.Ok?"完成":"关闭",88,"secondary",Close);var actions=FooterActions();actions.Children.Add(secondary);
      if(value.Ok && NativeMenuRefreshPending(value.UserDir)){var refresh=Button("刷新任务栏",120,"secondary",ShowMenuRefresh);refresh.Margin=new Thickness(12,0,0,0);actions.Children.Add(refresh);}
      primary=Button(value.Ok?"打开设置":"重新安装",136,"primary",value.Ok?(Action)OpenSettings:(Action)StartInstall);primary.Margin=new Thickness(12,0,0,0);actions.Children.Add(primary);
      Capture(value.Ok?"安装完成":value.Canceled?"安装取消":"安装未完成");if(closeAfterCancel)Close();
    }
    private static string PhaseLabel(string phase){var labels=new Dictionary<string,string>{{"dictionary-download","在线词库更新"},{"cloud","云输入组件"},{"interface","界面配置"}};return labels.ContainsKey(phase)?labels[phase]:phase;}
    private static bool NativeMenuRefreshPending(string user){try{var values=InstallFiles.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(IOPath.Combine(user,".mason-native-ui.json"),Encoding.UTF8));object pending;return values.TryGetValue("pending_host_refresh",out pending) && Convert.ToBoolean(pending);}catch{return false;}}
    private void ShowMenuRefresh(){
      overlay.Children.Clear();overlay.Visibility=Visibility.Visible;overlay.Children.Add(new Border {Background=Brushes.Black,Opacity=.24});var column=Column();column.Children.Add(T("刷新任务栏",20,"text",true));
      var note=T("刷新会短暂重新加载桌面和任务栏，资源管理器窗口可能关闭。已打开的程序若仍显示旧菜单，需要重新打开程序，或保存工作后重新登录。",14,"muted",false);note.Margin=new Thickness(0,12,0,20);column.Children.Add(note);
      var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};column.Children.Add(actions);actions.Children.Add(Button("稍后",88,"secondary",HideAttention));
      var refresh=Button("刷新",88,"primary",delegate{
        string script=MasonPaths.RuntimeFile(result.AppDir,"scripts","_refresh_taskbar.ps1"),powershell=IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        try{Process.Start(new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+InstallFiles.Quote(script)+" -RestartExplorer"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});HideAttention();}catch(Exception error){note.Text=error.Message;}
      });refresh.Margin=new Thickness(12,0,0,0);actions.Children.Add(refresh);
      overlay.Children.Add(new Border {Width=470,Background=B("surface"),CornerRadius=new CornerRadius(22),Padding=new Thickness(24),Child=column,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center});refresh.Focus();
    }
    private void StartInstall(){
      if(busy)return;
      if(address!=null){try{Plan.AppDir=InstallFiles.Full(address.Text.Trim());if(InstallFiles.Same(Plan.AppDir,IOPath.GetPathRoot(Plan.AppDir)))throw new Exception("请选择专用安装文件夹，例如 D:\\Mason。");}catch(Exception error){ShowAddressError(error.Message);return;}}
      seenLines=0;phase="";closeAfterCancel=false;watch.Restart();
      try {
        Plan.UserDir=MasonPaths.UserData(Plan.AppDir);Plan.LogDir=MasonPaths.Logs(Plan.AppDir);Plan.BackupRoot=MasonPaths.Backups(Plan.AppDir);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6);
        logDir=IOPath.Combine(Plan.LogDir,"安装记录",stamp);
        try{Directory.CreateDirectory(logDir);}catch(UnauthorizedAccessException){logDir=IOPath.Combine(IOPath.GetTempPath(),"Mason-install-"+stamp);Directory.CreateDirectory(logDir);}
        Plan.RunDir=logDir;InstallFiles.AtomicText(IOPath.Combine(logDir,"plan.json"),InstallFiles.Json.Serialize(Plan));
        string exe=IOPath.Combine(Plan.SourceDir,"Mason.Install.exe");bool admin=new WindowsInstallPlatform().IsAdministrator;
        var start=new ProcessStartInfo(exe,"--worker "+InstallFiles.Quote(logDir)){UseShellExecute=!admin,WindowStyle=ProcessWindowStyle.Hidden};if(admin)start.CreateNoWindow=true;else start.Verb="runas";
        worker=Process.Start(start);busy=true;ShowProgress(new ProgressEvent {Phase="prepare",Title="正在准备安装",Message="正在检查安装材料",OperationId="starting",CanCancel=false});
        poll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(150)};poll.Tick+=delegate{PollWorker();};poll.Start();
      }catch(Win32Exception error){ShowResult(new InstallResult {Canceled=error.NativeErrorCode==1223,Message=error.NativeErrorCode==1223?"管理员确认已取消，可以重新开始安装。":error.Message,AppDir=Plan.AppDir,UserDir=Plan.UserDir});}
      catch(Exception error){ShowResult(new InstallResult {Message=error.Message,AppDir=Plan.AppDir,UserDir=Plan.UserDir});}
    }
    private void ShowAddressError(string message){overlay.Children.Clear();overlay.Visibility=Visibility.Visible;overlay.Children.Add(new Border {Background=Brushes.Black,Opacity=.24});var content=Column();content.Children.Add(T("请检查安装地址",20,"text",true));var reason=T(message,14,"muted",false);reason.Margin=new Thickness(0,12,0,20);content.Children.Add(reason);var button=Button("知道了",100,"primary",HideAttention);button.HorizontalAlignment=HorizontalAlignment.Right;content.Children.Add(button);overlay.Children.Add(new Border {Width=450,Background=B("surface"),CornerRadius=new CornerRadius(22),Padding=new Thickness(24),Child=content,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center});button.Focus();}
    private void PollWorker(){
      try {string events=IOPath.Combine(logDir,"events.jsonl");if(File.Exists(events)){string text;using(var stream=new FileStream(events,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(stream,Encoding.UTF8))text=reader.ReadToEnd();var lines=text.Split('\n');for(int i=seenLines;i<lines.Length-1;i++){if(String.IsNullOrWhiteSpace(lines[i]))continue;ShowProgress(InstallFiles.Json.Deserialize<ProgressEvent>(lines[i]));}seenLines=lines.Length-1;}
        string resultFile=IOPath.Combine(logDir,"result.json");if(File.Exists(resultFile)){ShowResult(InstallFiles.Json.Deserialize<InstallResult>(File.ReadAllText(resultFile,Encoding.UTF8)));return;}
        if(worker!=null && worker.HasExited)ShowResult(new InstallResult {Message="安装进程已退出，尚未收到完成结果。请重新安装，已生成的备份会保留。",AppDir=Plan.AppDir,UserDir=Plan.UserDir});else if(elapsed!=null)elapsed.Text="已用时 "+Time(watch.Elapsed);
      }catch(IOException){}catch(Exception error){if(description!=null)description.Text="正在读取安装状态："+error.Message;}
    }
    private void Send(string action){if(current==null || String.IsNullOrWhiteSpace(logDir))return;InstallFiles.AtomicText(IOPath.Combine(logDir,"control.json"),InstallFiles.Json.Serialize(new ControlMessage {OperationId=current.OperationId,Action=action,Nonce=Guid.NewGuid().ToString("N")}));}
    private void OnClosing(object sender,CancelEventArgs e){if(!busy)return;e.Cancel=true;if(current!=null && current.CanCancel){closeAfterCancel=true;Send("cancel");}else if(description!=null)description.Text="当前正在写入或注册，完成此步骤后可以关闭。";}
    private void OpenSettings(){string path=MasonPaths.RuntimeFile(result.AppDir,"ui","Mason.Settings.exe");try{Process.Start(new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=IOPath.GetDirectoryName(path)});}catch(Exception error){description.Text=error.Message;}}
    public void Capture(string name){
      if(String.IsNullOrWhiteSpace(captureDir) || !captures.Add(name))return;Directory.CreateDirectory(captureDir);body.BeginAnimation(OpacityProperty,null);body.Opacity=1;shell.UpdateLayout();if(progress!=null && current!=null && current.Percent.HasValue){progress.BeginAnimation(InstallProgressBar.ValueProperty,null);progress.Value=current.Percent.Value/100;}
      int width=(int)Math.Ceiling(shell.ActualWidth),height=(int)Math.Ceiling(shell.ActualHeight);if(width<=0 || height<=0){captures.Remove(name);return;}var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(IOPath.Combine(captureDir,name+".png")))encoder.Save(file);
    }
    private static string Bytes(long bytes){if(bytes<1024)return bytes+" B";if(bytes<1024*1024)return (bytes/1024.0).ToString("0.0")+" KB";if(bytes<1024L*1024*1024)return (bytes/1048576.0).ToString("0.0")+" MB";return (bytes/(1024.0*1024*1024)).ToString("0.00")+" GB";}
    private static string Time(TimeSpan value){return value.TotalMinutes<1?(int)value.TotalSeconds+" 秒":(int)value.TotalMinutes+" 分 "+value.Seconds+" 秒";}
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int length);
    private void SetNativeTheme(){try{int value=theme=="dark" || (theme=="system" && dark)?1:0;IntPtr hwnd=new System.Windows.Interop.WindowInteropHelper(this).Handle;DwmSetWindowAttribute(hwnd,20,ref value,4);int corner=2;DwmSetWindowAttribute(hwnd,33,ref corner,4);}catch{}}
  }
  public static class InstallerProgram {
    [STAThread]public static int Main(string[] args){
      if(args.Length==2 && args[0]=="--worker")return Worker(args[1]);
      try{var app=new Application {ShutdownMode=ShutdownMode.OnMainWindowClose};app.Run(new InstallerWindow(args));return 0;}catch(Exception error){System.Windows.MessageBox.Show(error.Message,"麻绳输入法 · 安装");return 1;}
    }
    private static int Worker(string runDir){
      try {runDir=InstallFiles.Full(runDir);var plan=InstallFiles.Json.Deserialize<InstallPlan>(File.ReadAllText(IOPath.Combine(runDir,"plan.json"),Encoding.UTF8));
        if(!InstallFiles.Same(plan.SourceDir,AppDomain.CurrentDomain.BaseDirectory) || !InstallFiles.Same(plan.RunDir,runDir))throw new InvalidDataException("安装任务来源不匹配。");
        InstallResult result;
        using(var stream=new FileStream(IOPath.Combine(runDir,"events.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)) {AutoFlush=true}){
          var install=new CoreInstaller(plan,new WindowsInstallPlatform(),delegate(ProgressEvent value){lock(writer){writer.WriteLine(InstallFiles.Json.Serialize(value));}},delegate{string path=IOPath.Combine(runDir,"control.json");try{return File.Exists(path)?InstallFiles.Json.Deserialize<ControlMessage>(File.ReadAllText(path,Encoding.UTF8)):null;}catch{return null;}});
          result=install.Run();InstallFiles.AtomicText(IOPath.Combine(runDir,"result.json"),InstallFiles.Json.Serialize(result));
        }
        if(result.Ok){string target=IOPath.Combine(MasonPaths.Logs(plan.AppDir),"安装记录",IOPath.GetFileName(runDir));if(!InstallFiles.Same(runDir,target)){Directory.CreateDirectory(target);foreach(string file in Directory.GetFiles(runDir,"*.json").Concat(Directory.GetFiles(runDir,"*.jsonl"))){string saved=IOPath.Combine(target,IOPath.GetFileName(file));InstallFiles.AtomicText(saved,File.ReadAllText(file,Encoding.UTF8));}}}
        return result.Ok?0:result.Canceled?2:1;
      }catch(Exception error){try{InstallFiles.AtomicText(IOPath.Combine(runDir,"result.json"),InstallFiles.Json.Serialize(new InstallResult {Message=error.Message}));}catch{}return 1;}
    }
  }
}
