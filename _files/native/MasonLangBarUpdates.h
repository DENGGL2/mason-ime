#pragma once
#include <msctf.h>
#include <unordered_map>
#include <memory>
#include "MasonUpdateIndicators.h"

namespace MasonLangBarUpdates {
using ReleaseFunction=ULONG(STDMETHODCALLTYPE*)(void*);
using IconFunction=HRESULT(STDMETHODCALLTYPE*)(void*,HICON*);
using MenuFunction=HRESULT(STDMETHODCALLTYPE*)(void*,ITfMenu*);
struct Binding {
  ITfLangBarItemButton* button=nullptr;
  ITfSource* source=nullptr;
  void** originalButton=nullptr;
  void** originalSource=nullptr;
  void* buttonTable[12]{};
  void* sourceTable[5]{};
  DWORD thread=0;
  unsigned releasing=0;
};
inline SRWLOCK bindingsLock=SRWLOCK_INIT;
inline std::unordered_map<void*,Binding*> bindings;
inline thread_local UINT_PTR timer=0;
inline thread_local unsigned ticks=0;
inline thread_local MasonUpdateIndicators::Snapshot threadState;

// This is the prefix in upstream Weasel 0.17.4 LanguageBar.h. It is accepted
// only after both COM interface offsets and GetInfo's GUID match at runtime.
struct WeaselPrefix {void* buttonTable;void* sourceTable;GUID guid;void* textService;ITfLangBarItemSink* sink;LONG references;DWORD status;};
inline bool Prefix(void* object,WeaselPrefix* result) noexcept {
  __try{*result=*reinterpret_cast<WeaselPrefix*>(object);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
inline ITfLangBarItemSink* RetainSink(void* object) noexcept {
  WeaselPrefix value{};if(!Prefix(object,&value) || !value.sink)return nullptr;
  __try{value.sink->AddRef();return value.sink;}__except(EXCEPTION_EXECUTE_HANDLER){return nullptr;}
}
inline void UpdateSink(ITfLangBarItemSink* sink) noexcept {
  if(!sink)return;__try{sink->OnUpdate(TF_LBI_ICON);sink->Release();}__except(EXCEPTION_EXECUTE_HANDLER){}
}
inline Binding* Find(void* object){auto found=bindings.find(object);return found==bindings.end()?nullptr:found->second;}
inline ULONG STDMETHODCALLTYPE Release(void* object) noexcept {
  AcquireSRWLockExclusive(&bindingsLock);Binding* binding=Find(object);ReleaseFunction original=binding?reinterpret_cast<ReleaseFunction>((object==binding->button?binding->originalButton:binding->originalSource)[2]):nullptr;if(binding)++binding->releasing;ReleaseSRWLockExclusive(&bindingsLock);
  if(!original)return 0;ULONG count=original(object);AcquireSRWLockExclusive(&bindingsLock);binding=Find(object);if(binding){if(!count){bindings.erase(binding->button);bindings.erase(binding->source);delete binding;}else if(binding->releasing)--binding->releasing;}ReleaseSRWLockExclusive(&bindingsLock);return count;
}
inline HRESULT STDMETHODCALLTYPE GetIcon(void* object,HICON* icon) noexcept {
  AcquireSRWLockShared(&bindingsLock);Binding* binding=Find(object);IconFunction original=binding?reinterpret_cast<IconFunction>(binding->originalButton[10]):nullptr;ReleaseSRWLockShared(&bindingsLock);
  if(!original)return E_UNEXPECTED;HRESULT result=original(object,icon);
  try{if(SUCCEEDED(result) && icon && *icon && MasonUpdateIndicators::Current().unread){HICON badge=MasonUpdateIndicators::Badge(*icon);if(badge)*icon=badge;}}catch(...){ }
  return result;
}

class MenuProxy : public ITfMenu {
  LONG references=1;
  ITfMenu* original;
  MasonUpdateIndicators::Snapshot state;
public:
  MenuProxy(ITfMenu* value,const MasonUpdateIndicators::Snapshot& snapshot):original(value),state(snapshot){original->AddRef();}
  ~MenuProxy(){original->Release();}
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** object) override {if(!object)return E_POINTER;*object=nullptr;if(id==__uuidof(IUnknown) || id==__uuidof(ITfMenu)){*object=static_cast<ITfMenu*>(this);AddRef();return S_OK;}return original->QueryInterface(id,object);}
  ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&references);}
  ULONG STDMETHODCALLTYPE Release() override {ULONG count=InterlockedDecrement(&references);if(!count)delete this;return count;}
  HRESULT STDMETHODCALLTYPE AddMenuItem(UINT id,DWORD flags,HBITMAP bitmap,HBITMAP mask,const WCHAR* text,ULONG length,ITfMenu** submenu) override {
    if((id==40003 && state.application) || (id==40005 && state.dictionary)){MasonUpdateIndicators::MenuBitmaps();bitmap=MasonUpdateIndicators::menuDot;mask=MasonUpdateIndicators::menuDotMask;InterlockedIncrement(&MasonUpdateDiagnostics.menuBadges);}
    return original->AddMenuItem(id,flags,bitmap,mask,text,length,submenu);
  }
};
inline HRESULT STDMETHODCALLTYPE InitMenu(void* object,ITfMenu* menu) noexcept {
  AcquireSRWLockShared(&bindingsLock);Binding* binding=Find(object);MenuFunction original=binding?reinterpret_cast<MenuFunction>(binding->originalButton[8]):nullptr;ReleaseSRWLockShared(&bindingsLock);if(!original)return E_UNEXPECTED;
  try{auto state=MasonUpdateIndicators::Read();auto* proxy=new MenuProxy(menu,state);HRESULT result=original(object,proxy);proxy->Release();if(SUCCEEDED(result))MasonUpdateIndicators::Seen(state);return result;}catch(...){return original(object,menu);}
}

inline bool Bind(ITfLangBarItemButton* button,ITfSource* source){
  AcquireSRWLockShared(&bindingsLock);const bool exists=Find(button)!=nullptr;ReleaseSRWLockShared(&bindingsLock);if(exists)return true;
  void** table=*reinterpret_cast<void***>(button);void** sourceTable=*reinterpret_cast<void***>(source);
  if(!MasonUpdateIndicators::OwnedAddress(table[10]) || !MasonUpdateIndicators::OwnedAddress(sourceTable[3]))return false;
  TF_LANGBARITEMINFO info{};if(FAILED(button->GetInfo(&info)))return false;WeaselPrefix prefix{};
  DWORD status=0;if(FAILED(button->GetStatus(&status)) || reinterpret_cast<BYTE*>(source)!=reinterpret_cast<BYTE*>(button)+sizeof(void*) || !Prefix(button,&prefix) || prefix.guid!=info.guidItem || prefix.references<=0 || prefix.references>1000000 || prefix.status!=status)return false;
  auto value=std::make_unique<Binding>();value->button=button;value->source=source;value->thread=GetCurrentThreadId();value->originalButton=table;value->originalSource=sourceTable;
  std::memcpy(value->buttonTable,table,sizeof(value->buttonTable));std::memcpy(value->sourceTable,sourceTable,sizeof(value->sourceTable));
  value->buttonTable[2]=reinterpret_cast<void*>(&Release);value->buttonTable[8]=reinterpret_cast<void*>(&InitMenu);value->buttonTable[10]=reinterpret_cast<void*>(&GetIcon);value->sourceTable[2]=reinterpret_cast<void*>(&Release);
  AcquireSRWLockExclusive(&bindingsLock);if(Find(button)){ReleaseSRWLockExclusive(&bindingsLock);return true;}Binding* raw=value.release();bindings[button]=raw;bindings[source]=raw;
  InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(button),raw->buttonTable);InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(source),raw->sourceTable);ReleaseSRWLockExclusive(&bindingsLock);
  InterlockedIncrement(&MasonUpdateDiagnostics.langBarHooks);return true;
}
inline void Refresh(){
  std::vector<ITfLangBarItemButton*> items;const DWORD thread=GetCurrentThreadId();AcquireSRWLockShared(&bindingsLock);
  for(const auto& row:bindings)if(row.first==row.second->button && row.second->thread==thread && !row.second->releasing){row.second->button->AddRef();items.push_back(row.second->button);}ReleaseSRWLockShared(&bindingsLock);
  for(auto* button:items){UpdateSink(RetainSink(button));button->Release();InterlockedIncrement(&MasonUpdateDiagnostics.iconRefreshes);}
}
inline void FindItems(){
  HMODULE module=GetModuleHandleW(L"msctf.dll");if(!module)return;using GetManager=HRESULT(WINAPI*)(ITfThreadMgr**);auto get=reinterpret_cast<GetManager>(GetProcAddress(module,"TF_GetThreadMgr"));if(!get)return;
  ITfThreadMgr* manager=nullptr;if(FAILED(get(&manager)) || !manager)return;ITfLangBarItemMgr* itemManager=nullptr;HRESULT result=manager->QueryInterface(__uuidof(ITfLangBarItemMgr),reinterpret_cast<void**>(&itemManager));manager->Release();if(FAILED(result) || !itemManager)return;
  IEnumTfLangBarItems* enumeration=nullptr;result=itemManager->EnumItems(&enumeration);itemManager->Release();if(FAILED(result) || !enumeration)return;
  ITfLangBarItem* item=nullptr;ULONG count=0;unsigned visited=0;
  while(visited++<64 && enumeration->Next(1,&item,&count)==S_OK && count){ITfLangBarItemButton* button=nullptr;ITfSource* source=nullptr;
    if(SUCCEEDED(item->QueryInterface(__uuidof(ITfLangBarItemButton),reinterpret_cast<void**>(&button)))){
      if(SUCCEEDED(item->QueryInterface(__uuidof(ITfSource),reinterpret_cast<void**>(&source)))){Bind(button,source);source->Release();}button->Release();
    }item->Release();item=nullptr;
  }enumeration->Release();
}
inline void CALLBACK Tick(HWND,UINT,UINT_PTR,DWORD){try{const auto current=MasonUpdateIndicators::Read();if(++ticks==1)SetTimer(nullptr,timer,1500,&Tick);if(ticks<=3 || ticks%10==0)FindItems();if(!MasonUpdateIndicators::Same(threadState,current)){threadState=current;Refresh();}}catch(...){} }
inline void InstallOnThread(){
  // Enumerate TSF items after activation/factory creation has returned. Calling
  // back into the thread manager inside those calls can re-enter TSF locks.
  if(timer)return;try{MasonUpdateIndicators::refreshLanguageBar=&Refresh;timer=SetTimer(nullptr,0,100,&Tick);}catch(...){}
}
}
