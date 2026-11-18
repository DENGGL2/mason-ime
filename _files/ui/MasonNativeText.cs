using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mason.NativeUi {
  public static class TextAdapter {
    private sealed class Image {
      public byte[] Data; public int Pe,Optional,Sections,Count,PointerSize,Machine;
      public Image(byte[] data){
        Data=data;if(data.Length<256 || BitConverter.ToUInt16(data,0)!=0x5a4d)throw new InvalidDataException("引擎文件格式无效。");
        Pe=BitConverter.ToInt32(data,0x3c);if(Pe<64 || Pe>data.Length-256 || BitConverter.ToUInt32(data,Pe)!=0x4550)throw new InvalidDataException("引擎文件格式无效。");
        Machine=BitConverter.ToUInt16(data,Pe+4);Count=BitConverter.ToUInt16(data,Pe+6);Optional=Pe+24;
        ushort magic=BitConverter.ToUInt16(data,Optional);PointerSize=magic==0x20b?8:magic==0x10b?4:0;
        Sections=Optional+BitConverter.ToUInt16(data,Pe+20);
        if(PointerSize==0 || Count<1 || Count>64 || Sections>data.Length-40*Count)throw new InvalidDataException("引擎结构无效。");
      }
      public int Offset(uint rva){
        for(int i=0;i<Count;i++){
          int section=Sections+40*i;uint start=BitConverter.ToUInt32(Data,section+12),size=BitConverter.ToUInt32(Data,section+16);
          if(rva>=start && (ulong)rva<(ulong)start+size){long offset=(long)BitConverter.ToUInt32(Data,section+20)+(rva-start);if(offset<0 || offset>=Data.Length)break;return (int)offset;}
        }
        throw new InvalidDataException("引擎文字接口地址无效。");
      }
      public string Text(int offset){
        int end=offset;while(end<Data.Length && end-offset<256 && Data[end]!=0)end++;
        if(end==Data.Length || end-offset>=256)throw new InvalidDataException("引擎文字接口名称无效。");
        return Encoding.ASCII.GetString(Data,offset,end-offset);
      }
      public string CodeHash(){
        for(int i=0;i<Count;i++){
          int at=Sections+40*i;if(Encoding.ASCII.GetString(Data,at,8).TrimEnd('\0')!=".text")continue;
          int size=BitConverter.ToInt32(Data,at+16),offset=BitConverter.ToInt32(Data,at+20);
          if(size<1 || offset<0 || offset>Data.Length-size)throw new InvalidDataException("引擎代码区无效。");
          using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Data,offset,size)).Replace("-","");
        }
        throw new InvalidDataException("引擎缺少代码区。");
      }
      public int NextImportReference(int position){
        int next=Data.Length,directory=Optional+(PointerSize==8?120:104),descriptor=Offset(BitConverter.ToUInt32(Data,directory));
        for(int i=0;i<128;i++,descriptor+=20){
          if(descriptor>Data.Length-20)throw new InvalidDataException("引擎导入表无效。");
          uint name=BitConverter.ToUInt32(Data,descriptor+12);if(name==0)break;
          int at=Offset(name);if(at>position)next=Math.Min(next,at);
          uint table=BitConverter.ToUInt32(Data,descriptor);if(table==0)table=BitConverter.ToUInt32(Data,descriptor+16);
          int thunk=Offset(table);if(thunk>position)next=Math.Min(next,thunk);
          for(int j=0;j<512;j++,thunk+=PointerSize){
            if(thunk>Data.Length-PointerSize)throw new InvalidDataException("引擎函数导入表无效。");
            ulong value=PointerSize==8?BitConverter.ToUInt64(Data,thunk):BitConverter.ToUInt32(Data,thunk);if(value==0)break;
            if((value&(PointerSize==8?0x8000000000000000UL:0x80000000UL))!=0)continue;
            at=Offset((uint)value);if(at>position)next=Math.Min(next,at);
          }
        }
        return next;
      }
      private static uint Align(uint value,uint alignment){
        if(alignment==0 || (alignment&(alignment-1))!=0)throw new InvalidDataException("引擎区段对齐无效。");
        return checked((value+alignment-1)/alignment*alignment);
      }
      private void Put(int at,uint value){Array.Copy(BitConverter.GetBytes(value),0,Data,at,4);}
      public uint AddImportName(string name){
        byte[] text=Encoding.ASCII.GetBytes(name+"\0");
        for(int i=0;i<Count;i++){
          int at=Sections+40*i;if(Encoding.ASCII.GetString(Data,at,8).TrimEnd('\0')!=".mstext")continue;
          uint rva=BitConverter.ToUInt32(Data,at+12);if(Text(Offset(rva))!=name)throw new InvalidDataException("标红接口区段名称不匹配。");return rva;
        }
        int firstRaw=Data.Length;uint end=0;
        for(int i=0;i<Count;i++){
          int at=Sections+40*i;uint raw=BitConverter.ToUInt32(Data,at+20),size=BitConverter.ToUInt32(Data,at+16),rva=BitConverter.ToUInt32(Data,at+12),virtualSize=BitConverter.ToUInt32(Data,at+8);
          if(size>0){if(raw>Int32.MaxValue || raw>Data.Length-size)throw new InvalidDataException("引擎区段超出文件范围。");firstRaw=Math.Min(firstRaw,(int)raw);}
          end=Math.Max(end,checked(rva+Math.Max(size,virtualSize)));
        }
        if(Count>=64 || Sections+40*(Count+1)>firstRaw || Sections+40*(Count+1)>BitConverter.ToUInt32(Data,Optional+60))throw new InvalidDataException("引擎没有安全的标红接口区段空间。");
        uint sectionAlign=BitConverter.ToUInt32(Data,Optional+32),fileAlign=BitConverter.ToUInt32(Data,Optional+36);
        uint newRaw=Align((uint)Data.Length,fileAlign),newRva=Align(end,sectionAlign),newSize=Align((uint)text.Length,fileAlign);
        uint total=checked(newRaw+newSize);if(total>256*1024*1024)throw new InvalidDataException("引擎文件超出已验证的大小。");
        Array.Resize(ref Data,(int)total);Array.Copy(text,0,Data,(int)newRaw,text.Length);
        int header=Sections+40*Count;Array.Clear(Data,header,40);Array.Copy(Encoding.ASCII.GetBytes(".mstext"),0,Data,header,7);
        Put(header+8,(uint)text.Length);Put(header+12,newRva);Put(header+16,newSize);Put(header+20,newRaw);Put(header+36,0x40000040);
        Count++;Array.Copy(BitConverter.GetBytes((ushort)Count),0,Data,Pe+6,2);
        Put(Optional+8,checked(BitConverter.ToUInt32(Data,Optional+8)+newSize));Put(Optional+56,Align(checked(newRva+(uint)text.Length),sectionAlign));Put(Optional+64,0);
        int directories=Optional+(PointerSize==8?112:96),number=Optional+(PointerSize==8?108:92);
        if(BitConverter.ToUInt32(Data,number)>11){Put(directories+11*8,0);Put(directories+11*8+4,0);}
        return newRva;
      }
    }
    private static int Routes(Image image,bool change,string adapterName="Mason.dll",bool extended=false){
      int directory=image.Optional+(image.PointerSize==8?120:104);uint importRva=BitConverter.ToUInt32(image.Data,directory);
      if(importRva==0)throw new InvalidDataException("引擎缺少文字接口。");
      int descriptor=image.Offset(importRva),seen=0,routed=0,matched=0;
      for(int index=0;index<128;index++,descriptor+=20){
        if(descriptor>image.Data.Length-20)throw new InvalidDataException("引擎文字接口表无效。");
        uint nameRva=BitConverter.ToUInt32(image.Data,descriptor+12);if(nameRva==0)break;
        int nameAt=image.Offset(nameRva);string dll=image.Text(nameAt);
        bool originalDWrite=dll.Equals("DWrite.dll",StringComparison.OrdinalIgnoreCase),originalD2D=dll.Equals("d2d1.dll",StringComparison.OrdinalIgnoreCase),ours=dll.Equals(adapterName,StringComparison.OrdinalIgnoreCase),legacy=extended && dll.Equals("Mason.dll",StringComparison.OrdinalIgnoreCase);
        if(!originalDWrite && !originalD2D && !ours && !legacy)continue;
        uint thunkRva=BitConverter.ToUInt32(image.Data,descriptor);if(thunkRva==0)thunkRva=BitConverter.ToUInt32(image.Data,descriptor+16);
        int thunk=image.Offset(thunkRva),kind=0,count=0;
        for(int j=0;j<32;j++,thunk+=image.PointerSize){
          if(thunk>image.Data.Length-image.PointerSize)throw new InvalidDataException("引擎文字函数表无效。");
          ulong value=image.PointerSize==8?BitConverter.ToUInt64(image.Data,thunk):BitConverter.ToUInt32(image.Data,thunk);if(value==0)break;
          bool ordinal=(value&(image.PointerSize==8?0x8000000000000000UL:0x80000000UL))!=0;
          if(ordinal){int number=(int)(value&0xffff);if(number==1 && !originalDWrite)kind|=2;else if(number==2 && (ours || legacy))kind|=1;else throw new InvalidDataException("此引擎的文字接口尚未验证。");}
          else {
            string function=image.Text(image.Offset((uint)value)+2);
            if(function=="DWriteCreateFactory")kind|=1;else if(function=="D2D1CreateFactory")kind|=2;else throw new InvalidDataException("此引擎的文字接口尚未验证。");
          }
          count++;
        }
        if(count!=1 || (kind!=1 && kind!=2) || (seen&kind)!=0)throw new InvalidDataException("引擎文字接口数量不符合已验证的版本。");
        seen|=kind;matched++;
        if(ours)routed|=kind;
        else if(change && extended){
          uint replacement=image.AddImportName(adapterName);Array.Copy(BitConverter.GetBytes(replacement),0,image.Data,descriptor+12,4);Array.Clear(image.Data,descriptor+4,4);routed|=kind;
        }
        else if(change){
          byte[] replacement=Encoding.ASCII.GetBytes("Mason.dll\0");
          int capacity=dll.Length+1;
          if(replacement.Length>capacity){
            int boundary=image.NextImportReference(nameAt);
            if(nameAt+replacement.Length>boundary)throw new InvalidDataException("文字接口名称没有安全的扩展空间。");
            for(int extra=capacity;extra<replacement.Length;extra++)if(image.Data[nameAt+extra]!=0)throw new InvalidDataException("文字接口名称填充空间无效。");
            capacity=replacement.Length;
          }
          Array.Clear(image.Data,nameAt,capacity);Array.Copy(replacement,0,image.Data,nameAt,replacement.Length);routed|=kind;
        }
      }
      if(seen!=3 || matched!=2)throw new InvalidDataException("未找到经过验证的候选窗文字接口。");
      return routed;
    }
    public static bool HasRoutes(string path){return Routes(new Image(File.ReadAllBytes(path)),false)==3;}
    public static byte[] Patch(byte[] data){
      var before=new Image(data);string code=before.CodeHash();var result=new Image((byte[])data.Clone());Routes(result,true);
      if(result.CodeHash()!=code || Routes(result,false)!=3)throw new InvalidDataException("文字绘制连接校验失败，未覆盖引擎。");
      return result.Data;
    }
    public static string ClientLibraryName(string path){return ClientName(new Image(File.ReadAllBytes(path)));}
    private static string ClientName(Image image){
      if(image.Machine==0x8664)return "Mason64.dll";if(image.Machine==0x14c)return "Mason32.dll";throw new InvalidDataException("此客户端架构尚未验证。");
    }
    private static bool HasTextImports(Image image){
      int directory=image.Optional+(image.PointerSize==8?120:104);uint imports=BitConverter.ToUInt32(image.Data,directory);if(imports==0)return false;
      int descriptor=image.Offset(imports);
      for(int i=0;i<128;i++,descriptor+=20){
        if(descriptor>image.Data.Length-20)throw new InvalidDataException("客户端导入表无效。");uint name=BitConverter.ToUInt32(image.Data,descriptor+12);if(name==0)return false;
        string dll=image.Text(image.Offset(name));if(new[]{"DWrite.dll","d2d1.dll","Mason.dll","Mason64.dll","Mason32.dll"}.Contains(dll,StringComparer.OrdinalIgnoreCase))return true;
      }
      throw new InvalidDataException("客户端导入表无效。");
    }
    public static bool SupportsClient(string path){var image=new Image(File.ReadAllBytes(path));if(!HasTextImports(image))return false;Routes(image,false,ClientName(image),true);return true;}
    public static bool HasClientRoutes(string path){var image=new Image(File.ReadAllBytes(path));return HasTextImports(image) && Routes(image,false,ClientName(image),true)==3;}
    public static byte[] PatchClient(byte[] data){
      var before=new Image(data);string code=before.CodeHash();var result=new Image((byte[])data.Clone());string name=ClientName(result);
      Routes(result,true,name,true);if(result.CodeHash()!=code || Routes(result,false,name,true)!=3)throw new InvalidDataException("客户端绘制连接校验失败，原文件已保留。");return result.Data;
    }
    public static byte[] CanonicalRoutes(string path,byte[] constants,int sectionOffset){
      var image=new Image(File.ReadAllBytes(path));
      try{Routes(image,false,ClientName(image),true);}catch(InvalidDataException){return constants;}
      int directory=image.Optional+(image.PointerSize==8?120:104),descriptor=image.Offset(BitConverter.ToUInt32(image.Data,directory));
      for(int i=0;i<128;i++,descriptor+=20){
        uint name=BitConverter.ToUInt32(image.Data,descriptor+12);if(name==0)break;string dll=image.Text(image.Offset(name));
        if(!new[]{"DWrite.dll","d2d1.dll","Mason.dll",ClientName(image)}.Contains(dll,StringComparer.OrdinalIgnoreCase))continue;
        int at=descriptor-sectionOffset;if(at<0 || at>constants.Length-20)throw new InvalidDataException("客户端文字接口常量区无效。");
        Array.Clear(constants,at+4,4);Array.Clear(constants,at+12,4);
      }
      return constants;
    }
    private static void Write(string target,byte[] value,string backup){
      if(File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(value))return;
      if(File.Exists(target)){Directory.CreateDirectory(backup);File.Copy(target,Path.Combine(backup,Path.GetFileName(target)),false);}
      Directory.CreateDirectory(Path.GetDirectoryName(target));string temp=target+".mason-text-"+Guid.NewGuid().ToString("N");
      using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(value,0,value.Length);stream.Flush(true);}
      if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
      if(!File.ReadAllBytes(target).SequenceEqual(value))throw new IOException("标红组件写入校验失败。");
    }
    private static byte[] ClientAdapter(string client,string resources){
      var image=new Image(File.ReadAllBytes(client));string architecture=image.Machine==0x8664?"x64":image.Machine==0x14c?"x86":null;
      if(architecture==null)throw new InvalidDataException("此客户端架构尚未验证。");
      byte[] adapter=File.ReadAllBytes(Path.Combine(resources,"native",architecture,"Mason.dll"));
      if(new Image(adapter).Machine!=image.Machine)throw new InvalidDataException("标红组件与客户端架构不匹配。");return adapter;
    }
    public static bool IsClientCurrent(string client,string resources){
      if(!HasClientRoutes(client))return false;string target=Path.Combine(Path.GetDirectoryName(client),ClientLibraryName(client));
      return File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(ClientAdapter(client,resources));
    }
    public static bool InstallClientAdapter(string client,string resources,string backup){
      if(!SupportsClient(client))return false;
      byte[] adapter=ClientAdapter(client,resources);string target=Path.Combine(Path.GetDirectoryName(client),ClientLibraryName(client));
      bool changed=!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(adapter);Write(target,adapter,backup);return changed;
    }
    public static bool InstallClient(string client,string resources,string backup){
      if(!SupportsClient(client))return false;
      byte[] before=File.ReadAllBytes(client),patched=PatchClient(before);bool changed=InstallClientAdapter(client,resources,backup);
      if(!before.SequenceEqual(patched)){Write(client,patched,backup);changed=true;}
      if(!IsClientCurrent(client,resources))throw new InvalidDataException("客户端标红连接未完成。");return changed;
    }
    public static bool Install(string engine,string resources,string backup){
      engine=Path.GetFullPath(engine);string server=Path.Combine(engine,"WeaselServer.exe");
      var image=new Image(File.ReadAllBytes(server));string architecture=image.Machine==0x8664?"x64":image.Machine==0x14c?"x86":null;
      if(architecture==null)throw new InvalidDataException("此引擎架构尚未验证。");
      string library=Path.Combine(resources,"native",architecture,"Mason.dll");byte[] adapter=File.ReadAllBytes(library);
      if(new Image(adapter).Machine!=image.Machine)throw new InvalidDataException("标红组件与引擎架构不匹配。");
      byte[] patched=Patch(image.Data);string destination=Path.Combine(engine,"Mason.dll");
      bool changed=!image.Data.SequenceEqual(patched) || !File.Exists(destination) || !File.ReadAllBytes(destination).SequenceEqual(adapter);
      Write(destination,adapter,backup);Write(server,patched,backup);
      foreach(string name in new[]{"weasel.dll","weaselx64.dll","weasel.ime","weaselx64.ime"}){
        string client=Path.Combine(engine,name);if(File.Exists(client) && InstallClient(client,resources,Path.Combine(backup,name)))changed=true;
      }
      if(!HasRoutes(server))throw new InvalidDataException("标红绘制连接未完成。");
      return changed;
    }
  }
}
