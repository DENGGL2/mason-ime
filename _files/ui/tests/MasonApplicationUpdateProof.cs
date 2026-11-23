using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mason.Install;

public static class MasonApplicationUpdateProof {
  sealed class Platform : IInstallPlatform,IInstallLayoutPlatform {
    public string Engine;
    public bool Running=true;
    public bool IsAdministrator {get{return true;}}
    public string CurrentUserSid {get{return "Mason.ApplicationUpdate.Proof";}}
    public string FindEngine(){return Engine;}
    public bool EngineUsable(string path){return path==Engine;}
    public void InstallEngine(string path,InstallPlan plan,Action tick){throw new Exception("Existing engine must be reused.");}
    public void RegisterEngine(InstallPlan plan,Action tick){throw new Exception("Existing input profiles must not be registered again.");}
    public void StopEngine(string path,Action tick){Running=false;}
    public void ConfigureInterface(InstallPlan plan,Action tick){}
    public void Deploy(InstallPlan plan,Action tick){}
    public void Publish(InstallPlan plan,string backup,string[] skipped){}
    public void StartEngine(string path,Action tick){Running=true;}
    public bool EngineRunning(string path){return Running;}
    public void MigrateLayout(InstallPlan plan,Action<ProgressEvent> progress,Action tick){plan.LayoutMigrated=true;}
    public bool LayoutReady(InstallPlan plan){return plan.LayoutMigrated;}
  }
  static void Text(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,text,new UTF8Encoding(false));}
  static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
  public static int Main(string[] args){
    if(args.Length!=1)return 2;string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
    try{
      string source=Path.Combine(root,"安装包"),target=Path.Combine(root,"Mason"),user=Path.Combine(target,"用户数据");
      var payload=new Dictionary<string,string>{{"_files/ui/Mason.Settings.exe","new application"},{"_files/scripts/fixture.py","new backend"},{"_files/rime/rime_frost.schema.yaml","new bundled schema"},{"_files/rime/rime_frost.dict.yaml","new bundled dictionary"},{"_files/rime/default.custom.yaml","patch:\n  schema_list:\n    - schema: rime_frost\n"},{"_files/rime/rime_frost.custom.yaml","patch:\n  switches/@1/reset: 0\n"},{"_files/rime/rime.lua","new lua loader"},{"_files/rime/lua/fixture.lua","new lua extension"}};
      foreach(var entry in payload)Text(Path.Combine(source,entry.Key.Replace('/',Path.DirectorySeparatorChar)),entry.Value);
      var entries=payload.Keys.Select(name=>{string path=Path.Combine(source,name.Replace('/',Path.DirectorySeparatorChar));return new ManifestEntry {Path=name,Bytes=new FileInfo(path).Length,Sha256=InstallFiles.Hash(path)};}).ToArray();
      Text(Path.Combine(source,"_files/install-manifest.json"),InstallFiles.Json.Serialize(new InstallManifest {Product="Mason",Version=InstallFiles.ProductVersion,Files=entries}));
      var personal=new Dictionary<string,string>{{"default.custom.yaml","patch:\n  schema_list:\n    - schema: rime_frost\n  menu/page_size: 8\n"},{"rime_frost.custom.yaml","patch:\n  switches/@1/reset: 1\n  mason_learning/flexible: true\n"},{"weasel.custom.yaml","patch:\n  style/font_point: 18\n  style/color_scheme: mini_graphite_dark\n"},{"user.yaml","var:\n  previously_selected_schema: rime_frost\n  personal_value: preserve\n"},{"custom_phrase.txt","我的习惯\twode\t100\n"},{"mason-learning.tsv","shh\t时候\t2\n"},{"rime_frost.dict.yaml","# current online dictionary\n---\nname: rime_frost\nversion: 1.0.5\n...\n"},{"rime_frost.schema.yaml","personal schema overrides"}};
      foreach(var entry in personal)Text(Path.Combine(user,entry.Key),entry.Value);
      Text(Path.Combine(user,"rime.lua"),"old lua loader");
      Text(Path.Combine(user,"build/rime_frost.prism.bin"),"prism");Text(Path.Combine(user,"build/rime_frost.table.bin"),"table");
      Text(Path.Combine(user,"build/rime_frost.schema.yaml"),"schema_id: rime_frost\nname: 麻绳输入法\nswitches:\n  - name: traditionalization\n    reset: 1\n");Text(Path.Combine(user,"build/default.yaml"),"schema_list:\n  - schema: rime_frost\n");
      string engine=Path.Combine(target,"程序");Directory.CreateDirectory(engine);var platform=new Platform {Engine=engine};
      var plan=new InstallPlan {SourceDir=source,AppDir=target,EngineDir=engine,PreviousEngineDir=engine,UserDir=user,PreviousUserDir=user,UserSid=platform.CurrentUserSid,RunDir=Path.Combine(root,"运行记录"),Mode="Hant",ExistingVersion="2.4.8",ExistingComplete=true,ApplicationUpdate=true,UpdateDictionary=false,InstallCloud=false,EngineRegistered=true};
      var result=new CoreInstaller(plan,platform,delegate(ProgressEvent progress){},delegate{return null;}).Run();Require(result.Ok,result.Message);
      foreach(var entry in personal)Require(File.ReadAllText(Path.Combine(user,entry.Key),Encoding.UTF8)==entry.Value,"User content changed: "+entry.Key);
      Require(File.ReadAllText(Path.Combine(user,"rime.lua"))=="new lua loader","Updated runtime extension was not applied.");
      Require(File.ReadAllText(Path.Combine(target,"程序/资源/ui/Mason.Settings.exe"))=="new application","New application was not copied to the existing path.");
      Require(Directory.Exists(result.BackupDir),"Recovery backup missing.");
      Text(Path.Combine(root,"result.json"),InstallFiles.Json.Serialize(new{status="passed",existing_engine_reused=true,registration_unchanged=true,personal_files_preserved=personal.Count,traditional_mode_preserved=true,existing_installation_path=true,backup_created=true}));return 0;
    }catch(Exception error){Text(Path.Combine(root,"error.txt"),error.ToString());return 1;}
  }
}
