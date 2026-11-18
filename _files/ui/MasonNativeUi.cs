using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml;

namespace Mason.NativeUi {
  public sealed class MenuItem {
    public ushort Flags,Id;
    public string Text;
    public List<MenuItem> Children;
  }
  public sealed class MenuResource {
    public ushort Name,Language;
    public byte[] Prefix;
    public List<MenuItem> Items;
    public ushort[] CommandIds;
    public string[] Commands;
  }
  public static class Resources {
    // These are existing Weasel commands, verified against both TSF dispatchers
    // and the server command table. No unsupported command IDs are introduced.
    public static readonly ushort[] MenuCommandIds={40010,40012,40009,40004,40008,40005,40003};
    private static readonly string[][] MenuLinks={
      new[]{"https://rime.im/docs/","mason:full-shape"},
      new[]{"https://rime.im/discuss/","mason:translation"},
      new[]{"https://rime.im/","mason:dict"}
    };
    public static Dictionary<ushort,byte[]> Manifests(string path){
      var result=new Dictionary<ushort,byte[]>();IntPtr module=LoadLibraryExW(path,IntPtr.Zero,0x22);if(module==IntPtr.Zero)throw Error("无法读取引擎清单。");
      EnumLanguage callback=delegate(IntPtr m,IntPtr t,IntPtr n,ushort language,IntPtr state){result[language]=ReadRaw(m,24,1,language);return true;};
      try{EnumResourceLanguagesW(module,new IntPtr(24),new IntPtr(1),callback,IntPtr.Zero);if(result.Count==0)throw new InvalidDataException("引擎缺少兼容性清单。");return result;}finally{FreeLibrary(module);GC.KeepAlive(callback);}
    }
    private static XmlDocument ManifestDocument(byte[] bytes){var doc=new XmlDocument {PreserveWhitespace=true};doc.LoadXml(Encoding.UTF8.GetString(bytes).Trim('\ufeff','\0'));return doc;}
    public static bool HasUtf8Manifest(string path){return Manifests(path).Values.All(bytes=>{var doc=ManifestDocument(bytes);return doc.GetElementsByTagName("activeCodePage","http://schemas.microsoft.com/SMI/2019/WindowsSettings").Cast<XmlNode>().Any(node=>node.InnerText=="UTF-8");});}
    public static bool EnsureUtf8Manifest(string path,string backupDir){
      if(HasUtf8Manifest(path))return false;string code=SectionHash(path,".text"),constants=SectionHash(path,".rdata");var manifests=Manifests(path);var replacements=new Dictionary<ushort,byte[]>();
      foreach(var pair in manifests){var doc=ManifestDocument(pair.Value);var manager=new XmlNamespaceManager(doc.NameTable);manager.AddNamespace("app","urn:schemas-microsoft-com:asm.v3");var application=doc.SelectSingleNode("//app:application",manager);if(application==null){application=doc.CreateElement("application","urn:schemas-microsoft-com:asm.v3");doc.DocumentElement.AppendChild(application);}var settings=application.SelectSingleNode("app:windowsSettings",manager);if(settings==null){settings=doc.CreateElement("windowsSettings","urn:schemas-microsoft-com:asm.v3");application.AppendChild(settings);}var page=doc.GetElementsByTagName("activeCodePage","http://schemas.microsoft.com/SMI/2019/WindowsSettings").Cast<XmlNode>().FirstOrDefault();if(page==null){page=doc.CreateElement("activeCodePage","http://schemas.microsoft.com/SMI/2019/WindowsSettings");settings.AppendChild(page);}page.InnerText="UTF-8";using(var stream=new MemoryStream()){using(var writer=XmlWriter.Create(stream,new XmlWriterSettings {Encoding=new UTF8Encoding(false),CloseOutput=false}))doc.Save(writer);replacements[pair.Key]=stream.ToArray();}}
      Directory.CreateDirectory(backupDir);string saved=Path.Combine(backupDir,Path.GetFileName(path));if(File.Exists(saved))throw new IOException("兼容性备份已存在，原程序未修改。");File.Copy(path,saved);
      IntPtr update=BeginUpdateResourceW(path,false);if(update==IntPtr.Zero)throw Error("无法准备中文目录兼容性更新。");bool committed=false;
      try{foreach(var pair in replacements)if(!UpdateResourceW(update,new IntPtr(24),new IntPtr(1),pair.Key,pair.Value,(uint)pair.Value.Length))throw Error("无法更新中文目录兼容性。");if(!EndUpdateResourceW(update,false))throw Error("无法提交中文目录兼容性。");committed=true;}finally{if(!committed)EndUpdateResourceW(update,true);}
      if(code!=SectionHash(path,".text") || constants!=SectionHash(path,".rdata") || !HasUtf8Manifest(path)){File.Copy(saved,path,true);throw new InvalidDataException("中文目录兼容性验证未通过，原程序已恢复。");}return true;
    }
    private delegate bool EnumName(IntPtr module,IntPtr type,IntPtr name,IntPtr state);
    private delegate bool EnumLanguage(IntPtr module,IntPtr type,IntPtr name,ushort language,IntPtr state);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryExW(string file,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll")]static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool EnumResourceNamesW(IntPtr module,IntPtr type,EnumName callback,IntPtr state);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool EnumResourceLanguagesW(IntPtr module,IntPtr type,IntPtr name,EnumLanguage callback,IntPtr state);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr FindResourceExW(IntPtr module,IntPtr type,IntPtr name,ushort language);
    [DllImport("kernel32.dll")]static extern uint SizeofResource(IntPtr module,IntPtr resource);
    [DllImport("kernel32.dll")]static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
    [DllImport("kernel32.dll")]static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr BeginUpdateResourceW(string file,bool deleteExisting);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool UpdateResourceW(IntPtr update,IntPtr type,IntPtr name,ushort language,byte[] data,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool EndUpdateResourceW(IntPtr update,bool discard);
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr LoadMenuIndirectW(byte[] data);
    [DllImport("user32.dll")]static extern bool DestroyMenu(IntPtr menu);
    private static Exception Error(string message){return new Win32Exception(Marshal.GetLastWin32Error(),message);}
    public static byte[] ReadRaw(IntPtr module,int type,int name,ushort language){
      IntPtr resource=FindResourceExW(module,new IntPtr(type),new IntPtr(name),language);if(resource==IntPtr.Zero)return null;
      uint length=SizeofResource(module,resource);if(length>4*1024*1024)throw new InvalidDataException("资源大小不符合预期。");
      var bytes=new byte[length];IntPtr pointer=LockResource(LoadResource(module,resource));if(pointer==IntPtr.Zero)throw Error("无法读取界面资源。");Marshal.Copy(pointer,bytes,0,bytes.Length);return bytes;
    }
    private static string ReadText(BinaryReader reader){var text=new StringBuilder();ushort c;while((c=reader.ReadUInt16())!=0)text.Append((char)c);return text.ToString();}
    private static List<MenuItem> ReadItems(BinaryReader reader,int depth){
      if(depth>12)throw new InvalidDataException("菜单层级无效。");var items=new List<MenuItem>();bool last=false;
      while(!last){var item=new MenuItem {Flags=reader.ReadUInt16()};if((item.Flags&0x10)==0)item.Id=reader.ReadUInt16();item.Text=ReadText(reader);if((item.Flags&0x10)!=0)item.Children=ReadItems(reader,depth+1);items.Add(item);last=(item.Flags&0x80)!=0;}return items;
    }
    private static void Collect(List<MenuItem> items,List<ushort> ids,List<string> texts){foreach(var item in items)if(item.Children!=null)Collect(item.Children,ids,texts);else if(item.Id!=0){ids.Add(item.Id);texts.Add(item.Text);}}
    public static MenuResource[] Read(string file){
      IntPtr module=LoadLibraryExW(file,IntPtr.Zero,0x22);if(module==IntPtr.Zero)throw Error("无法读取输入法界面资源。");var result=new List<MenuResource>();Exception failure=null;
      EnumName nameCallback=delegate(IntPtr mod,IntPtr type,IntPtr name,IntPtr state){
        if(name.ToInt64()<1 || name.ToInt64()>65535)return true;
        EnumLanguage languageCallback=delegate(IntPtr m,IntPtr t,IntPtr n,ushort lang,IntPtr data){try{
          byte[] bytes=ReadRaw(m,4,(int)n.ToInt64(),lang);using(var reader=new BinaryReader(new MemoryStream(bytes))){ushort version=reader.ReadUInt16(),offset=reader.ReadUInt16();if(version!=0 || offset>bytes.Length-4)throw new InvalidDataException("菜单格式不受支持。");var prefix=new byte[4+offset];Array.Copy(bytes,prefix,prefix.Length);reader.BaseStream.Position=prefix.Length;var items=ReadItems(reader,0);var ids=new List<ushort>();var labels=new List<string>();Collect(items,ids,labels);
            if(ids.Contains(40008) && ids.Contains(40003))result.Add(new MenuResource {Name=(ushort)n.ToInt64(),Language=lang,Prefix=prefix,Items=items,CommandIds=ids.ToArray(),Commands=labels.ToArray()});
          }return true;}catch(Exception error){failure=error;return false;}};
        bool success=EnumResourceLanguagesW(mod,type,name,languageCallback,IntPtr.Zero);GC.KeepAlive(languageCallback);return success && failure==null;
      };
      try{EnumResourceNamesW(module,new IntPtr(4),nameCallback,IntPtr.Zero);if(failure!=null)throw failure;return result.ToArray();}finally{FreeLibrary(module);GC.KeepAlive(nameCallback);}
    }
    private static List<MenuItem> Compact(List<MenuItem> source,bool english,bool traditional){
      string[] labels=english?new[]{"Switch Chinese / English","Switch simplified / traditional","Switch half / full width","Toggle translation","Settings","Update dictionary","Check for updates"}:
        traditional?new[]{"切換中 / 英","切換簡 / 繁","切換半 / 全形","翻譯開 / 關","設定","更新詞庫","檢查更新"}:
        new[]{"切换中 / 英","切换简 / 繁","切换半 / 全角","翻译开 / 关","设置","更新词库","检查更新"};
      var items=new List<MenuItem>();for(int i=0;i<MenuCommandIds.Length;i++){if(i==4)items.Add(new MenuItem {Id=0,Text="",Flags=0});items.Add(new MenuItem {Id=MenuCommandIds[i],Text=labels[i],Flags=0});}
      var root=source.FirstOrDefault(item=>item.Children!=null);
      return root==null?items:new List<MenuItem>{new MenuItem {Flags=0x10,Text=english?"Mason":traditional?"麻繩輸入法":"麻绳输入法",Children=items}};
    }
    private static void WriteItems(BinaryWriter writer,List<MenuItem> items){for(int i=0;i<items.Count;i++){var item=items[i];ushort flags=(ushort)(item.Flags&~0x80);if(i==items.Count-1)flags|=0x80;writer.Write(flags);if(item.Children==null)writer.Write(item.Id);foreach(char c in item.Text)writer.Write((ushort)c);writer.Write((ushort)0);if(item.Children!=null)WriteItems(writer,item.Children);}}
    private static byte[] MenuBytes(MenuResource menu){
      bool chinese=menu.Commands.Any(x=>x.Any(c=>c>=0x3400 && c<=0x9fff));bool traditional=menu.Language==1028 || menu.Name==108 || menu.Commands.Any(x=>x.Contains("輸") || x.Contains("設定"));
      using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){writer.Write(menu.Prefix);WriteItems(writer,Compact(menu.Items,!chinese,traditional));byte[] bytes=stream.ToArray();IntPtr native=LoadMenuIndirectW(bytes);if(native==IntPtr.Zero)throw Error("精简菜单校验失败。");DestroyMenu(native);return bytes;}
    }
    private sealed class IconPart {public byte Width,Height,Colors;public ushort Planes,Bits;public byte[] Data;}
    private static IconPart[] IconParts(string path){
      byte[] bytes=File.ReadAllBytes(path);using(var reader=new BinaryReader(new MemoryStream(bytes))){if(reader.ReadUInt16()!=0 || reader.ReadUInt16()!=1)throw new InvalidDataException("M 图标格式无效。");int count=reader.ReadUInt16();if(count<1 || count>12)throw new InvalidDataException("M 图标尺寸数量无效。");var result=new List<IconPart>();for(int i=0;i<count;i++){var icon=new IconPart {Width=reader.ReadByte(),Height=reader.ReadByte(),Colors=reader.ReadByte()};reader.ReadByte();icon.Planes=reader.ReadUInt16();icon.Bits=reader.ReadUInt16();int length=reader.ReadInt32(),offset=reader.ReadInt32();if(length<=0 || length>1024*1024 || offset<6+16*count || offset>bytes.Length-length)throw new InvalidDataException("M 图标内容无效。");icon.Data=new byte[length];Array.Copy(bytes,offset,icon.Data,0,length);result.Add(icon);}return result.ToArray();}
    }
    private static byte[] IconGroup(IconPart[] parts){using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)parts.Length);for(int i=0;i<parts.Length;i++){var part=parts[i];writer.Write(part.Width);writer.Write(part.Height);writer.Write(part.Colors);writer.Write((byte)0);writer.Write(part.Planes);writer.Write(part.Bits);writer.Write(part.Data.Length);writer.Write((ushort)(0x7000+i));}return stream.ToArray();}}
    private static ushort[] IconLanguages(string path,int group){
      IntPtr module=LoadLibraryExW(path,IntPtr.Zero,0x22);if(module==IntPtr.Zero)throw Error("无法读取图标资源。");var languages=new List<ushort>();EnumLanguage callback=delegate(IntPtr m,IntPtr t,IntPtr n,ushort lang,IntPtr p){languages.Add(lang);return true;};try{EnumResourceLanguagesW(module,new IntPtr(14),new IntPtr(group),callback,IntPtr.Zero);return languages.ToArray();}finally{FreeLibrary(module);GC.KeepAlive(callback);}
    }
    public static bool HasMIcon(string path,string icon,int group){
      var parts=IconParts(icon);byte[] expected=IconGroup(parts);ushort[] languages=IconLanguages(path,group);if(languages.Length==0)return false;IntPtr module=LoadLibraryExW(path,IntPtr.Zero,0x22);try{foreach(ushort language in languages){byte[] bytes=ReadRaw(module,14,group,language);if(bytes==null || !bytes.SequenceEqual(expected))return false;for(int i=0;i<parts.Length;i++){bytes=ReadRaw(module,3,0x7000+i,language);if(bytes==null || !bytes.SequenceEqual(parts[i].Data))return false;}}return true;}finally{FreeLibrary(module);}
    }
    public static bool IsCompact(string path){var menus=Read(path);return menus.Length>0 && menus.All(x=>x.CommandIds.SequenceEqual(MenuCommandIds) && !x.Commands.Any(t=>t.Contains(" (&")));}
    private static byte[] SectionBytes(string path,string section,out int offset){
      byte[] data=File.ReadAllBytes(path);int pe=BitConverter.ToInt32(data,0x3c),count=BitConverter.ToUInt16(data,pe+6),header=pe+24+BitConverter.ToUInt16(data,pe+20);
      for(int i=0;i<count;i++){int at=header+40*i;if(Encoding.ASCII.GetString(data,at,8).TrimEnd('\0')!=section)continue;int length=BitConverter.ToInt32(data,at+16);offset=BitConverter.ToInt32(data,at+20);var bytes=new byte[length];Array.Copy(data,offset,bytes,0,length);return bytes;}throw new InvalidDataException("没有找到原生命令数据区。");
    }
    private static int[] TokenOffsets(byte[] bytes,byte[] token){var positions=new List<int>();for(int at=0;at<=bytes.Length-token.Length;at+=2){if(bytes[at]!=token[0])continue;bool same=true;for(int i=1;i<token.Length;i++)if(bytes[at+i]!=token[i]){same=false;break;}if(same)positions.Add(at);}return positions.ToArray();}
    private static int[] AsciiTokenOffsets(byte[] bytes,string token){byte[] value=Encoding.ASCII.GetBytes(token+"\0");var positions=new List<int>();for(int at=0;at<=bytes.Length-value.Length;at++){if(bytes[at]!=value[0])continue;bool same=true;for(int i=1;i<value.Length;i++)if(bytes[at+i]!=value[i]){same=false;break;}if(same)positions.Add(at);}return positions.ToArray();}
    private static byte[] LinkBytes(string[] link,bool target){byte[] original=Encoding.Unicode.GetBytes(link[0]+"\0");if(!target)return original;var bytes=new byte[original.Length];byte[] value=Encoding.Unicode.GetBytes(link[1]+"\0");if(value.Length>bytes.Length)throw new InvalidDataException("菜单入口长度无效。");Array.Copy(value,bytes,value.Length);return bytes;}
    private static byte[] NormalizeLinks(byte[] bytes){
      foreach(var link in MenuLinks){byte[] original=LinkBytes(link,false),target=LinkBytes(link,true);int[] old=TokenOffsets(bytes,original),current=TokenOffsets(bytes,target);if(old.Length+current.Length>1)throw new InvalidDataException("原生菜单链接数量不符合已验证的格式。");if(old.Length==1)Array.Copy(target,0,bytes,old[0],target.Length);}
      int[] oldImport=AsciiTokenOffsets(bytes,"WinSparkle.dll"),newImport=AsciiTokenOffsets(bytes,"Mason.Menu.dll");if(oldImport.Length+newImport.Length>1)throw new InvalidDataException("检查更新入口数量不符合预期。");if(oldImport.Length==1){byte[] name=Encoding.ASCII.GetBytes("Mason.Menu.dll\0");Array.Copy(name,0,bytes,oldImport[0],name.Length);}return bytes;
    }
    public static string MenuRouteHash(string path){int offset;byte[] bytes=NormalizeLinks(SectionBytes(path,".rdata",out offset));bytes=TextAdapter.CanonicalRoutes(path,bytes,offset);using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    public static bool HasMenuRoutes(string path){
      if(Read(path).Length==0)return true;int offset;byte[] bytes=SectionBytes(path,".rdata",out offset);int needed=Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)?3:2;
      bool links=MenuLinks.Take(needed).All(link=>TokenOffsets(bytes,LinkBytes(link,false)).Length==0 && TokenOffsets(bytes,LinkBytes(link,true)).Length==1);
      return links && (needed!=3 || (AsciiTokenOffsets(bytes,"WinSparkle.dll").Length==0 && AsciiTokenOffsets(bytes,"Mason.Menu.dll").Length==1));
    }
    private static void PatchMenuLinks(string path){
      if(Read(path).Length==0)return;int offset;byte[] before=SectionBytes(path,".rdata",out offset),after=NormalizeLinks((byte[])before.Clone());if(!before.SequenceEqual(after)){byte[] image=File.ReadAllBytes(path);Array.Copy(after,0,image,offset,after.Length);File.WriteAllBytes(path,image);}if(!HasMenuRoutes(path))throw new InvalidDataException("原生菜单动作连接失败。");
    }
    public static string SectionHash(string path,string section){
      byte[] data=File.ReadAllBytes(path);int pe=BitConverter.ToInt32(data,0x3c);if(pe<64 || BitConverter.ToUInt32(data,pe)!=0x4550)throw new InvalidDataException("程序格式无效。");int count=BitConverter.ToUInt16(data,pe+6),header=pe+24+BitConverter.ToUInt16(data,pe+20);
      for(int i=0;i<count;i++){int at=header+40*i;string name=Encoding.ASCII.GetString(data,at,8).TrimEnd('\0');if(name!=section)continue;int length=BitConverter.ToInt32(data,at+16),offset=BitConverter.ToInt32(data,at+20);using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(data,offset,length)).Replace("-","").ToLowerInvariant();}return null;
    }
    public static bool IsOwnedModule(string engine,string module){
      if(!File.Exists(module) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(module),@"^weasel(?:x64|ARM|ARM64|ARM64X)?\.(dll|ime)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return false;
      byte[] data=File.ReadAllBytes(module);int pe=BitConverter.ToInt32(data,0x3c);int machine=BitConverter.ToUInt16(data,pe+4);string prefix=machine==0x8664?"weaselx64":machine==0x14c?"weasel":null;if(prefix==null)return false;
      string source=Path.Combine(engine,prefix+Path.GetExtension(module));return File.Exists(source) && SectionHash(source,".text")==SectionHash(module,".text") && MenuRouteHash(source)==MenuRouteHash(module);
    }
    public static void Patch(string path,string icon,int group){
      if(IsCompact(path) && HasMenuRoutes(path) && HasMIcon(path,icon,group))return;
      string code=SectionHash(path,".text"),constants=MenuRouteHash(path);var menus=Read(path);var menuData=menus.Select(MenuBytes).ToArray();var parts=IconParts(icon);byte[] icons=IconGroup(parts);ushort[] languages=IconLanguages(path,group);
      if(languages.Length==0)throw new InvalidDataException("没有找到产品图标，原程序已保留。");
      IntPtr update=BeginUpdateResourceW(path,false);if(update==IntPtr.Zero)throw Error("无法准备界面资源更新。");bool committed=false;
      try{for(int i=0;i<menus.Length;i++)if(!UpdateResourceW(update,new IntPtr(4),new IntPtr(menus[i].Name),menus[i].Language,menuData[i],(uint)menuData[i].Length))throw Error("无法更新菜单。");
        foreach(ushort language in languages){for(int i=0;i<parts.Length;i++)if(!UpdateResourceW(update,new IntPtr(3),new IntPtr(0x7000+i),language,parts[i].Data,(uint)parts[i].Data.Length))throw Error("无法写入 M 图标。");if(!UpdateResourceW(update,new IntPtr(14),new IntPtr(group),language,icons,(uint)icons.Length))throw Error("无法连接 M 图标。");}
        if(!EndUpdateResourceW(update,false))throw Error("无法提交界面资源。");committed=true;
      }finally{if(!committed)EndUpdateResourceW(update,true);}
      PatchMenuLinks(path);
      if(code!=SectionHash(path,".text") || constants!=MenuRouteHash(path))throw new InvalidDataException("原生引擎代码或菜单入口以外的常量意外变化，更新已停止。");
      if(menus.Length>0 && !IsCompact(path))throw new InvalidDataException("菜单数量校验未通过。");if(!HasMIcon(path,icon,group))throw new InvalidDataException("M 图标校验未通过。");
    }
  }
}
