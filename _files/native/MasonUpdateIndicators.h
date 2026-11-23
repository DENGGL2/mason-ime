#pragma once
#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <string>
#include <vector>
#include <cmath>
#include <cstring>
#include <algorithm>
#include <utility>

struct MasonUpdateDiagnosticData {
  DWORD size;
  volatile LONG popupHooks, langBarHooks, iconBadges, iconRefreshes, menuBadges, menuReads;
};
extern "C" { MasonUpdateDiagnosticData MasonUpdateDiagnostics = {sizeof(MasonUpdateDiagnosticData)}; }

namespace MasonUpdateIndicators {
struct Snapshot { bool application=false, dictionary=false, unread=false; std::wstring appVersion, dictionaryVersion; };
inline SRWLOCK stateLock=SRWLOCK_INIT;
inline Snapshot cached;
inline std::wstring root;
inline bool rootReady=false;
inline void (*refreshLanguageBar)()=nullptr;
inline HBITMAP menuDot=nullptr;
inline HBITMAP menuDotMask=nullptr;
inline void RefreshTray();
#ifdef MASON_UPDATE_PROOF
inline void SetTestRoot(const std::wstring& value){root=value;rootReady=true;}
#endif

inline std::wstring UserRoot(){
  if(rootReady)return root;rootReady=true;
  wchar_t value[32768]{};DWORD bytes=sizeof(value);
  if(RegGetValueW(HKEY_CURRENT_USER,L"Software\\Rime\\Weasel",L"RimeUserDir",RRF_RT_REG_SZ,nullptr,value,&bytes)==ERROR_SUCCESS)root=value;
  return root;
}
inline std::wstring Ini(const std::wstring& file,const wchar_t* section,const wchar_t* key){wchar_t value[80]{};GetPrivateProfileStringW(section,key,L"",value,80,file.c_str());return value;}
inline bool ValidVersion(const std::wstring& value){if(value.empty() || value.size()>40)return false;for(wchar_t c:value)if((c<L'0' || c>L'9') && c!=L'.')return false;return true;}
inline Snapshot Read(){
  Snapshot value;const auto directory=UserRoot();if(directory.empty())return value;
  const auto status=directory+L"\\mason-update-status.ini",seen=directory+L"\\mason-update-seen.ini";
  value.appVersion=Ini(status,L"updates",L"app_version");value.dictionaryVersion=Ini(status,L"updates",L"dictionary_version");
  value.application=ValidVersion(value.appVersion) && Ini(status,L"updates",L"app_available")==L"1";
  value.dictionary=ValidVersion(value.dictionaryVersion) && Ini(status,L"updates",L"dictionary_available")==L"1";
  value.unread=(value.application && Ini(seen,L"seen",L"app_version")!=value.appVersion)
      || (value.dictionary && Ini(seen,L"seen",L"dictionary_version")!=value.dictionaryVersion);
  AcquireSRWLockExclusive(&stateLock);cached=value;ReleaseSRWLockExclusive(&stateLock);return value;
}
inline Snapshot Current(){AcquireSRWLockShared(&stateLock);Snapshot value=cached;ReleaseSRWLockShared(&stateLock);return value;}
inline bool Same(const Snapshot& a,const Snapshot& b){return a.unread==b.unread && a.application==b.application && a.dictionary==b.dictionary && a.appVersion==b.appVersion && a.dictionaryVersion==b.dictionaryVersion;}
inline bool Seen(const Snapshot& value){
  const auto directory=UserRoot();if(directory.empty())return false;
  const DWORD attributes=GetFileAttributesW(directory.c_str());if(attributes==INVALID_FILE_ATTRIBUTES || !(attributes&FILE_ATTRIBUTE_DIRECTORY) || (attributes&FILE_ATTRIBUTE_REPARSE_POINT))return false;
  std::string body="[seen]\r\napp_version=";for(wchar_t c:value.appVersion)body.push_back(static_cast<char>(c));body+="\r\ndictionary_version=";for(wchar_t c:value.dictionaryVersion)body.push_back(static_cast<char>(c));body+="\r\n";
  const auto target=directory+L"\\mason-update-seen.ini",temporary=target+L".new-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64());
  HANDLE file=CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)return false;
  DWORD written=0;BOOL ok=WriteFile(file,body.data(),static_cast<DWORD>(body.size()),&written,nullptr);CloseHandle(file);
  if(!ok || written!=body.size() || !MoveFileExW(temporary.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))return false;
  InterlockedIncrement(&MasonUpdateDiagnostics.menuReads);Read();RefreshTray();if(refreshLanguageBar)refreshLanguageBar();return true;
}

inline HBITMAP Bitmap(int width,int height,void** pixels){BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=width;info.bmiHeader.biHeight=-height;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;return CreateDIBSection(nullptr,&info,DIB_RGB_COLORS,pixels,nullptr,0);}
inline void Circle(DWORD* pixels,int width,int height,double x,double y,double radius){
  for(int row=0;row<height;++row)for(int col=0;col<width;++col){int coverage=0;for(int sy=0;sy<4;++sy)for(int sx=0;sx<4;++sx){double dx=col+(sx+.5)/4-x,dy=row+(sy+.5)/4-y;if(dx*dx+dy*dy<=radius*radius)++coverage;}if(!coverage)continue;
    const double alpha=coverage/16.0;DWORD old=pixels[row*width+col];const BYTE b=static_cast<BYTE>(std::lround(0x82*alpha+(old&255)*(1-alpha))),g=static_cast<BYTE>(std::lround(0x78*alpha+((old>>8)&255)*(1-alpha))),r=static_cast<BYTE>(std::lround(0xDD*alpha+((old>>16)&255)*(1-alpha))),a=static_cast<BYTE>(std::lround(255*alpha+((old>>24)&255)*(1-alpha)));pixels[row*width+col]=(DWORD(a)<<24)|(DWORD(r)<<16)|(DWORD(g)<<8)|b;
  }
}
inline void MenuBitmaps(){
  if(menuDot)return;void* pixels=nullptr;menuDot=Bitmap(12,12,&pixels);if(!menuDot)return;std::memset(pixels,0,576);Circle(static_cast<DWORD*>(pixels),12,12,6,6,4.2);
  BYTE mask[24];std::memset(mask,255,sizeof(mask));for(int y=0;y<12;++y)for(int x=0;x<12;++x)if((static_cast<DWORD*>(pixels)[y*12+x]>>24)>0)mask[y*2+x/8]&=static_cast<BYTE>(~(0x80>>(x%8)));menuDotMask=CreateBitmap(12,12,1,1,mask);
}
inline HICON Badge(HICON original){
  if(!original)return nullptr;ICONINFO source{};if(!GetIconInfo(original,&source))return nullptr;BITMAP dimensions{};GetObjectW(source.hbmColor?source.hbmColor:source.hbmMask,sizeof(dimensions),&dimensions);
  const int width=dimensions.bmWidth,height=source.hbmColor?dimensions.bmHeight:dimensions.bmHeight/2;
  if(width<8 || height<8 || width>128 || height>128){if(source.hbmColor)DeleteObject(source.hbmColor);if(source.hbmMask)DeleteObject(source.hbmMask);return nullptr;}
  void* pixels=nullptr;HBITMAP color=Bitmap(width,height,&pixels);HDC dc=CreateCompatibleDC(nullptr);if(!color || !pixels || !dc){if(color)DeleteObject(color);if(dc)DeleteDC(dc);if(source.hbmColor)DeleteObject(source.hbmColor);if(source.hbmMask)DeleteObject(source.hbmMask);return nullptr;}HGDIOBJ before=SelectObject(dc,color);std::memset(pixels,0,width*height*4);DrawIconEx(dc,0,0,original,width,height,0,nullptr,DI_NORMAL);SelectObject(dc,before);DeleteDC(dc);
  auto* values=static_cast<DWORD*>(pixels);bool alpha=false;for(int i=0;i<width*height;++i)alpha=alpha || (values[i]>>24)!=0;
  if(!alpha){void* maskPixels=nullptr;HBITMAP maskColor=Bitmap(width,height,&maskPixels);dc=CreateCompatibleDC(nullptr);if(maskColor && dc && maskPixels){before=SelectObject(dc,maskColor);DrawIconEx(dc,0,0,original,width,height,0,nullptr,DI_MASK);for(int i=0;i<width*height;++i)if((static_cast<DWORD*>(maskPixels)[i]&0xFFFFFF)==0)values[i]|=0xFF000000;SelectObject(dc,before);}if(dc)DeleteDC(dc);if(maskColor)DeleteObject(maskColor);}
  const double radius=(std::max)(2.5,(std::min)(5.5,width*.19));Circle(values,width,height,width-radius-.25,radius+.25,radius);
  std::vector<BYTE> zeroMask(((width+15)/16)*2*height,0);HBITMAP mask=CreateBitmap(width,height,1,1,zeroMask.data());ICONINFO info{};info.fIcon=TRUE;info.hbmColor=color;info.hbmMask=mask;HICON result=CreateIconIndirect(&info);
  DeleteObject(mask);DeleteObject(color);if(source.hbmColor)DeleteObject(source.hbmColor);if(source.hbmMask)DeleteObject(source.hbmMask);if(result)InterlockedIncrement(&MasonUpdateDiagnostics.iconBadges);return result;
}

inline bool KnownMenu(HMENU menu){return menu && GetMenuState(menu,40008,MF_BYCOMMAND)!=static_cast<UINT>(-1) && GetMenuState(menu,40003,MF_BYCOMMAND)!=static_cast<UINT>(-1);}
inline void MenuBadges(HMENU menu,const Snapshot& value){
  if(!KnownMenu(menu))return;MenuBitmaps();for(const auto item:{std::make_pair(40003u,value.application),std::make_pair(40005u,value.dictionary)}){MENUITEMINFOW info{};info.cbSize=sizeof(info);info.fMask=MIIM_BITMAP;info.hbmpItem=item.second?menuDot:nullptr;if(SetMenuItemInfoW(menu,item.first,FALSE,&info) && item.second)InterlockedIncrement(&MasonUpdateDiagnostics.menuBadges);}
}
using PopupFunction=BOOL(WINAPI*)(HMENU,UINT,int,int,int,HWND,const RECT*);
using PopupExFunction=BOOL(WINAPI*)(HMENU,UINT,int,int,HWND,LPTPMPARAMS);
inline PopupFunction popupOriginal=nullptr;
inline PopupExFunction popupExOriginal=nullptr;
inline BOOL WINAPI Popup(HMENU menu,UINT flags,int x,int y,int reserved,HWND owner,const RECT* area){if(KnownMenu(menu)){const auto value=Read();MenuBadges(menu,value);Seen(value);}return popupOriginal?popupOriginal(menu,flags,x,y,reserved,owner,area):FALSE;}
inline BOOL WINAPI PopupEx(HMENU menu,UINT flags,int x,int y,HWND owner,LPTPMPARAMS area){if(KnownMenu(menu)){const auto value=Read();MenuBadges(menu,value);Seen(value);}return popupExOriginal?popupExOriginal(menu,flags,x,y,owner,area):FALSE;}
inline bool OwnedModule(HMODULE module){wchar_t path[MAX_PATH]{};if(!GetModuleFileNameW(module,path,MAX_PATH))return false;const wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;return _wcsicmp(name,L"WeaselServer.exe")==0 || _wcsicmp(name,L"weasel.dll")==0 || _wcsicmp(name,L"weaselx64.dll")==0 || _wcsicmp(name,L"weasel.ime")==0 || _wcsicmp(name,L"weaselx64.ime")==0;}
inline bool OwnedAddress(void* address){HMODULE module=nullptr;return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(address),&module) && OwnedModule(module);}
inline void Patch(HMODULE module,void* original,void* replacement){
  if(!original || !OwnedModule(module))return;auto* base=reinterpret_cast<BYTE*>(module);auto* dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);if(dos->e_magic!=IMAGE_DOS_SIGNATURE)return;auto* nt=reinterpret_cast<IMAGE_NT_HEADERS*>(base+dos->e_lfanew);if(nt->Signature!=IMAGE_NT_SIGNATURE)return;
  const auto directory=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];if(!directory.VirtualAddress)return;auto* imports=reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base+directory.VirtualAddress);
  for(size_t i=0;i<directory.Size/sizeof(IMAGE_IMPORT_DESCRIPTOR) && imports[i].Name;++i){if(!imports[i].FirstThunk)continue;auto* table=reinterpret_cast<IMAGE_THUNK_DATA*>(base+imports[i].FirstThunk);for(size_t slot=0;slot<4096 && table[slot].u1.Function;++slot){if(reinterpret_cast<void*>(table[slot].u1.Function)!=original)continue;DWORD old=0;if(!VirtualProtect(table+slot,sizeof(table[slot]),PAGE_READWRITE,&old))continue;InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(&table[slot].u1.Function),replacement);DWORD ignored=0;VirtualProtect(table+slot,sizeof(table[slot]),old,&ignored);InterlockedIncrement(&MasonUpdateDiagnostics.popupHooks);}}
}
inline void Install(){
  UserRoot();Read();HMODULE user=GetModuleHandleW(L"user32.dll");if(!user)return;popupOriginal=reinterpret_cast<PopupFunction>(GetProcAddress(user,"TrackPopupMenu"));popupExOriginal=reinterpret_cast<PopupExFunction>(GetProcAddress(user,"TrackPopupMenuEx"));
  HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,GetCurrentProcessId());if(snapshot==INVALID_HANDLE_VALUE)return;MODULEENTRY32W entry{};entry.dwSize=sizeof(entry);if(Module32FirstW(snapshot,&entry))do{Patch(entry.hModule,reinterpret_cast<void*>(popupOriginal),reinterpret_cast<void*>(&Popup));Patch(entry.hModule,reinterpret_cast<void*>(popupExOriginal),reinterpret_cast<void*>(&PopupEx));}while(Module32NextW(snapshot,&entry));CloseHandle(snapshot);
}
using NotifyFunction=BOOL(WINAPI*)(DWORD,PNOTIFYICONDATAW);
inline NotifyFunction trackedNotify=nullptr;
inline NOTIFYICONDATAW tracked{};
inline HICON trackedIcon=nullptr;
inline HICON trackedSource=nullptr;
inline UINT_PTR trayTimer=0;
inline Snapshot trayState;
inline void RefreshTray(){
  if(!trackedNotify || !tracked.hWnd || !trackedIcon)return;const auto value=Read();NOTIFYICONDATAW data=tracked;data.cbSize=sizeof(data);data.uFlags=NIF_ICON|(tracked.uFlags&NIF_GUID);HICON badge=value.unread?Badge(trackedIcon):nullptr;data.hIcon=badge?badge:trackedIcon;trackedNotify(NIM_MODIFY,&data);if(badge)DestroyIcon(badge);trayState=value;
}
inline void CALLBACK TrayTick(HWND,UINT,UINT_PTR,DWORD){try{const auto value=Read();if(!Same(trayState,value))RefreshTray();}catch(...){} }
inline BOOL Forward(DWORD message,PNOTIFYICONDATAW data,NotifyFunction original){
  if(!original)return FALSE;
  if(data && message==NIM_DELETE && data->hWnd==tracked.hWnd && data->uID==tracked.uID){if(trayTimer)KillTimer(tracked.hWnd,trayTimer);trayTimer=0;if(trackedIcon)DestroyIcon(trackedIcon);trackedIcon=nullptr;trackedSource=nullptr;tracked={};}
  if(data && (message==NIM_ADD || message==NIM_MODIFY) && data->cbSize>=offsetof(NOTIFYICONDATAW,hIcon)+sizeof(HICON) && data->cbSize<=sizeof(NOTIFYICONDATAW) && (data->uFlags&NIF_ICON) && data->hIcon){
    trackedNotify=original;if(data->hIcon!=trackedSource || data->hWnd!=tracked.hWnd || data->uID!=tracked.uID){if(trackedIcon)DestroyIcon(trackedIcon);trackedIcon=CopyIcon(data->hIcon);trackedSource=data->hIcon;tracked={};std::memcpy(&tracked,data,data->cbSize);}
    if(!trayTimer && tracked.hWnd)trayTimer=SetTimer(tracked.hWnd,0x4D41534F,1500,&TrayTick);
    trayState=Current();HICON badge=trayState.unread?Badge(data->hIcon):nullptr;if(badge){NOTIFYICONDATAW shadow{};std::memcpy(&shadow,data,data->cbSize);shadow.hIcon=badge;BOOL result=original(message,&shadow);DestroyIcon(badge);return result;}
  }
  return original(message,data);
}
}
