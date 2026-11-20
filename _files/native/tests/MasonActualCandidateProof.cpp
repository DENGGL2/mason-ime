#define NOMINMAX
#include <windows.h>
#include <msctf.h>
#include <shellapi.h>
#include <string>
#include <fstream>
#include <vector>
#include <stdexcept>
#include <cstring>
#include "MasonTestTextStore.h"
#pragma comment(lib,"user32.lib")
#pragma comment(lib,"ole32.lib")
#pragma comment(lib,"oleaut32.lib")
#pragma comment(lib,"uuid.lib")
#pragma comment(lib,"shell32.lib")

// A real, private SxS instance of the installed TSF client renders into this
// process's in-memory text store. No SendInput, profile activation, registration,
// default-language changes or access to another application's text is used.
namespace {
struct MotionData {
  DWORD size;LONG initialized,importHooks,attached,detached,liveWindows;
  LONG frames,opens,closes,selections,ticks,liveBuffers;
  LONG lastAlpha,lastScale,lastPhase,lastSelection,failures;
  LONG serverModules,clientModules,layerCalls,showCalls,positionCalls,exitWindows;
  LONG letterOpens,letterFrames,lastLetterAlpha,lastLetterScale,activeLetters;
  LONG candidateOpens,candidateFrames,lastCandidateAlpha,lastCandidateScale,activeCandidates;
};
struct TextData {DWORD size;LONG factory,d2d,layouts,marked,wic,hwnd,dxgi,dc,devices,contexts,compatible;
  LONG drawLayouts,drawMarkedLayouts,lastStart,lastLength,drawTexts,drawMarkedTexts;};
struct Sample {DWORD ms;LONG alpha,scale,phase,frames;};
struct KeyTiming {double tested,down,total;};
std::vector<KeyTiming> keyTimings;
ITfThreadMgrEx* manager=nullptr;
ITfDocumentMgr* document=nullptr;
ITfContext* context=nullptr;
ITfInputProcessorProfiles* profiles=nullptr;
ITfInputProcessorProfileMgr* profileManager=nullptr;
ITfKeyEventSink* keys=nullptr;
ITfKeystrokeMgr* observedManager=nullptr;
void** originalKeyTable=nullptr;void* keyTable[17]{};
LONG adviceResult=0;std::wstring clientPath;
TF_INPUTPROCESSORPROFILE original{};bool originalKnown=false;
MasonTestTextStore* store=nullptr;
bool activated=false;
MotionData* motion=nullptr;TextData* text=nullptr;
HWND editor=nullptr;
int stage=0,letter=0;ULONGLONG started=0,lastLetter=0;
LONG firstOpens=0,exitBefore=0;
std::vector<Sample> enter,exitTrace;
bool openingObserved=false,exitObserved=false,typingDidNotReopen=false;
bool overshootObserved=false;LONG firstAlpha=0,firstScale=0,maxScale=0;
LONG firstLetterOpens=0;bool newLetterObserved=false;
LONG firstCandidateOpens=0;bool candidateObserved=false;
LONG maxCandidateScale=0;
std::wstring reportPath;
void Require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void Check(HRESULT code,const char* message){if(FAILED(code))throw std::runtime_error(std::string(message)+" (HRESULT "+std::to_string(static_cast<long>(code))+")");}
const wchar_t* Adapter(){return sizeof(void*)==8?L"Mason64.dll":L"Mason32.dll";}
using Advice=HRESULT(STDMETHODCALLTYPE*)(ITfKeystrokeMgr*,TfClientId,ITfKeyEventSink*,BOOL);
HRESULT STDMETHODCALLTYPE ObserveAdvice(ITfKeystrokeMgr* object,TfClientId id,ITfKeyEventSink* sink,BOOL foreground){
  const HRESULT result=reinterpret_cast<Advice>(originalKeyTable[3])(object,id,sink,foreground);adviceResult=result;
  if(SUCCEEDED(result) && sink){
    HMODULE module=nullptr;void* function=(*reinterpret_cast<void***>(sink))[4];
    wchar_t path[MAX_PATH]{};
    if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(function),&module) && GetModuleFileNameW(module,path,MAX_PATH)){
      const wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;
      if(_wcsicmp(name,L"weasel.dll")==0 || _wcsicmp(name,L"weaselx64.dll")==0){
        if(keys)keys->Release();keys=sink;keys->AddRef();clientPath=path;
      }
    }
  }
  return result;
}
void ObserveManager(){
  Check(manager->QueryInterface(IID_ITfKeystrokeMgr,reinterpret_cast<void**>(&observedManager)),"Key manager missing");
  originalKeyTable=*reinterpret_cast<void***>(observedManager);std::memcpy(keyTable,originalKeyTable,sizeof(keyTable));
  keyTable[3]=reinterpret_cast<void*>(&ObserveAdvice);*reinterpret_cast<void***>(observedManager)=keyTable;
}
void RestoreManager(){if(observedManager){*reinterpret_cast<void***>(observedManager)=originalKeyTable;observedManager->Release();observedManager=nullptr;}}
void Counters(){
  HMODULE module=GetModuleHandleW(Adapter());
  if(module){motion=reinterpret_cast<MotionData*>(GetProcAddress(module,"MasonMotionDiagnostics"));
    text=reinterpret_cast<TextData*>(GetProcAddress(module,"MasonTextDiagnostics"));}
}
void Key(UINT code){
  LARGE_INTEGER frequency{},start{},testedAt{},downAt{},end{};
  QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&start);
  BOOL tested=FALSE,eaten=FALSE;
  const LPARAM down=1|(MapVirtualKeyW(code,MAPVK_VK_TO_VSC)<<16);
  Check(keys->OnTestKeyDown(context,code,down,&tested),"Real TSF TestKeyDown failed");
  QueryPerformanceCounter(&testedAt);
  Require(tested!=FALSE,"Real TSF did not accept a generated test key");
  Check(keys->OnKeyDown(context,code,down,&eaten),"Real TSF KeyDown failed");
  QueryPerformanceCounter(&downAt);
  Require(eaten!=FALSE,"Real TSF did not process a generated test key");
  BOOL ignored=FALSE;
  keys->OnTestKeyUp(context,code,down|(1LL<<30)|(1LL<<31),&ignored);
  keys->OnKeyUp(context,code,down|(1LL<<30)|(1LL<<31),&ignored);
  Counters();
  QueryPerformanceCounter(&end);
  const double ms=1000.0/frequency.QuadPart;
  keyTimings.push_back({(testedAt.QuadPart-start.QuadPart)*ms,(downAt.QuadPart-testedAt.QuadPart)*ms,(end.QuadPart-start.QuadPart)*ms});
}
void Initialize(HWND window){
  CLSID clsid{};Check(CLSIDFromString(L"{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}",&clsid),"CLSID parse failed");
  Check(CoCreateInstance(CLSID_TF_ThreadMgr,nullptr,CLSCTX_INPROC_SERVER,IID_ITfThreadMgrEx,reinterpret_cast<void**>(&manager)),"Thread manager creation failed");
  TfClientId client=0;
  ObserveManager();
  Check(manager->ActivateEx(&client,TF_TMAE_NOACTIVATEKEYBOARDLAYOUT),"Isolated thread manager activation failed");
  SetFocus(editor);
  Check(manager->CreateDocumentMgr(&document),"Document creation failed");
  store=new MasonTestTextStore(window);TfEditCookie cookie=0;
  Check(document->CreateContext(client,0,static_cast<ITextStoreACP*>(store),&context,&cookie),"Context creation failed");
  Check(document->Push(context),"Context push failed");Check(manager->SetFocus(document),"Test document focus failed");
  Check(CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,IID_ITfInputProcessorProfiles,reinterpret_cast<void**>(&profiles)),"Profile reader creation failed");
  Check(profiles->QueryInterface(IID_ITfInputProcessorProfileMgr,reinterpret_cast<void**>(&profileManager)),"Profile manager missing");
  TF_INPUTPROCESSORPROFILE mason{};bool found=false;
  for(LANGID language:{static_cast<LANGID>(0x0804),static_cast<LANGID>(0x0404)}){
    IEnumTfInputProcessorProfiles* enumeration=nullptr;
    if(FAILED(profileManager->EnumProfiles(language,&enumeration)))continue;
    TF_INPUTPROCESSORPROFILE candidate{};ULONG fetched=0;
    while(enumeration->Next(1,&candidate,&fetched)==S_OK && fetched==1){
      if(candidate.dwProfileType!=TF_PROFILETYPE_INPUTPROCESSOR || !IsEqualCLSID(candidate.clsid,clsid))continue;
      BSTR label=nullptr;
      if(SUCCEEDED(profiles->GetLanguageProfileDescription(candidate.clsid,language,candidate.guidProfile,&label)) && label){
        std::wstring name(label,SysStringLen(label));SysFreeString(label);
        if(name.find(L"Mason")!=std::wstring::npos || name.find(L"麻绳")!=std::wstring::npos){mason=candidate;found=true;break;}
      }
    }
    enumeration->Release();if(found)break;
  }
  Require(found,"The already installed and enabled Mason profile was not found");
  originalKnown=SUCCEEDED(profileManager->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&original));
  // Let Windows allocate the real TIP's service client ID. Capture its key sink
  // from this test process's own interface object; all advice calls are forwarded
  // unchanged. Generated input can then reach that real sink without foreground
  // keyboard injection or stealing focus from the user's current application.
  Check(profileManager->ActivateProfile(mason.dwProfileType,mason.langid,mason.clsid,mason.guidProfile,mason.hkl,0),"Current-test-thread Mason activation failed");activated=true;
  Counters();
}
void Trace(std::vector<Sample>& values){if(motion)values.push_back(Sample{static_cast<DWORD>(GetTickCount64()-started),motion->lastAlpha,motion->lastScale,motion->lastPhase,motion->frames});}
void Report(const char* status,const char* error=nullptr){
  std::ofstream file(reportPath,std::ios::binary);
  file<<"{\"status\":\""<<status<<"\",\"architecture\":\""<<(sizeof(void*)==8?"x64":"x86")<<"\",\"actual_tsf_client\":true,\"registry_changed\":false,\"input_committed\":false,\"default_input_changed\":false";
  if(error){file<<",\"error\":\"";for(const char* p=error;*p;p++){if(*p=='"'||*p=='\\')file<<'\\';file<<*p;}file<<'"';}
  file<<",\"key_advice_hresult\":"<<adviceResult<<",\"sink_captured\":"<<(keys?"true":"false");
  file<<",\"opening_observed\":"<<(openingObserved?"true":"false")<<",\"exit_observed\":"<<(exitObserved?"true":"false")<<",\"typing_did_not_reopen\":"<<(typingDidNotReopen?"true":"false");
  file<<",\"overshoot_observed\":"<<(overshootObserved?"true":"false")<<",\"first_alpha\":"<<firstAlpha<<",\"first_scale\":"<<firstScale<<",\"max_scale\":"<<maxScale;
  file<<",\"new_letter_observed\":"<<(newLetterObserved?"true":"false");
  file<<",\"candidate_content_observed\":"<<(candidateObserved?"true":"false");
  file<<",\"candidate_max_scale\":"<<maxCandidateScale;
  if(motion){file<<",\"motion\":{\"server_modules\":"<<motion->serverModules<<",\"client_modules\":"<<motion->clientModules<<",\"frames\":"<<motion->frames<<",\"opens\":"<<motion->opens<<",\"closes\":"<<motion->closes<<",\"exit_windows\":"<<motion->exitWindows<<",\"failures\":"<<motion->failures<<",\"live_windows\":"<<motion->liveWindows<<",\"live_buffers\":"<<motion->liveBuffers<<",\"letter_opens\":"<<motion->letterOpens<<",\"letter_frames\":"<<motion->letterFrames<<",\"active_letters\":"<<motion->activeLetters<<",\"candidate_opens\":"<<motion->candidateOpens<<",\"candidate_frames\":"<<motion->candidateFrames<<",\"active_candidates\":"<<motion->activeCandidates<<'}';}
  if(text)file<<",\"red_draws\":"<<text->drawMarkedLayouts+text->drawMarkedTexts;
  file<<",\"key_timings\":[";bool firstKey=true;
  for(const auto& key:keyTimings){if(!firstKey)file<<',';firstKey=false;file<<"{\"test_ms\":"<<key.tested<<",\"down_ms\":"<<key.down<<",\"total_ms\":"<<key.total<<'}';}file<<']';
  auto trace=[&](const char* name,const std::vector<Sample>& values){file<<",\""<<name<<"\":[";bool first=true;for(const auto& s:values){if(!first)file<<',';first=false;file<<"{\"ms\":"<<s.ms<<",\"alpha\":"<<s.alpha<<",\"scale\":"<<s.scale<<",\"phase\":"<<s.phase<<",\"frames\":"<<s.frames<<'}';}file<<']';};
  trace("enter_trace",enter);trace("exit_trace",exitTrace);file<<"}\n";
}
void Step(HWND window){
  const ULONGLONG now=GetTickCount64();
  if(stage==0){Initialize(window);started=now;stage=5;return;}
  if(stage==5){
    if(!keys){if(now-started>1500)throw std::runtime_error("Windows did not activate a real Mason key sink in the test thread");return;}
    RestoreManager();Check(keys->OnSetFocus(TRUE),"Real TSF focus failed");Key('N');if(motion){firstAlpha=motion->lastAlpha;firstScale=motion->lastScale;}
    // The first synchronous key can also load fonts and initialize the real
    // client. Measure the visible animation after that work has completed.
    started=GetTickCount64();stage=1;return;
  }
  if(stage==1){
    Counters();Trace(enter);
    if(motion){maxScale=std::max(maxScale,motion->lastScale);maxCandidateScale=std::max(maxCandidateScale,motion->lastCandidateScale);if(motion->lastScale>=1080)overshootObserved=true;}
    if(motion && motion->lastPhase==1 && motion->lastAlpha>=102 && motion->lastAlpha<255 && motion->lastScale>=900 && motion->lastScale<=1100)openingObserved=true;
    if(now-started<600)return;
    Require(motion && motion->size==sizeof(MotionData),"Real client did not load the new adapter");
    Require(motion->initialized==1 && motion->clientModules==1 && motion->serverModules==0,"Motion was not scoped to the real client module");
    Require(openingObserved,"The actual candidate popup did not animate its first bitmap");
    Require(firstAlpha==102 && firstScale==900,"The real first frame did not begin at alpha .4 and scale .9");
    Require(overshootObserved,"The real popup did not grow to 1.1 before settling");
    Require(motion->lastAlpha==255 && motion->lastScale==1000,"The actual candidate popup did not settle");
    Require(motion->letterOpens>0 && motion->letterFrames>0,"The real preedit glyphs did not animate");
    Require(motion->candidateOpens>0 && motion->candidateFrames>0,"The real Chinese candidate content did not animate");
    Require(maxCandidateScale==1000,"Real candidate content changed its native size");
    firstCandidateOpens=motion->candidateOpens;
    firstLetterOpens=motion->letterOpens;firstOpens=motion->opens;lastLetter=0;letter=0;started=now;stage=2;return;
  }
  if(stage==2){
    const char* rest="igapsuwo";
    if(rest[letter]){if(now-lastLetter>=45){Key(rest[letter++]-'a'+'A');lastLetter=GetTickCount64();if(motion->activeLetters>0 && motion->lastLetterAlpha>=102 && motion->lastLetterAlpha<255 && motion->lastAlpha==255)newLetterObserved=true;if(motion->activeCandidates>0 && motion->lastCandidateAlpha>=204 && motion->lastCandidateAlpha<255 && motion->lastAlpha==255)candidateObserved=true;}return;}
    if(now-lastLetter<400)return;
    typingDidNotReopen=motion->opens==firstOpens;
    Require(typingDidNotReopen,"Typing into an existing composition replayed the popup animation");
    Require(newLetterObserved && motion->letterOpens>firstLetterOpens,"Typing did not animate each new real preedit letter");
    Require(motion->activeLetters==0,"Real preedit animations did not settle after typing stopped");
    Require(candidateObserved && motion->candidateOpens>firstCandidateOpens,"Changed real candidates did not animate during typing");
    Require(motion->activeCandidates==0,"Real candidate content did not settle after typing stopped");
    Require(motion->frames>1 && motion->failures==0,"Actual candidate rendering failed");
    exitBefore=motion->exitWindows;Key(VK_ESCAPE);started=now;stage=3;return;
  }
  if(stage==3){
    Trace(exitTrace);
    if(motion->exitWindows==exitBefore && motion->liveWindows==0 && motion->liveBuffers==0)exitObserved=true;
    if(now-started<350)return;
    Require(exitObserved,"TSF teardown did not close the simple popup immediately");
    Require(motion->liveWindows==0 && motion->liveBuffers==0,"Real TSF teardown leaked candidate state or surfaces");
    Require(motion->failures==0,"Actual layered-window presentation failed");
    Report("passed");KillTimer(window,1);PostMessageW(window,WM_CLOSE,0,0);stage=4;
  }
}
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM wparam,LPARAM lparam){
  if(message==WM_CREATE){editor=CreateWindowW(L"EDIT",L"",WS_CHILD|WS_VISIBLE|ES_MULTILINE,0,0,400,120,window,nullptr,GetModuleHandleW(nullptr),nullptr);return 0;}
  if(message==WM_TIMER){try{Step(window);}catch(const std::exception& error){Report("failed",error.what());KillTimer(window,1);PostMessageW(window,WM_CLOSE,0,0);}return 0;}
  if(message==WM_DESTROY){PostQuitMessage(0);return 0;}
  return DefWindowProcW(window,message,wparam,lparam);
}
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int){
  int argc=0;LPWSTR* args=CommandLineToArgvW(GetCommandLineW(),&argc);if(argc!=2)return 2;reportPath=args[1];LocalFree(args);
  CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
  wchar_t executable[MAX_PATH]{};GetModuleFileNameW(nullptr,executable,MAX_PATH);
  std::wstring directory=executable;directory=directory.substr(0,directory.find_last_of(L'\\')+1);
  // Loading the test helper first also covers Windows' installed client loader,
  // which can use System32 directly instead of a private COM activation context.
  Require(LoadLibraryW((directory+Adapter()).c_str())!=nullptr,"Test adapter preload failed");
  WNDCLASSW cls{};cls.lpfnWndProc=&Procedure;cls.hInstance=instance;cls.lpszClassName=L"Mason.ActualCandidateProof";RegisterClassW(&cls);
  HWND window=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,cls.lpszClassName,L"",WS_POPUP,-16000,-16000,440,220,nullptr,nullptr,instance,nullptr);
  if(!window){Report("failed","Test window creation failed");return 1;}
  ShowWindow(window,SW_SHOWNOACTIVATE);ShowWindow(window,SW_SHOWNOACTIVATE);SetTimer(window,1,16,nullptr);
  MSG message{};while(GetMessageW(&message,nullptr,0,0)>0){TranslateMessage(&message);DispatchMessageW(&message);}
  RestoreManager();if(keys){keys->OnSetFocus(FALSE);keys->Release();}
  if(activated && originalKnown)profileManager->ActivateProfile(original.dwProfileType,original.langid,original.clsid,original.guidProfile,original.hkl,0);
  if(profileManager)profileManager->Release();if(profiles)profiles->Release();
  if(document){document->Pop(TF_POPF_ALL);document->Release();}
  if(context)context->Release();if(store)store->Release();
  if(manager){manager->Deactivate();manager->Release();}
  CoUninitialize();return stage==4?0:1;
}
