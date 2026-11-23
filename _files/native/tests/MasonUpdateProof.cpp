#define NOMINMAX
#include <windows.h>
#include <msctf.h>
#include <d2d1.h>
#include <fstream>
#include <string>
#include <stdexcept>
#include "MasonTestTextStore.h"
#pragma comment(lib,"user32.lib")
#pragma comment(lib,"gdi32.lib")
#pragma comment(lib,"ole32.lib")
#pragma comment(lib,"oleaut32.lib")
#pragma comment(lib,"uuid.lib")

struct UpdateData {DWORD size;LONG popupHooks,langBarHooks,iconBadges,iconRefreshes,menuBadges,menuReads;};
static void Require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
static void Check(HRESULT result,const char* message){if(FAILED(result))throw std::runtime_error(std::string(message)+": "+std::to_string(static_cast<long>(result)));}
static void Pump(unsigned duration){ULONGLONG until=GetTickCount64()+duration;while(GetTickCount64()<until){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}Sleep(10);}}

class MenuCapture : public ITfMenu {
public:
  LONG references=1;unsigned entries=0,dots=0;bool app=false,dictionary=false;
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** object) override {if(!object)return E_POINTER;*object=nullptr;if(iid==IID_IUnknown || iid==__uuidof(ITfMenu)){*object=this;AddRef();return S_OK;}return E_NOINTERFACE;}
  ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&references);}
  ULONG STDMETHODCALLTYPE Release() override {return InterlockedDecrement(&references);}
  HRESULT STDMETHODCALLTYPE AddMenuItem(UINT id,DWORD,HBITMAP bitmap,HBITMAP,const WCHAR*,ULONG,ITfMenu**) override {++entries;if(bitmap){++dots;if(id==40003)app=true;if(id==40005)dictionary=true;}return S_OK;}
};

int wmain(int argc,wchar_t** args){
  if(argc!=3)return 2;std::wstring root=args[1],report=args[2];int exit=1;std::string error;
  ITfThreadMgrEx* manager=nullptr;ITfDocumentMgr* document=nullptr;ITfContext* context=nullptr;MasonTestTextStore* store=nullptr;
  ITfInputProcessorProfiles* profiles=nullptr;ITfInputProcessorProfileMgr* profileManager=nullptr;TF_INPUTPROCESSORPROFILE previous{};bool previousKnown=false,activated=false;HWND window=nullptr;UpdateData* diagnostic=nullptr;
  CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
  try{
    wchar_t executable[MAX_PATH]{};GetModuleFileNameW(nullptr,executable,MAX_PATH);std::wstring directory=executable;directory=directory.substr(0,directory.find_last_of(L'\\')+1);
    const wchar_t* adapter=sizeof(void*)==8?L"Mason64.dll":L"Mason32.dll";HMODULE module=LoadLibraryW((directory+adapter).c_str());Require(module!=nullptr,"Private update adapter could not be loaded");
    using SetRoot=void(__cdecl*)(const wchar_t*);auto setRoot=reinterpret_cast<SetRoot>(GetProcAddress(module,"MasonProofSetUpdateRoot"));if(!setRoot)setRoot=reinterpret_cast<SetRoot>(GetProcAddress(module,"_MasonProofSetUpdateRoot"));Require(setRoot!=nullptr,"Proof root setter is unavailable");setRoot(root.c_str());
    diagnostic=reinterpret_cast<UpdateData*>(GetProcAddress(module,"MasonUpdateDiagnostics"));Require(diagnostic && diagnostic->size==sizeof(UpdateData),"Update diagnostic ABI mismatch");
    window=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,L"STATIC",L"",WS_POPUP,-16000,-16000,300,120,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);Require(window!=nullptr,"Private test window could not be created");
    Check(CoCreateInstance(CLSID_TF_ThreadMgr,nullptr,CLSCTX_INPROC_SERVER,IID_ITfThreadMgrEx,reinterpret_cast<void**>(&manager)),"Thread manager creation failed");TfClientId id=0;Check(manager->ActivateEx(&id,TF_TMAE_NOACTIVATEKEYBOARDLAYOUT),"Private thread activation failed");
    Check(manager->CreateDocumentMgr(&document),"Document creation failed");store=new MasonTestTextStore(window);TfEditCookie cookie=0;Check(document->CreateContext(id,0,static_cast<ITextStoreACP*>(store),&context,&cookie),"Context creation failed");Check(document->Push(context),"Context push failed");Check(manager->SetFocus(document),"Document focus failed");
    Check(CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,IID_ITfInputProcessorProfiles,reinterpret_cast<void**>(&profiles)),"Profile reader unavailable");Check(profiles->QueryInterface(IID_ITfInputProcessorProfileMgr,reinterpret_cast<void**>(&profileManager)),"Profile manager unavailable");
    CLSID mason{};CLSIDFromString(L"{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}",&mason);TF_INPUTPROCESSORPROFILE selected{};bool found=false;
    for(LANGID language:{static_cast<LANGID>(0x0804),static_cast<LANGID>(0x0404)}){IEnumTfInputProcessorProfiles* enumeration=nullptr;if(FAILED(profileManager->EnumProfiles(language,&enumeration)))continue;TF_INPUTPROCESSORPROFILE item{};ULONG fetched=0;while(enumeration->Next(1,&item,&fetched)==S_OK && fetched){if(item.dwProfileType==TF_PROFILETYPE_INPUTPROCESSOR && item.clsid==mason){selected=item;found=true;break;}}enumeration->Release();if(found)break;}
    Require(found,"Installed Mason profile was not found");previousKnown=SUCCEEDED(profileManager->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&previous));Check(profileManager->ActivateProfile(selected.dwProfileType,selected.langid,selected.clsid,selected.guidProfile,selected.hkl,0),"Private-thread profile activation failed");activated=true;
    using Factory=HRESULT(WINAPI*)(D2D1_FACTORY_TYPE,REFIID,const D2D1_FACTORY_OPTIONS*,void**);auto create=reinterpret_cast<Factory>(GetProcAddress(module,"D2D1CreateFactory"));ID2D1Factory* factory=nullptr;Check(create(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory),nullptr,reinterpret_cast<void**>(&factory)),"Private factory initialization failed");factory->Release();Pump(650);
    ITfLangBarItemMgr* itemManager=nullptr;Check(manager->QueryInterface(IID_ITfLangBarItemMgr,reinterpret_cast<void**>(&itemManager)),"Language bar manager unavailable");IEnumTfLangBarItems* enumeration=nullptr;Check(itemManager->EnumItems(&enumeration),"Language bar enumeration failed");itemManager->Release();
    ITfLangBarItem* item=nullptr;ULONG fetched=0;ITfLangBarItemButton* button=nullptr;
    while(enumeration->Next(1,&item,&fetched)==S_OK && fetched){TF_LANGBARITEMINFO info{};item->GetInfo(&info);if(info.clsidService==mason)item->QueryInterface(IID_ITfLangBarItemButton,reinterpret_cast<void**>(&button));item->Release();if(button)break;}enumeration->Release();Require(button!=nullptr,"Real Mason language bar item unavailable");
    Require(diagnostic->langBarHooks>0,"Real language bar object did not match the verified layout");HICON icon=nullptr;Check(button->GetIcon(&icon),"Real language bar icon unavailable");Require(icon!=nullptr && diagnostic->iconBadges>0,"Real language bar icon was not badged");DestroyIcon(icon);
    MenuCapture menu;Check(button->InitMenu(&menu),"Real menu initialization failed");Require(menu.app && menu.dictionary && menu.dots==2,"Both real update menu items must have dots");Require(diagnostic->menuReads>0,"Opening the real menu did not mark updates read");
    LONG badges=diagnostic->iconBadges;icon=nullptr;Check(button->GetIcon(&icon),"Read-state icon unavailable");Require(diagnostic->iconBadges==badges,"Read-state icon still received a dot");
    std::wstring status=root+L"\\mason-update-status.ini";WritePrivateProfileStringW(L"updates",L"dictionary_version",L"1.0.7",status.c_str());Pump(1800);badges=diagnostic->iconBadges;icon=nullptr;Check(button->GetIcon(&icon),"Next-version icon unavailable");Require(diagnostic->iconBadges>badges,"A new dictionary version did not restore the icon dot");DestroyIcon(icon);
    button->Release();exit=0;
  }catch(const std::exception& failure){error=failure.what();}
  if(activated && previousKnown)profileManager->ActivateProfile(previous.dwProfileType,previous.langid,previous.clsid,previous.guidProfile,previous.hkl,0);
  if(profileManager)profileManager->Release();if(profiles)profiles->Release();if(document){document->Pop(TF_POPF_ALL);document->Release();}if(context)context->Release();if(store)store->Release();if(manager){manager->Deactivate();manager->Release();}if(window)DestroyWindow(window);
  std::ofstream output(report,std::ios::binary);output<<"{\"status\":\""<<(exit?"failed":"passed")<<"\",\"architecture\":\""<<(sizeof(void*)==8?"x64":"x86")<<"\",\"typing_performed\":false,\"registry_changed\":false,\"error\":\"";for(char c:error){if(c=='"' || c=='\\')output<<'\\';output<<c;}output<<"\"";if(diagnostic)output<<",\"language_bar_bindings\":"<<diagnostic->langBarHooks<<",\"badged_icons\":"<<diagnostic->iconBadges<<",\"menu_dots\":"<<diagnostic->menuBadges<<",\"read_events\":"<<diagnostic->menuReads;output<<"}\n";
  CoUninitialize();return exit;
}
