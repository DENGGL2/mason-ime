#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <d2d1.h>
#include <dwrite.h>
#include <string>
#include <fstream>
#include <stdexcept>
#include <cmath>
#include <vector>
#include <cstring>
#pragma comment(lib,"user32.lib")
#pragma comment(lib,"gdi32.lib")
#pragma comment(lib,"ole32.lib")

struct Diagnostics {
  DWORD size;LONG initialized,importHooks,attached,detached,liveWindows;
  LONG frames,opens,closes,selections,ticks,liveBuffers;
  LONG lastAlpha,lastScale,lastPhase,lastSelection,failures;
  LONG serverModules,clientModules,layerCalls,showCalls,positionCalls,exitWindows;
  LONG letterOpens,letterFrames,lastLetterAlpha,lastLetterScale,activeLetters;
  LONG candidateOpens,candidateFrames,lastCandidateAlpha,lastCandidateScale,activeCandidates;
};
struct TextDiagnostics {
  DWORD size;LONG factories,otherFactories,layouts,markedLayouts,wic,hwnd,dxgi,dc,devices,contexts,compatible;
  LONG drawLayouts,drawMarkedLayouts,lastMarkedStart,lastMarkedLength,drawTexts,drawMarkedTexts;
};
using D2DFactory=HRESULT(WINAPI*)(D2D1_FACTORY_TYPE,REFIID,const D2D1_FACTORY_OPTIONS*,void**);
using WriteFactory=HRESULT(WINAPI*)(DWRITE_FACTORY_TYPE,REFIID,IUnknown**);
static int lastMouseX=-1,lastMouseY=-1;
static LRESULT CALLBACK WindowProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam){
  if(message==WM_LBUTTONUP){lastMouseX=static_cast<short>(LOWORD(lparam));lastMouseY=static_cast<short>(HIWORD(lparam));return 0;}
  return DefWindowProcW(window,message,wparam,lparam);
}
static void Require(bool condition,const char* text){if(!condition)throw std::runtime_error(text);}
static void Check(HRESULT hr,const char* text){Require(SUCCEEDED(hr),text);}
static void Pump(unsigned milliseconds){
  const ULONGLONG end=GetTickCount64()+milliseconds;
  do{
    MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
    MsgWaitForMultipleObjects(0,nullptr,FALSE,1,QS_ALLINPUT);
  }while(GetTickCount64()<end);
}
static HWND MakeWindow(const wchar_t* className,bool layered=true){
  WNDCLASSW cls{};cls.lpfnWndProc=WindowProc;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=className;
  RegisterClassW(&cls);
  return CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE|(layered?WS_EX_LAYERED:WS_EX_TRANSPARENT),className,nullptr,WS_POPUP,-18000,-18000,420,150,nullptr,nullptr,cls.hInstance,nullptr);
}
struct EntrySearch {HWND owner,entry=nullptr;};
static BOOL CALLBACK FindEntry(HWND window,LPARAM value){
  auto* search=reinterpret_cast<EntrySearch*>(value);wchar_t name[80]{};
  if(GetWindow(window,GW_OWNER)==search->owner && GetClassNameW(window,name,80) && wcscmp(name,L"Mason.CandidateMotion.Entry")==0){search->entry=window;return FALSE;}
  return TRUE;
}
static HWND EntryFor(HWND owner){EntrySearch search{owner};EnumThreadWindows(GetCurrentThreadId(),FindEntry,reinterpret_cast<LPARAM>(&search));return search.entry;}
struct Paint {
  HDC dc=nullptr;HBITMAP bitmap=nullptr;HGDIOBJ previous=nullptr;BYTE* pixels=nullptr;
  ID2D1Factory* factory=nullptr;IDWriteFactory* write=nullptr;ID2D1DCRenderTarget* target=nullptr;IDWriteTextFormat* format=nullptr;ID2D1SolidColorBrush* brush=nullptr;
  Paint(D2DFactory makeD2D,WriteFactory makeWrite){
    dc=CreateCompatibleDC(nullptr);BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=420;info.bmiHeader.biHeight=-150;
    info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
    bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,reinterpret_cast<void**>(&pixels),nullptr,0);Require(bitmap && pixels,"CreateDIBSection failed");previous=SelectObject(dc,bitmap);
    Check(makeD2D(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory),nullptr,reinterpret_cast<void**>(&factory)),"D2D factory failed");
    Check(makeWrite(DWRITE_FACTORY_TYPE_ISOLATED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(&write)),"DWrite factory failed");
    auto props=D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED));
    Check(factory->CreateDCRenderTarget(&props,&target),"CreateDCRenderTarget failed");const RECT rect{0,0,420,150};Check(target->BindDC(dc,&rect),"BindDC failed");
    Check(write->CreateTextFormat(L"Microsoft YaHei UI",nullptr,DWRITE_FONT_WEIGHT_NORMAL,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,20,L"zh-cn",&format),"CreateTextFormat failed");
    Check(target->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White),&brush),"CreateSolidColorBrush failed");
  }
  ~Paint(){
    if(brush)brush->Release();if(format)format->Release();if(target)target->Release();if(write)write->Release();if(factory)factory->Release();
    if(dc && previous)SelectObject(dc,previous);if(bitmap)DeleteObject(bitmap);if(dc)DeleteDC(dc);
  }
  void Text(const std::wstring& text,float x,float y){
    IDWriteTextLayout* layout=nullptr;Check(write->CreateTextLayout(text.c_str(),static_cast<UINT32>(text.size()),format,390,40,&layout),"CreateTextLayout failed");
    target->DrawTextLayout(D2D1::Point2F(x,y),layout,brush,static_cast<D2D1_DRAW_TEXT_OPTIONS>(4));layout->Release();
  }
  void Frame(HWND window,const std::wstring& preedit,int selected,const std::vector<std::wstring>& words={L"你告诉我",L"你好",L"中文"}){
    // Like Weasel, paint the panel background into its DIB and bind that DC
    // before the DirectWrite batch, so preedit capture sees the actual backdrop.
    for(size_t i=0;i<static_cast<size_t>(420)*150;i++){pixels[i*4]=pixels[i*4+1]=pixels[i*4+2]=51;pixels[i*4+3]=255;}
    for(int y=60;y<105;y++)for(int x=10+selected*130;x<130+selected*130;x++){auto* pixel=pixels+(y*420+x)*4;pixel[0]=140;pixel[1]=pixel[2]=89;}
    const RECT bounds{0,0,420,150};Check(target->BindDC(dc,&bounds),"Frame BindDC failed");
    target->BeginDraw();target->Clear(D2D1::ColorF(.20f,.20f,.20f,1));
    brush->SetColor(D2D1::ColorF(.35f,.35f,.55f,1));target->FillRectangle(D2D1::RectF(10.f+selected*130.f,60,130.f+selected*130.f,105),brush);
    brush->SetColor(D2D1::ColorF(D2D1::ColorF::White));Text(preedit,12,12);Text(L"1",2,65);Text(L"2",135,65);Text(L"3",265,65);Text(words[0],15,65);Text(words[1],148,65);Text(words[2],278,65);
    Check(target->EndDraw(),"EndDraw failed");
    SIZE size{420,150};POINT position{-18000,-18000},origin{0,0};BLENDFUNCTION blend{AC_SRC_OVER,0,255,AC_SRC_ALPHA};
    Require(UpdateLayeredWindow(window,nullptr,&position,&size,dc,&origin,0,&blend,ULW_ALPHA)!=FALSE,"UpdateLayeredWindow failed");
  }
};
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
  int argc=0;LPWSTR* args=CommandLineToArgvW(GetCommandLineW(),&argc);if(argc!=3)return 2;
  std::ofstream report(args[2],std::ios::binary);HWND window=nullptr,unrelated=nullptr;int exit=1;
  Diagnostics* diagnostics=nullptr;std::vector<std::string> passed;
  try{
    Require(SUCCEEDED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)),"COM initialization failed");
    HMODULE adapter=LoadLibraryW(args[1]);Require(adapter!=nullptr,"Motion DLL could not be loaded");
    auto makeD2D=reinterpret_cast<D2DFactory>(GetProcAddress(adapter,"D2D1CreateFactory"));auto makeWrite=reinterpret_cast<WriteFactory>(GetProcAddress(adapter,"DWriteCreateFactory"));
    diagnostics=reinterpret_cast<Diagnostics*>(GetProcAddress(adapter,"MasonMotionDiagnostics"));
    auto text=reinterpret_cast<TextDiagnostics*>(GetProcAddress(adapter,"MasonTextDiagnostics"));
    Require(makeD2D && makeWrite && diagnostics && diagnostics->size==sizeof(Diagnostics),"Motion diagnostics or factory export missing");
    {
      Paint paint(makeD2D,makeWrite);
      Require(diagnostics->initialized==1 && diagnostics->importHooks==7,"The server import hooks did not install");
      // Warm the font/render-target caches before measuring window-owned GDI
      // resources. These caches are deliberately retained by DirectWrite.
      unrelated=MakeWindow(L"MasonUnrelatedMotionProof");paint.Frame(unrelated,L"nigap\x2063" L"suwo",0);DestroyWindow(unrelated);unrelated=nullptr;
      const DWORD baseline=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);
      window=MakeWindow(L"ATL:MasonMotionProof",false);Require(window!=nullptr,"Candidate window creation failed");
      ShowWindow(window,SW_SHOWNA);Pump(110);ShowWindow(window,SW_HIDE);Require(!IsWindowVisible(window),"A candidate with no frame could not be hidden");
      ShowWindow(window,SW_SHOWNA);Pump(110);
      SetWindowLongPtrW(window,GWL_EXSTYLE,(GetWindowLongPtrW(window,GWL_EXSTYLE)&~WS_EX_TRANSPARENT)|WS_EX_LAYERED);
      paint.Frame(window,L"nihao",0);
      Require(diagnostics->lastAlpha==102 && diagnostics->lastScale==900,"The first bitmap did not start at alpha .4 and scale .9");Pump(35);
      Require(diagnostics->lastAlpha>=102 && diagnostics->lastAlpha<255 && diagnostics->lastScale>=900 && diagnostics->lastScale<=1100,"Show before the first bitmap skipped the opening animation");
      Pump(330);ShowWindow(window,SW_HIDE);Pump(220);
      passed.push_back("first_bitmap_starts_the_clock");
      paint.Frame(window,L"nihao",0);Require(!IsWindowVisible(window) && diagnostics->lastAlpha==0,"A hidden candidate frame was made visible");
      ShowWindow(window,SW_SHOWNA);Pump(45);
      Require(IsWindowVisible(window) && diagnostics->lastAlpha>=102 && diagnostics->lastAlpha<255 && diagnostics->lastScale>=900 && diagnostics->lastScale<=1100,"Candidate opening was not animated");
      LONG peak=diagnostics->lastScale;bool expanded=false;
      for(int sample=0;sample<54;sample++){Pump(5);peak=std::max(peak,diagnostics->lastScale);if(HWND visual=EntryFor(window)){RECT rect{};GetWindowRect(visual,&rect);if(rect.right-rect.left>440 && rect.bottom-rect.top>155)expanded=true;}}
      Require(peak>=1080 && peak<=1100 && expanded,"The 1.1 scale peak was cropped or not rendered");
      passed.push_back("alpha_04_to_1_and_unclipped_scale_09_11_1");
      Pump(60);Require(diagnostics->lastAlpha==255 && diagnostics->lastScale==1000 && diagnostics->lastPhase==2 && !EntryFor(window),"Candidate opening did not settle");
      Require(diagnostics->letterOpens>0 && diagnostics->letterFrames>0,"Actual glyph pixels were not animated");
      const LONG lettersBefore=diagnostics->letterOpens,windowOpensBefore=diagnostics->opens;
      paint.Frame(window,L"nihaop",0);
      Require(diagnostics->letterOpens==lettersBefore+1 && diagnostics->activeLetters==1,"Appending a letter replayed the existing letters");
      Require(diagnostics->lastLetterAlpha==102 && diagnostics->lastLetterScale==900 && diagnostics->lastAlpha==255 && diagnostics->lastScale==1000,"New letter animation did not start independently");
      Pump(80);Require(diagnostics->activeLetters==1 && diagnostics->lastLetterAlpha>102 && diagnostics->lastLetterScale>=1080,"The new letter did not fade and grow");
      Pump(160);Require(diagnostics->activeLetters==0,"Letter animation did not finish within 200 ms");
      paint.Frame(window,L"niha",0);Require(diagnostics->letterOpens==lettersBefore+1 && diagnostics->activeLetters==0,"Backspace replayed old letters");
      paint.Frame(window,L"nihao",0);paint.Frame(window,L"nihaow",0);
      Require(diagnostics->letterOpens==lettersBefore+3 && diagnostics->activeLetters==2 && diagnostics->opens==windowOpensBefore,"Rapid typing stacked or replayed animations");
      Pump(240);Require(diagnostics->activeLetters==0,"Rapid typing left an animated glyph behind");
      passed.push_back("new_letters_animate_independently_and_backspace_is_immediate");
      const LONG candidatesBefore=diagnostics->candidateOpens;
      const std::vector<std::wstring> mixed{L"麻绳",L"office",L"\U0001F600"};
      paint.Frame(window,L"nihaow",0,mixed);
      Require(diagnostics->candidateOpens==candidatesBefore+3 && diagnostics->activeCandidates==3,"Chinese, English and emoji candidates did not animate as content items");
      Require(diagnostics->lastCandidateAlpha==204 && diagnostics->lastCandidateScale==1000,"Candidate content did not start at opacity .8 and its native size");
      Pump(80);Require(diagnostics->lastCandidateAlpha>204 && diagnostics->lastCandidateScale==1000,"Candidate content faded with an unwanted scale change");
      paint.Frame(window,L"nihaow",1,mixed);Require(diagnostics->candidateOpens==candidatesBefore+3,"Changing highlight replayed candidate content");
      for(int sample=0;sample<24;sample++){
        Pump(10);Require(diagnostics->lastCandidateScale==1000,"Candidate content changed size during its opacity animation");
      }
      Require(diagnostics->activeCandidates==0,"Candidate content did not settle within 200 ms");
      passed.push_back("candidate_opacity_08_to_1_at_native_size");
      auto family=mixed;family[2]=L"\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
      paint.Frame(window,L"nihaow",1,family);
      Require(diagnostics->candidateOpens==candidatesBefore+4 && diagnostics->activeCandidates==1,"Replacing an emoji replayed unchanged candidates or split the family emoji");
      Pump(240);Require(diagnostics->activeCandidates==0,"A combined emoji retained its animation");
      passed.push_back("candidate_chinese_english_and_complete_emoji_animation");
      const LONG selectedBefore=diagnostics->selections;
      const LONG framesBefore=diagnostics->frames;paint.Frame(window,L"nihao",1);paint.Frame(window,L"nihao",2);
      Require(diagnostics->selections==selectedBefore && diagnostics->frames==framesBefore+2 && diagnostics->lastAlpha==255,"Candidate selection was delayed");
      passed.push_back("selection_is_immediate");
      const LONG selectedAfter=diagnostics->selections;
      paint.Frame(window,L"nigapsuwo",0);Require(diagnostics->selections==selectedAfter,"Typing a different pinyin replayed a selection animation");
      passed.push_back("typed_text_is_immediate");
      paint.Frame(window,L"nigap\x2063" L"suwo",0);Require(text->markedLayouts>0 && text->drawMarkedLayouts>0 && text->lastMarkedStart==4,"Red correction glyph rendering regressed");
      passed.push_back("red_correction_glyph");
      ShowWindow(window,SW_HIDE);Require(!IsWindowVisible(window) && !EntryFor(window),"Candidate close was not immediate");
      ShowWindow(window,SW_SHOWNA);Pump(130);Require(IsWindowVisible(window) && diagnostics->lastAlpha>=102,"A rapid hide/show lost the window");
      Pump(230);Require(diagnostics->lastAlpha==255 && diagnostics->lastScale==1000,"A rapid hide/show did not settle");
      Require((GetWindowLongPtrW(window,GWL_EXSTYLE)&WS_EX_TRANSPARENT)==0,"A reopened candidate kept click-through enabled");
      passed.push_back("hide_show_reversal");
      // MoveTo can reposition without repainting. Timer presentation must not
      // put the candidate back at its previous caret position.
      ShowWindow(window,SW_HIDE);Pump(15);ShowWindow(window,SW_SHOWNA);
      SetWindowPos(window,nullptr,-17000,-17100,0,0,SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE);Pump(140);
      RECT moved{};GetWindowRect(window,&moved);Require(moved.left==-17000 && moved.top==-17100,"Motion presentation reverted a caret move");
      passed.push_back("caret_position_is_preserved");
      SetWindowPos(window,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_HIDEWINDOW);Pump(220);Require(!IsWindowVisible(window),"SWP_HIDEWINDOW did not finish hiding");
      SetWindowPos(window,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_SHOWWINDOW);Pump(30);
      const double scale=diagnostics->lastScale/1000.0;
      const int x=static_cast<int>(std::lround(35*scale));
      const int y=static_cast<int>(std::lround(75*scale));
      HWND visual=EntryFor(window);Require(visual!=nullptr,"The popup visual is missing");
      Require((GetWindowLongPtrW(visual,GWL_EXSTYLE)&WS_EX_NOACTIVATE)!=0,"The popup visual can take keyboard focus");
      SendMessageW(visual,WM_LBUTTONUP,0,MAKELPARAM(x,y));Require(std::abs(lastMouseX-35)<=2 && std::abs(lastMouseY-75)<=2,"Scaled candidate clicks used unscaled geometry");
      passed.push_back("scaled_pointer_coordinates");
      Pump(340);unrelated=MakeWindow(L"MasonUnrelatedMotionProof");const LONG attached=diagnostics->attached;
      paint.Frame(unrelated,L"unrelated",0);ShowWindow(unrelated,SW_SHOWNA);Pump(30);Require(diagnostics->attached==attached,"Motion changed an unrelated popup");
      ShowWindow(unrelated,SW_HIDE);Require(!IsWindowVisible(unrelated),"An unrelated popup was forced to animate");DestroyWindow(unrelated);unrelated=nullptr;
      passed.push_back("unrelated_windows_are_untouched");
      const LONG exits=diagnostics->exitWindows;ShowWindow(window,SW_HIDE);DestroyWindow(window);window=nullptr;
      Require(diagnostics->exitWindows==exits && diagnostics->liveWindows==0,"Engine teardown retained an unwanted dismissal surface");
      Require(diagnostics->liveWindows==0 && diagnostics->liveBuffers==0,"Destroyed candidates leaked surfaces or window state");
      Require(diagnostics->activeCandidates==0,"Destroyed candidates retained animated content");
      const LONG finalTicks=diagnostics->ticks;Pump(80);Require(diagnostics->ticks==finalTicks,"A destroyed candidate retained its timer");
      Require(GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS)<=baseline,"Destroyed candidates leaked GDI objects");
      Require(diagnostics->failures==0,"A layered-window presentation failed");
      passed.push_back("destroy_cleans_timers_and_surfaces");
      passed.push_back("engine_teardown_and_visual_close_are_immediate");
      DWORD handlesBefore=0,handlesAfter=0;
      Require(GetProcessHandleCount(GetCurrentProcess(),&handlesBefore)!=FALSE,"Process handle baseline missing");
      for(int cycle=0;cycle<8;cycle++){
        window=MakeWindow(L"ATL:MasonMotionProof");Require(window!=nullptr,"Rapid candidate recreation failed");
        paint.Frame(window,L"nihao",0);ShowWindow(window,SW_SHOWNA);Pump(cycle%2?20:2);
        DestroyWindow(window);window=nullptr;
        const LONG endedTicks=diagnostics->ticks;Pump(35);
        Require(diagnostics->ticks==endedTicks,"A canceled frame message animated a destroyed window");
        Require(diagnostics->liveWindows==0 && diagnostics->liveBuffers==0,"Rapid recreation retained native resources");
      }
      Require(GetProcessHandleCount(GetCurrentProcess(),&handlesAfter)!=FALSE && handlesAfter<=handlesBefore,"Frame scheduling leaked timer, event or thread handles");
      passed.push_back("rapid_recreation_discards_late_frames_and_releases_thread_handles");
    }
    report<<"{\"status\":\"passed\",\"tests\":[";for(size_t i=0;i<passed.size();i++){if(i)report<<',';report<<'"'<<passed[i]<<'"';}
    report<<"],\"frames\":"<<diagnostics->frames<<",\"timer_ticks\":"<<diagnostics->ticks<<",\"opens\":"<<diagnostics->opens<<",\"closes\":"<<diagnostics->closes<<",\"selection_changes\":"<<diagnostics->selections<<",\"live_windows\":"<<diagnostics->liveWindows<<",\"live_buffers\":"<<diagnostics->liveBuffers<<",\"presentation_failures\":"<<diagnostics->failures<<",\"letter_opens\":"<<diagnostics->letterOpens<<",\"letter_frames\":"<<diagnostics->letterFrames<<",\"active_letters\":"<<diagnostics->activeLetters<<",\"candidate_opens\":"<<diagnostics->candidateOpens<<",\"candidate_frames\":"<<diagnostics->candidateFrames<<",\"active_candidates\":"<<diagnostics->activeCandidates<<"}";exit=0;
  }catch(const std::exception& error){report<<"{\"status\":\"failed\",\"error\":\""<<error.what()<<"\",\"completed\":"<<passed.size()<<"}";}
  if(window)DestroyWindow(window);if(unrelated)DestroyWindow(unrelated);LocalFree(args);CoUninitialize();return exit;
}
#pragma comment(lib,"shell32.lib")
