using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mason.Diagnostics {
  public sealed class RemoteMenu {
    public string Key;
    public string Sha256;
    public int[] Ids;
    public string[] Labels;
    public string Error;
  }
  public sealed class ModuleMenus {
    public int ProcessId;
    public string BaseAddress;
    public uint ImageSize;
    public uint ResourceRva;
    public uint ResourceSize;
    public RemoteMenu[] Menus;
  }
  public static class RemoteMenuProbe {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static byte[] Read(IntPtr process, long address, int length) {
      if(length<0 || length>16*1024*1024) throw new InvalidDataException("Resource read exceeds the diagnostic bound.");
      byte[] bytes=new byte[length]; UIntPtr count;
      if(!ReadProcessMemory(process, new IntPtr(address), bytes, new UIntPtr((uint)length), out count) || count.ToUInt64()!=(ulong)length)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read module resources.");
      return bytes;
    }
    static string Hash(byte[] bytes) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static ushort U16(byte[] bytes, int at) { return BitConverter.ToUInt16(bytes, at); }
    static uint U32(byte[] bytes, int at) { return BitConverter.ToUInt32(bytes, at); }
    static string Text(BinaryReader reader) { var result=new StringBuilder(); ushort c; while((c=reader.ReadUInt16())!=0)result.Append((char)c);return result.ToString(); }
    static void Items(BinaryReader reader, List<int> ids, List<string> labels, int depth) {
      if(depth>12 || ids.Count>256)throw new InvalidDataException("Unexpected menu nesting.");
      bool last=false;
      while(!last) {
        ushort flags=reader.ReadUInt16();int id=(flags&0x10)==0?reader.ReadUInt16():0;string label=Text(reader);
        if((flags&0x10)!=0)Items(reader, ids, labels, depth+1);
        else if(id!=0){ids.Add(id);labels.Add(label);}
        last=(flags&0x80)!=0;
      }
    }
    static RemoteMenu Menu(string key, byte[] data) {
      var menu=new RemoteMenu { Key=key, Sha256=Hash(data) };
      try {
        using(var reader=new BinaryReader(new MemoryStream(data))) {
          ushort version=reader.ReadUInt16(),offset=reader.ReadUInt16();
          if(version!=0)throw new InvalidDataException("Unsupported menu template version: "+version);
          reader.BaseStream.Position=4+offset;var ids=new List<int>();var labels=new List<string>();
          Items(reader, ids, labels, 0);menu.Ids=ids.ToArray();menu.Labels=labels.ToArray();
        }
      } catch(Exception error) { menu.Error=error.Message; }
      return menu;
    }
    static void Walk(IntPtr process, long imageBase, byte[] tree, int relative, List<string> keys, List<RemoteMenu> menus, int depth) {
      if(depth>4 || relative<0 || relative>tree.Length-16)throw new InvalidDataException("Invalid resource directory.");
      int count=U16(tree, relative+12)+U16(tree, relative+14);
      if(count>4096 || relative+16+8*count>tree.Length)throw new InvalidDataException("Invalid resource entries.");
      for(int i=0;i<count;i++) {
        uint name=U32(tree,relative+16+8*i),child=U32(tree,relative+20+8*i);string text;
        if((name&0x80000000)!=0) {int at=(int)(name&0x7fffffff);int length=U16(tree,at);text=Encoding.Unicode.GetString(tree,at+2,2*length);}
        else text=name.ToString();
        if(depth==0 && text!="4")continue;
        var next=new List<string>(keys);next.Add(text);
        if((child&0x80000000)!=0)Walk(process,imageBase,tree,(int)(child&0x7fffffff),next,menus,depth+1);
        else {
          int at=(int)child;uint rva=U32(tree,at),length=U32(tree,at+4);
          if(length>1024*1024)throw new InvalidDataException("Unexpected menu size.");
          menus.Add(Menu(String.Join("/",next.ToArray()),Read(process,imageBase+rva,(int)length)));
        }
      }
    }
    public static ModuleMenus Inspect(int processId, long imageBase) {
      // Read access only. This probe never writes memory or injects code.
      IntPtr process=OpenProcess(0x1010,false,processId);
      if(process==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
      try {
        byte[] header=Read(process,imageBase,4096);int pe=(int)U32(header,0x3c);
        if(U32(header,pe)!=0x4550)throw new InvalidDataException("Not a PE module.");
        int optional=pe+24;bool x64=U16(header,optional)==0x20b;int directory=optional+(x64?112:96);
        uint rva=U32(header,directory+16),length=U32(header,directory+20);var menus=new List<RemoteMenu>();
        if(rva!=0)Walk(process,imageBase,Read(process,imageBase+rva,(int)length),0,new List<string>(),menus,0);
        return new ModuleMenus { ProcessId=processId, BaseAddress="0x"+imageBase.ToString("x"),ImageSize=U32(header,optional+56), ResourceRva=rva,ResourceSize=length,Menus=menus.ToArray() };
      } finally { CloseHandle(process); }
    }
  }
}
