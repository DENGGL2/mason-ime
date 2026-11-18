using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Mason.Common;
using Mason.Input;
using Microsoft.Win32;

[assembly: AssemblyTitle("麻绳输入法 · 系统托盘")]
[assembly: AssemblyProduct("Mason")]
[assembly: AssemblyVersion("2.4.4.0")]
[assembly: AssemblyFileVersion("2.4.4.0")]

namespace Mason.Tray {
  public sealed class TrayMenu : ContextMenuStrip {
    public static readonly string[][] Switches={
      new[]{"切换中 / 英","ascii_mode","中文","英文"},new[]{"切换简 / 繁","traditionalization","简体","繁体"},
      new[]{"切换半 / 全角","full_shape","半角","全角"},new[]{"翻译开 / 关","chinese_english","关闭","开启"}
    };
    private readonly ModeStore modes;
    private readonly Action<string> launch;
    private readonly Dictionary<string,ToolStripMenuItem> toggles=new Dictionary<string,ToolStripMenuItem>();
    public bool Dark {get;private set;}
    public TrayMenu(ModeStore store,Action<string> execute){
      modes=store;launch=execute;ShowImageMargin=false;ShowCheckMargin=false;Font=new Font("Microsoft YaHei UI",10);Padding=new Padding(6);AutoSize=true;
      foreach(var definition in Switches){string name=definition[1];var item=Item(definition[0],delegate{try{modes.Toggle(name);RefreshState();}catch(Exception error){MessageBox.Show(error.Message,"麻绳输入法",MessageBoxButtons.OK,MessageBoxIcon.Information);}});toggles[name]=item;Items.Add(item);}
      Items.Add(new ToolStripSeparator {Margin=new Padding(8,5,8,5)});
      Items.Add(Item("设置",delegate{launch("");}));Items.Add(Item("更新词库",delegate{launch("--update-dictionary");}));Items.Add(Item("检查更新",delegate{launch("--check-updates");}));
      Opening+=delegate{RefreshState();};ApplyTheme(false);
    }
    private ToolStripMenuItem Item(string text,Action action){var item=new ToolStripMenuItem(text) {AutoSize=false,Width=242,Height=36,Padding=new Padding(10,4,10,4)};item.Click+=delegate{action();};return item;}
    public void ApplyTheme(bool dark){Dark=dark;BackColor=dark?Color.FromArgb(36,36,36):Color.FromArgb(249,249,249);ForeColor=dark?Color.FromArgb(239,239,239):Color.FromArgb(32,32,32);Renderer=new MenuRenderer(dark);foreach(ToolStripItem item in Items){item.ForeColor=ForeColor;item.BackColor=BackColor;}Invalidate();}
    public void RefreshState(){var state=modes.Read();foreach(var definition in Switches)toggles[definition[1]].ShortcutKeyDisplayString=definition[state.Values[definition[1]]?3:2];}
  }
  public sealed class MenuRenderer : ToolStripProfessionalRenderer {
    private readonly bool dark;
    public MenuRenderer(bool value){dark=value;RoundedEdges=false;}
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e){e.Graphics.Clear(dark?Color.FromArgb(36,36,36):Color.FromArgb(249,249,249));}
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e){
      if(!e.Item.Selected)return;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
      RectangleF r=new RectangleF(2,1,e.Item.Width-4,e.Item.Height-2);using(var path=Round(r,12))using(var brush=new SolidBrush(dark?Color.FromArgb(69,69,69):Color.FromArgb(229,229,229)))e.Graphics.FillPath(brush,path);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){using(var pen=new Pen(dark?Color.FromArgb(66,66,66):Color.FromArgb(224,224,224)))e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);}
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e){using(var pen=new Pen(dark?Color.FromArgb(66,66,66):Color.FromArgb(224,224,224)))e.Graphics.DrawLine(pen,8,e.Item.Height/2,e.Item.Width-8,e.Item.Height/2);}
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){e.TextColor=dark?Color.FromArgb(239,239,239):Color.FromArgb(32,32,32);base.OnRenderItemText(e);}
    private static GraphicsPath Round(RectangleF r,float radius){float d=radius*2;var p=new GraphicsPath();p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
  }
  sealed class TrayContext : ApplicationContext {
    private readonly string engine,app,user,pipeName;
    private readonly NotifyIcon icon;
    private readonly TrayMenu menu;
    private readonly Control dispatch=new Control();
    private readonly System.Windows.Forms.Timer watch=new System.Windows.Forms.Timer();
    private readonly Stopwatch life=Stopwatch.StartNew();
    private volatile bool closing;
    private NamedPipeServerStream pending;
    public TrayContext(string directory,string pipe){
      engine=Path.GetFullPath(directory);pipeName=pipe;
      var info=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-runtime.json"),Encoding.UTF8));
      app=Path.GetFullPath(Convert.ToString(info["app_dir"]));user=Path.GetFullPath(Convert.ToString(info["user_dir"]));
      if(Convert.ToString(info["product"])!="Mason" || !Same(engine,MasonPaths.Program(app)) || !Same(user,MasonPaths.UserData(app)))throw new InvalidDataException("托盘运行目录不匹配。");
      dispatch.CreateControl();var handle=dispatch.Handle;
      menu=new TrayMenu(new ModeStore(user),LaunchSettings);
      icon=new NotifyIcon {Icon=new Icon(MasonPaths.RuntimeFile(app,"ui","Mason.ico")),Text="麻绳输入法 · Mason",ContextMenuStrip=menu,Visible=true};
      icon.DoubleClick+=delegate{LaunchSettings("");};menu.Opening+=delegate{menu.ApplyTheme(ReadDark());};
      SystemEvents.UserPreferenceChanged+=PreferenceChanged;watch.Interval=1000;watch.Tick+=delegate{if(life.ElapsedMilliseconds>5000 && !NativeRunning())ExitThread();};watch.Start();
      var listener=new Thread(Listen) {IsBackground=true};listener.Start();
    }
    private static bool Same(string first,string second){return String.Equals(Path.GetFullPath(first).TrimEnd('\\'),Path.GetFullPath(second).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);}
    private bool NativeRunning(){foreach(var process in Process.GetProcessesByName("WeaselServer"))using(process){try{if(Same(process.MainModule.FileName,Path.Combine(engine,"WeaselServer.exe")) && !process.HasExited)return true;}catch{}}return false;}
    private void LaunchSettings(string arguments){try{string file=MasonPaths.RuntimeFile(app,"ui","Mason.Settings.exe");Process.Start(new ProcessStartInfo(file,arguments) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(file)});}catch(Exception error){MessageBox.Show(error.Message,"麻绳输入法",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
    private bool ReadDark(){
      try{string text=File.ReadAllText(Path.Combine(user,"weasel.custom.yaml"),Encoding.UTF8);if(text.Contains("# rime-mini-ui: dark"))return true;if(text.Contains("# rime-mini-ui: light"))return false;}catch{}
      using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))return key!=null && Convert.ToInt32(key.GetValue("AppsUseLightTheme",1))==0;
    }
    private void PreferenceChanged(object sender,UserPreferenceChangedEventArgs e){if(!closing && dispatch.IsHandleCreated)dispatch.BeginInvoke(new Action(delegate{menu.ApplyTheme(ReadDark());}));}
    private void Listen(){while(!closing){try{using(var stream=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.None)){pending=stream;stream.WaitForConnection();string command;using(var reader=new StreamReader(stream,Encoding.UTF8))command=reader.ReadLine();pending=null;if(command=="stop" && !closing)dispatch.BeginInvoke(new Action(ExitThread));}}catch(IOException){}catch(ObjectDisposedException){}}}
    protected override void ExitThreadCore(){if(closing)return;closing=true;watch.Stop();SystemEvents.UserPreferenceChanged-=PreferenceChanged;icon.Visible=false;icon.Dispose();menu.Dispose();if(pending!=null)try{pending.Dispose();}catch{}dispatch.Dispose();base.ExitThreadCore();}
  }
  public static class Program {
    public static string Identity(string engine){string value=WindowsIdentity.GetCurrent().User.Value+"|"+Path.GetFullPath(engine).ToLowerInvariant();using(var hash=System.Security.Cryptography.SHA256.Create())return "Mason.Tray."+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,32);}
    public static bool Stop(string engine){try{using(var pipe=new NamedPipeClientStream(".",Identity(engine),PipeDirection.Out)){pipe.Connect(1200);using(var writer=new StreamWriter(pipe,new UTF8Encoding(false)) {AutoFlush=true})writer.WriteLine("stop");}return true;}catch(IOException){return false;}catch(TimeoutException){return false;}}
    [STAThread]public static int Main(string[] args){
      if(args.Length==2 && args[0]=="--stop"){Stop(args[1]);return 0;}
      if(args.Length!=1)return 1;string engine=Path.GetFullPath(args[0]);
      try{bool created;using(var mutex=new Mutex(true,@"Local\"+Identity(engine),out created)){if(!created)return 0;try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new TrayContext(engine,Identity(engine)));return 0;}finally{mutex.ReleaseMutex();}}}
      catch(Exception error){try{var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(engine,"mason-runtime.json"),Encoding.UTF8));string logs=Convert.ToString(config["log_dir"]);Directory.CreateDirectory(logs);File.AppendAllText(Path.Combine(logs,"mason-tray.log"),DateTime.Now.ToString("s")+" "+error+Environment.NewLine,Encoding.UTF8);}catch{}return 1;}
    }
  }
}
