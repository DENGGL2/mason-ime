#pragma once
#include <commctrl.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <utility>
#include <atomic>
#include <string>
#include <vector>
#include <emmintrin.h>
#ifdef min
#undef min
#endif
#ifdef max
#undef max
#endif

// Transient counters only. Input strings and fingerprints are never exported.
struct MasonMotionDiagnosticData {
  DWORD size;
  volatile LONG initialized, importHooks, attached, detached, liveWindows;
  volatile LONG frames, opens, closes, selections, ticks, liveBuffers;
  volatile LONG lastAlpha, lastScale, lastPhase, lastSelection, failures;
  volatile LONG serverModules, clientModules, layerCalls, showCalls, positionCalls, exitWindows;
  volatile LONG letterOpens, letterFrames, lastLetterAlpha, lastLetterScale, activeLetters;
  volatile LONG candidateOpens, candidateFrames, lastCandidateAlpha, lastCandidateScale, activeCandidates;
};
extern "C" { MasonMotionDiagnosticData MasonMotionDiagnostics = {sizeof(MasonMotionDiagnosticData)}; }

namespace MasonCandidateMotion {
// User-requested popup only: alpha .4 -> 1, scale .9 -> 1.1 -> 1.
constexpr double EnterMs = 200, OpacityMs = 200.0*160/300, LookMs = 120, StartScale = .9;
constexpr double StartAlpha = .4, PeakScale = 1.1, PeakMs = 200.0*160/300;
constexpr double CandidateStartAlpha = .8;
constexpr double EnterResponse = .24, AlphaResponse = .18, ExitResponse = .14;
constexpr wchar_t StateProperty[] = L"Mason.CandidateMotion.State.1";
using LayerFunction = BOOL(WINAPI*)(HWND,HDC,const POINT*,const SIZE*,HDC,const POINT*,COLORREF,const BLENDFUNCTION*,DWORD);
using ShowFunction = BOOL(WINAPI*)(HWND,int);
using PositionFunction = BOOL(WINAPI*)(HWND,HWND,int,int,int,int,UINT);
inline LayerFunction originalLayer = nullptr;
inline ShowFunction originalShow = nullptr;
inline PositionFunction originalPosition = nullptr;
inline std::atomic<bool> active{false};
inline SRWLOCK moduleLock=SRWLOCK_INIT;
inline HMODULE ownerModules[8]{};
inline unsigned ownerCount=0;
inline thread_local HWND exitWindow=nullptr;
inline double FrameTime() noexcept {
  static const double milliseconds=[](){LARGE_INTEGER frequency{};QueryPerformanceFrequency(&frequency);return 1000.0/frequency.QuadPart;}();
  LARGE_INTEGER counter{};QueryPerformanceCounter(&counter);return counter.QuadPart*milliseconds;
}
inline ULONGLONG Now() noexcept {return static_cast<ULONGLONG>(FrameTime());}
inline UINT FrameMessage() noexcept {
  static const UINT message=RegisterWindowMessageW(L"Mason.CandidateMotion.Frame.1");return message;
}
inline std::atomic<ULONG_PTR> frameSerial{0};

// The worker only schedules a message. All rendering and HWND state stay on
// the original input/UI thread; one pending message prevents frame backlogs.
struct FrameClock {
  HWND window=nullptr;ULONG_PTR token=0;
  HANDLE timer=nullptr,stop=nullptr,thread=nullptr;
  std::atomic<bool> pending{false};
  std::atomic<bool> failed{false};
  ~FrameClock(){
    if(stop)SetEvent(stop);
    if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
    if(timer)CloseHandle(timer);if(stop)CloseHandle(stop);
  }
};
inline DWORD WINAPI FrameWorker(void* value) noexcept {
  auto& clock=*static_cast<FrameClock*>(value);
  const HANDLE handles[]{clock.stop,clock.timer};
  constexpr double interval=1000.0/60;
  double next=FrameTime()+interval;
  for(;;){
    LARGE_INTEGER due{};due.QuadPart=-std::max<LONGLONG>(1000,static_cast<LONGLONG>((next-FrameTime())*10000));
    if(!SetWaitableTimer(clock.timer,&due,0,nullptr,nullptr,FALSE)){
      clock.failed.store(true,std::memory_order_release);PostMessageW(clock.window,FrameMessage(),clock.token,0);return 1;
    }
    if(WaitForMultipleObjects(2,handles,FALSE,INFINITE)!=WAIT_OBJECT_0+1)return 0;
    if(!clock.pending.exchange(true,std::memory_order_acq_rel) &&
        !PostMessageW(clock.window,FrameMessage(),clock.token,0)){
      clock.pending.store(false,std::memory_order_release);return 1;
    }
    const double now=FrameTime();next+=interval;
    if(next<=now)next=now+interval;
  }
}

inline double Unit(double value) noexcept { return std::max(0.0,std::min(1.0,value)); }
inline double Cubic(double t,double x1,double y1,double x2,double y2) noexcept {
  if(t<=0 || t>=1)return Unit(t);
  auto b=[](double v,double a,double b){const double s=1-v;return 3*s*s*v*a+3*s*v*v*b+v*v*v;};
  double lo=0,hi=1,v=.5;
  for(int i=0;i<24;i++){v=(lo+hi)/2;if(b(v,x1,x2)<t)lo=v;else hi=v;}
  return b(v,y1,y2);
}
inline bool Enabled() noexcept {
  BOOL enabled=TRUE;
  return SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION,0,&enabled,0)?enabled!=FALSE:true;
}

// DirectWrite calls between BeginDraw / EndDraw describe the actual painted
// frame. The signature distinguishes typed text from a selection repaint.
struct TextFrame {
  std::uint64_t hash=14695981039346656037ull;
  bool painting=false,ready=false,latin=false,animationEnabled=true;
};
inline thread_local TextFrame textFrame;
inline void BeginTextFrame() noexcept { if(active){textFrame=TextFrame{};textFrame.painting=true;textFrame.animationEnabled=Enabled();} }
inline void RecordText(const wchar_t* text,UINT32 length) noexcept {
  if(!active || !textFrame.painting || !text || length>1024)return;
  bool latin=false,other=false;
  for(UINT32 i=0;i<length;i++){
    const wchar_t c=text[i];
    if((c>=L'a' && c<=L'z') || (c>=L'A' && c<=L'Z'))latin=true;
    else if(c>L'\x7f' && c!=L'\x200b')other=true;
  }
  if(!latin || other)return;
  textFrame.latin=true;
  for(UINT32 i=0;i<length;i++){
    textFrame.hash^=static_cast<unsigned short>(text[i]);
    textFrame.hash*=1099511628211ull;
  }
}
inline void EndTextFrame() noexcept { if(active){textFrame.painting=false;textFrame.ready=true;} }
struct Signature {std::uint64_t hash=0;bool valid=false;};
inline Signature TakeTextFrame() noexcept {
  const Signature value{textFrame.hash,textFrame.ready && textFrame.latin};
  textFrame.ready=false;return value;
}

struct Surface {
  HDC dc=nullptr;HBITMAP bitmap=nullptr;HGDIOBJ previous=nullptr;
  unsigned char* pixels=nullptr;int width=0,height=0;
  Surface()=default;
  Surface(const Surface&)=delete;Surface& operator=(const Surface&)=delete;
  ~Surface(){Reset();}
  void Reset() noexcept {
    if(dc && previous)SelectObject(dc,previous);
    if(bitmap){DeleteObject(bitmap);InterlockedDecrement(&MasonMotionDiagnostics.liveBuffers);}
    if(dc)DeleteDC(dc);
    dc=nullptr;bitmap=nullptr;previous=nullptr;pixels=nullptr;width=height=0;
  }
  void Swap(Surface& other) noexcept {
    std::swap(dc,other.dc);std::swap(bitmap,other.bitmap);std::swap(previous,other.previous);
    std::swap(pixels,other.pixels);std::swap(width,other.width);std::swap(height,other.height);
  }
  size_t Bytes() const noexcept {return static_cast<size_t>(width)*height*4;}
  bool Ensure(int w,int h) noexcept {
    if(w==width && h==height && pixels)return true;
    if(w<=0 || h<=0 || w>8192 || h>8192 || static_cast<size_t>(w)*h>8388608)return false;
    Surface next;next.dc=CreateCompatibleDC(nullptr);if(!next.dc)return false;
    BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth=w;info.bmiHeader.biHeight=-h;
    info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
    next.bitmap=CreateDIBSection(next.dc,&info,DIB_RGB_COLORS,reinterpret_cast<void**>(&next.pixels),nullptr,0);
    if(next.bitmap)InterlockedIncrement(&MasonMotionDiagnostics.liveBuffers);
    if(!next.bitmap || !next.pixels)return false;
    next.previous=SelectObject(next.dc,next.bitmap);
    if(!next.previous || next.previous==HGDI_ERROR)return false;
    next.width=w;next.height=h;Swap(next);return true;
  }
  bool Copy(HDC source,const POINT& origin,int w,int h) noexcept {
    if(!source || !Ensure(w,h))return false;
    if(!BitBlt(dc,0,0,w,h,source,origin.x,origin.y,SRCCOPY))return false;
    GdiFlush();return true;
  }
  bool Copy(const Surface& source) noexcept {
    if(!source.pixels || !Ensure(source.width,source.height))return false;
    std::memcpy(pixels,source.pixels,source.Bytes());return true;
  }
};
inline thread_local Surface backgroundScratch,paintedScratch;
inline void ResetGlyphScratch() noexcept {backgroundScratch.Reset();paintedScratch.Reset();}
inline bool CaptureRegion(Surface& snapshot,HDC dc,const RECT& rect) noexcept {
  const int width=rect.right-rect.left,height=rect.bottom-rect.top;
  if(width<=0 || height<=0 || !snapshot.Ensure(std::max(width,snapshot.width),std::max(height,snapshot.height)))return false;
  if(!BitBlt(snapshot.dc,0,0,width,height,dc,rect.left,rect.top,SRCCOPY))return false;
  GdiFlush();return true;
}
inline bool SnapshotPixels(HDC dc,const RECT& rect,std::vector<unsigned char>& output) {
  const int width=rect.right-rect.left,height=rect.bottom-rect.top;
  if(!CaptureRegion(backgroundScratch,dc,rect))return false;
  output.resize(static_cast<size_t>(width)*height*4);
  for(int y=0;y<height;y++)std::memcpy(output.data()+static_cast<size_t>(y)*width*4,backgroundScratch.pixels+static_cast<size_t>(y)*backgroundScratch.width*4,static_cast<size_t>(width)*4);
  return true;
}
inline bool LatinPreedit(const wchar_t* text,UINT32 length) noexcept {
  if(!text || length>1024)return false;
  bool letter=false;
  for(UINT32 i=0;i<length;i++){
    const wchar_t c=text[i];
    if((c>=L'a' && c<=L'z') || (c>=L'A' && c<=L'Z'))letter=true;
    else if(c!=L' ' && c!=L'\'' && c!=0x200b && c!=0x2063)return false;
  }
  return letter;
}
inline bool LatinPreedit(const std::wstring& text) noexcept {return LatinPreedit(text.data(),static_cast<UINT32>(text.size()));}
struct GlyphScale {
  int width=0,height=0;std::vector<float> delta;
};
struct LetterPixels {
  wchar_t letter=0;RECT rect{};
  std::vector<short> delta;
  ULONGLONG began=0;
  GlyphScale resized;
};
struct CandidatePixels {
  std::wstring text;std::vector<LetterPixels> glyphs;ULONGLONG began=0;
};
inline thread_local std::vector<CandidatePixels> candidateFrame;
struct GlyphFrame {
  std::wstring text;std::vector<LetterPixels> letters;
  HDC dc=nullptr;LONG row=0,lastRight=0;
};
inline thread_local GlyphFrame glyphFrame;
struct GlyphPending {
  HDC dc=nullptr;RECT rect{};std::wstring text;
  ID2D1RenderTarget* target=nullptr;
  bool candidate=false;
  std::vector<LetterPixels> letters;std::vector<unsigned char> before;
};
inline thread_local std::vector<GlyphPending> glyphPending;
inline bool VisibleText(const wchar_t* text,UINT32 length) noexcept {
  if(!text || !length || length>1024)return false;
  for(UINT32 i=0;i<length;i++)if(text[i]>L' ' && text[i]!=0x200b && text[i]!=0x2063)return true;
  return false;
}
inline GlyphPending BeforeCandidateGlyphs(HDC dc,const RECT& binding,ID2D1RenderTarget* target,D2D1_POINT_2F origin,IDWriteTextLayout* layout,const std::wstring& text) {
  GlyphPending pending;if(!VisibleText(text.data(),static_cast<UINT32>(text.size())))return pending;
  bool label=text.size()<=3,emoji=false;
  for(wchar_t c:text){if((c<L'0' || c>L'9') && c!=L'.' && c!=L' ')label=false;if((c>=0xd800 && c<=0xdfff) || (c>=0x2600 && c<=0x27bf) || c==0x200d || c==0xfe0f || c==0x20e3)emoji=true;}
  if(label)return pending;
  BITMAP bitmap{};if(!dc || !GetObjectW(GetCurrentObject(dc,OBJ_BITMAP),sizeof(bitmap),&bitmap) || bitmap.bmBitsPixel!=32)return pending;
  D2D1_MATRIX_3X2_F transform{};target->GetTransform(&transform);FLOAT dpiX=96,dpiY=96;target->GetDpi(&dpiX,&dpiY);
  if(std::abs(transform._12)>.0001 || std::abs(transform._21)>.0001 || transform._11<=0 || transform._22<=0)return pending;
  RECT total{bitmap.bmWidth,std::abs(bitmap.bmHeight),0,0};
  auto add=[&](FLOAT left,FLOAT top,FLOAT width,FLOAT height){
    if(width<=0 || height<=0)return;
    LetterPixels cell;
    cell.rect.left=binding.left+static_cast<LONG>(std::ceil(((origin.x+left)*transform._11+transform._31)*dpiX/96));
    cell.rect.right=binding.left+static_cast<LONG>(std::ceil(((origin.x+left+width)*transform._11+transform._31)*dpiX/96));
    cell.rect.top=binding.top+static_cast<LONG>(std::floor(((origin.y+top)*transform._22+transform._32)*dpiY/96))-2;
    cell.rect.bottom=binding.top+static_cast<LONG>(std::ceil(((origin.y+top+height)*transform._22+transform._32)*dpiY/96))+2;
    cell.rect.left=std::max<LONG>(0,cell.rect.left);cell.rect.right=std::min<LONG>(bitmap.bmWidth,cell.rect.right);
    cell.rect.top=std::max<LONG>(0,cell.rect.top);cell.rect.bottom=std::min<LONG>(std::abs(bitmap.bmHeight),cell.rect.bottom);
    if(cell.rect.right<=cell.rect.left || cell.rect.bottom<=cell.rect.top)return;
    total.left=std::min(total.left,cell.rect.left);total.top=std::min(total.top,cell.rect.top);total.right=std::max(total.right,cell.rect.right);total.bottom=std::max(total.bottom,cell.rect.bottom);
    pending.letters.push_back(std::move(cell));
  };
  if(emoji){DWRITE_TEXT_METRICS metrics{};if(FAILED(layout->GetMetrics(&metrics)))return pending;add(metrics.left,metrics.top,metrics.widthIncludingTrailingWhitespace,metrics.height);}
  else for(UINT32 i=0;i<text.size();){
    FLOAT x=0,y=0;DWRITE_HIT_TEST_METRICS hit{};if(FAILED(layout->HitTestTextPosition(i,FALSE,&x,&y,&hit)))return GlyphPending{};
    const UINT32 next=std::max(i+1,hit.textPosition+hit.length);
    if(text[i]>L' ' && text[i]!=0x200b && text[i]!=0x2063)add(hit.left,hit.top,hit.width,hit.height);
    i=next;
  }
  if(pending.letters.empty())return pending;
  if(!SnapshotPixels(dc,total,pending.before))return GlyphPending{};
  pending.dc=dc;pending.rect=total;pending.target=target;pending.text=text;pending.candidate=true;
  return pending;
}
inline GlyphPending BeforeGlyphs(HDC dc,const RECT& binding,ID2D1RenderTarget* target,D2D1_POINT_2F origin,IDWriteTextLayout* layout,const std::wstring& text) {
  GlyphPending pending;
  if(!active || !textFrame.painting || !textFrame.animationEnabled || !dc || !layout || text.size()>1024)return pending;
  if(!LatinPreedit(text))return BeforeCandidateGlyphs(dc,binding,target,origin,layout,text);
  if(text.size()>128)return pending;
  BITMAP bitmap{};if(!GetObjectW(GetCurrentObject(dc,OBJ_BITMAP),sizeof(bitmap),&bitmap) || bitmap.bmBitsPixel!=32)return pending;
  D2D1_MATRIX_3X2_F transform{};target->GetTransform(&transform);FLOAT dpiX=96,dpiY=96;target->GetDpi(&dpiX,&dpiY);
  if(std::abs(transform._12)>.0001 || std::abs(transform._21)>.0001 || transform._11<=0 || transform._22<=0)return pending;
  DWRITE_TEXT_METRICS rowMetrics{};
  if(glyphFrame.dc && SUCCEEDED(layout->GetMetrics(&rowMetrics))){
    const LONG row=binding.top+static_cast<LONG>(std::floor(((origin.y+rowMetrics.top)*transform._22+transform._32)*dpiY/96))-2;
    if(glyphFrame.dc!=dc || std::abs(glyphFrame.row-row)>2)return BeforeCandidateGlyphs(dc,binding,target,origin,layout,text);
  }
  RECT total{bitmap.bmWidth,std::abs(bitmap.bmHeight),0,0};
  for(UINT32 i=0;i<text.size();i++){
    const wchar_t c=text[i];if(!((c>=L'a' && c<=L'z') || (c>=L'A' && c<=L'Z')))continue;
    FLOAT x=0,y=0;DWRITE_HIT_TEST_METRICS hit{};
    if(FAILED(layout->HitTestTextPosition(i,FALSE,&x,&y,&hit)) || hit.width<=0)return GlyphPending{};
    UINT32 clusterLetters=0;
    for(UINT32 at=hit.textPosition;at<hit.textPosition+hit.length && at<text.size();at++)if((text[at]>=L'a' && text[at]<=L'z') || (text[at]>=L'A' && text[at]<=L'Z'))clusterLetters++;
    if(clusterLetters!=1)return GlyphPending{};
    LetterPixels cell;cell.letter=c;
    cell.rect.left=binding.left+static_cast<LONG>(std::ceil(((origin.x+hit.left)*transform._11+transform._31)*dpiX/96));
    cell.rect.right=binding.left+static_cast<LONG>(std::ceil(((origin.x+hit.left+hit.width)*transform._11+transform._31)*dpiX/96));
    cell.rect.top=binding.top+static_cast<LONG>(std::floor(((origin.y+hit.top)*transform._22+transform._32)*dpiY/96))-2;
    cell.rect.bottom=binding.top+static_cast<LONG>(std::ceil(((origin.y+hit.top+hit.height)*transform._22+transform._32)*dpiY/96))+2;
    cell.rect.left=std::max<LONG>(0,cell.rect.left);cell.rect.right=std::min<LONG>(bitmap.bmWidth,cell.rect.right);
    cell.rect.top=std::max<LONG>(0,cell.rect.top);cell.rect.bottom=std::min<LONG>(std::abs(bitmap.bmHeight),cell.rect.bottom);
    if(cell.rect.right<=cell.rect.left || cell.rect.bottom<=cell.rect.top)return GlyphPending{};
    total.left=std::min(total.left,cell.rect.left);total.top=std::min(total.top,cell.rect.top);
    total.right=std::max(total.right,cell.rect.right);total.bottom=std::max(total.bottom,cell.rect.bottom);
    pending.text.push_back(c);pending.letters.push_back(std::move(cell));
  }
  if(pending.letters.empty())return pending;
  if(glyphFrame.dc){
    if(glyphFrame.dc!=dc || std::abs(glyphFrame.row-total.top)>2)return BeforeCandidateGlyphs(dc,binding,target,origin,layout,text);
    if(total.left<glyphFrame.lastRight){glyphFrame=GlyphFrame{};glyphPending.clear();}
  }
  if(!SnapshotPixels(dc,total,pending.before))return GlyphPending{};
  pending.rect=total;pending.dc=dc;pending.target=target;
  glyphFrame.dc=dc;glyphFrame.row=total.top;glyphFrame.lastRight=total.right;
  return pending;
}
inline void FinishGlyphs(GlyphPending& pending,const Surface& after,const RECT& captured) {
  if(!pending.dc)return;
  const int width=pending.rect.right-pending.rect.left;
  CandidatePixels candidate;candidate.text=pending.text;
  for(auto& cell:pending.letters){
    const int w=cell.rect.right-cell.rect.left,h=cell.rect.bottom-cell.rect.top;cell.delta.resize(static_cast<size_t>(w)*h*4);
    const __m128i zero=_mm_setzero_si128(),channels=_mm_set_epi16(0,-1,-1,-1,0,-1,-1,-1);
    for(int y=0;y<h;y++){
      const auto* native=after.pixels+(static_cast<size_t>(cell.rect.top+y-captured.top)*after.width+cell.rect.left-captured.left)*4;
      const auto* before=pending.before.data()+(static_cast<size_t>(y+cell.rect.top-pending.rect.top)*width+cell.rect.left-pending.rect.left)*4;
      auto* delta=cell.delta.data()+static_cast<size_t>(y)*w*4;int x=0;
      for(;x+1<w;x+=2){
        const __m128i a=_mm_unpacklo_epi8(_mm_loadl_epi64(reinterpret_cast<const __m128i*>(native+x*4)),zero);
        const __m128i b=_mm_unpacklo_epi8(_mm_loadl_epi64(reinterpret_cast<const __m128i*>(before+x*4)),zero);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(delta+x*4),_mm_and_si128(_mm_sub_epi16(a,b),channels));
      }
      if(x<w){for(int c=0;c<3;c++)delta[x*4+c]=static_cast<short>(native[x*4+c]-before[x*4+c]);delta[x*4+3]=0;}
    }
    if(std::none_of(cell.delta.begin(),cell.delta.end(),[](short value){return value!=0;})){if(pending.candidate)continue;glyphFrame.text.clear();glyphFrame.letters.clear();return;}
    if(pending.candidate)candidate.glyphs.push_back(std::move(cell));else glyphFrame.letters.push_back(std::move(cell));
  }
  if(pending.candidate){if(!candidate.glyphs.empty())candidateFrame.push_back(std::move(candidate));}else glyphFrame.text+=pending.text;
}
inline void AfterGlyphs(ID2D1RenderTarget*,GlyphPending& pending) {if(pending.dc)glyphPending.push_back(std::move(pending));}
inline void CompleteGlyphs(ID2D1RenderTarget* target) noexcept {
  try{
    for(;;){
      auto first=std::find_if(glyphPending.begin(),glyphPending.end(),[&](const GlyphPending& item){return item.target==target;});if(first==glyphPending.end())break;
      const HDC dc=first->dc;RECT area=first->rect;
      for(const auto& item:glyphPending)if(item.target==target && item.dc==dc){area.left=std::min(area.left,item.rect.left);area.top=std::min(area.top,item.rect.top);area.right=std::max(area.right,item.rect.right);area.bottom=std::max(area.bottom,item.rect.bottom);}
      const bool copied=CaptureRegion(paintedScratch,dc,area);
      for(auto at=glyphPending.begin();at!=glyphPending.end();){if(at->target==target && at->dc==dc){if(copied)FinishGlyphs(*at,paintedScratch,area);at=glyphPending.erase(at);}else ++at;}
    }
  }
  catch(...){glyphPending.clear();glyphFrame=GlyphFrame{};candidateFrame.clear();}
}
inline GlyphFrame TakeGlyphs() noexcept {GlyphFrame result=std::move(glyphFrame);glyphFrame=GlyphFrame{};glyphPending.clear();return result;}
inline void PopValues(double elapsed,double& alpha,double& scale) noexcept {
  auto out=[](double t){const double inverse=1-Unit(t);return 1-inverse*inverse*inverse;};
  alpha=StartAlpha+(1-StartAlpha)*out(elapsed/OpacityMs);
  scale=elapsed<=PeakMs?StartScale+(PeakScale-StartScale)*out(elapsed/PeakMs):PeakScale+(1-PeakScale)*out((elapsed-PeakMs)/(EnterMs-PeakMs));
}
inline void CandidateValues(double elapsed,double& alpha,double& scale) noexcept {
  const double opacity=1-Unit(elapsed/OpacityMs);
  alpha=CandidateStartAlpha+(1-CandidateStartAlpha)*(1-opacity*opacity*opacity);
  scale=1;
}
inline void ReuseGlyphScale(LetterPixels& current,LetterPixels& previous) noexcept {
  if(current.rect.right-current.rect.left==previous.rect.right-previous.rect.left && current.rect.bottom-current.rect.top==previous.rect.bottom-previous.rect.top && current.delta.size()==previous.delta.size() && !previous.resized.delta.empty() && std::memcmp(current.delta.data(),previous.delta.data(),current.delta.size()*sizeof(short))==0)current.resized=std::move(previous.resized);
}
struct ScaleSample {int lower=0,upper=0;unsigned weight=0;};
enum class Phase : LONG {Hidden=0,Entering=1,Steady=2,Leaving=3};
struct State {
  HWND window=nullptr;
  HWND entryWindow=nullptr;
  bool forwardingMouse=false;
  ~State(){delete clock;if(entryWindow)DestroyWindow(entryWindow);}
  Surface frame,incoming,blendFrom,composition,presentation;
  Signature signature;
  std::wstring preedit;std::vector<LetterPixels> letters;
  std::vector<CandidatePixels> candidates;
  std::vector<ScaleSample> sampleX,sampleY;int sampleWidth=0,sampleHeight=0;
  BLENDFUNCTION blend{AC_SRC_OVER,0,255,AC_SRC_ALPHA};
  COLORREF colorKey=0;DWORD flags=ULW_ALPHA;
  Phase phase=Phase::Hidden;
  bool requested=false,mixing=false,clickThrough=false,restoreTransparent=false,awaitingFrame=false,pendingOpen=false,ghost=false;
  UINT_PTR timer=0;
  FrameClock* clock=nullptr;
  bool engineInvisible=false;
  bool strippedCandidates=false;
  ULONGLONG start=0,mixStart=0,last=0;
  double alpha=0,scale=StartScale,fromAlpha=0,fromScale=StartScale;
  double alphaDuration=OpacityMs,scaleDuration=EnterMs;
  double paintedScale=1;
  double alphaVelocity=0,scaleVelocity=0,anchorX=0,anchorY=0;
  double paintedScaleX=1,paintedScaleY=1;
  int paintedLeft=0,paintedTop=0;
};
inline State* Find(HWND window) noexcept {return reinterpret_cast<State*>(GetPropW(window,StateProperty));}
inline bool LettersActive(const State& state,ULONGLONG now) noexcept {
  for(const auto& cell:state.letters)if(cell.began && now-cell.began<EnterMs)return true;
  return false;
}
inline bool CandidatesActive(const State& state,ULONGLONG now) noexcept {for(const auto& item:state.candidates)if(item.began && now-item.began<OpacityMs)return true;return false;}
inline void ClearCandidates(State& state) noexcept {state.candidates.clear();candidateFrame.clear();InterlockedExchange(&MasonMotionDiagnostics.activeCandidates,0);}
inline void UpdateCandidates(State& state,const POINT& origin,ULONGLONG now) noexcept {
  try{
    auto captured=std::move(candidateFrame);candidateFrame.clear();std::vector<bool> used(state.candidates.size(),false);
    for(auto& item:captured){
      size_t found=state.candidates.size();
      for(size_t i=0;i<state.candidates.size();i++)if(!used[i] && state.candidates[i].text==item.text){found=i;break;}
      if(found<state.candidates.size()){
        auto& previous=state.candidates[found];item.began=previous.began;used[found]=true;
        if(item.glyphs.size()==previous.glyphs.size())for(size_t i=0;i<item.glyphs.size();i++)ReuseGlyphScale(item.glyphs[i],previous.glyphs[i]);
      }
      else {item.began=now;InterlockedIncrement(&MasonMotionDiagnostics.candidateOpens);}
      for(auto& glyph:item.glyphs){OffsetRect(&glyph.rect,-origin.x,-origin.y);glyph.began=item.began;}
    }
    state.candidates=std::move(captured);
  }catch(...){ClearCandidates(state);}
}
inline void ClearLetters(State& state) noexcept {
  state.letters.clear();state.preedit.clear();glyphFrame=GlyphFrame{};glyphPending.clear();
  ResetGlyphScratch();
  InterlockedExchange(&MasonMotionDiagnostics.activeLetters,0);
}
inline void UpdateLetters(State& state,GlyphFrame&& captured,const POINT& origin,ULONGLONG now) noexcept {
  try{
    if(captured.letters.size()!=captured.text.size() || captured.letters.empty()){ClearLetters(state);return;}
    size_t prefix=0,suffix=0;
    while(prefix<state.preedit.size() && prefix<captured.text.size() && state.preedit[prefix]==captured.text[prefix])prefix++;
    while(suffix<state.preedit.size()-prefix && suffix<captured.text.size()-prefix && state.preedit[state.preedit.size()-suffix-1]==captured.text[captured.text.size()-suffix-1])suffix++;
    for(size_t i=0;i<captured.letters.size();i++){
      auto& cell=captured.letters[i];OffsetRect(&cell.rect,-origin.x,-origin.y);
      if(i<prefix && i<state.letters.size()){cell.began=state.letters[i].began;ReuseGlyphScale(cell,state.letters[i]);}
      else if(i>=captured.text.size()-suffix){const size_t previous=state.preedit.size()-(captured.text.size()-i);cell.began=previous<state.letters.size()?state.letters[previous].began:0;if(previous<state.letters.size())ReuseGlyphScale(cell,state.letters[previous]);}
      else {cell.began=now;InterlockedIncrement(&MasonMotionDiagnostics.letterOpens);}
    }
    state.preedit=std::move(captured.text);state.letters=std::move(captured.letters);
  }catch(...){ClearLetters(state);}
}
inline __m128 PixelValue(const unsigned char* pixel) noexcept {
  unsigned value=0;std::memcpy(&value,pixel,sizeof(value));const __m128i zero=_mm_setzero_si128();
  return _mm_cvtepi32_ps(_mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(static_cast<int>(value)),zero),zero));
}
inline __m128 DeltaValue(const short* delta) noexcept {
  const __m128i packed=_mm_loadl_epi64(reinterpret_cast<const __m128i*>(delta));
  return _mm_cvtepi32_ps(_mm_unpacklo_epi16(packed,_mm_cmpgt_epi16(_mm_setzero_si128(),packed)));
}
inline void StorePixel(unsigned char* pixel,__m128 value) noexcept {
  const __m128i rounded=_mm_cvttps_epi32(_mm_add_ps(value,_mm_set1_ps(.5f))),zero=_mm_setzero_si128();
  const unsigned packed=static_cast<unsigned>(_mm_cvtsi128_si32(_mm_packus_epi16(_mm_packs_epi32(rounded,zero),zero)));
  std::memcpy(pixel,&packed,sizeof(packed));
}
inline __m128 SampleDelta(const LetterPixels& cell,int x,int y,int width,int height) noexcept {
  return x<0 || y<0 || x>=width || y>=height?_mm_setzero_ps():DeltaValue(cell.delta.data()+(static_cast<size_t>(y)*width+x)*4);
}
inline const float* ResizedGlyph(LetterPixels& cell,int width,int height,int nativeWidth,int nativeHeight) noexcept {
  if(cell.resized.width==width && cell.resized.height==height && !cell.resized.delta.empty())return cell.resized.delta.data();
  try{
    struct Sample {int lower=0;float weight=0;};
    std::vector<Sample> xs(width),ys(height);std::vector<float> pixels(static_cast<size_t>(width)*height*4);
    for(int x=0;x<width;x++){const double value=(x+.5)*nativeWidth/width-.5;xs[x].lower=static_cast<int>(std::floor(value));xs[x].weight=static_cast<float>(value-xs[x].lower);}
    for(int y=0;y<height;y++){const double value=(y+.5)*nativeHeight/height-.5;ys[y].lower=static_cast<int>(std::floor(value));ys[y].weight=static_cast<float>(value-ys[y].lower);}
    const __m128 one=_mm_set1_ps(1);
    for(int y=0;y<height;y++){
      const auto& sy=ys[y];const __m128 wy=_mm_set1_ps(sy.weight),inverseY=_mm_sub_ps(one,wy);
      for(int x=0;x<width;x++){
        const auto& sx=xs[x];const __m128 wx=_mm_set1_ps(sx.weight),inverseX=_mm_sub_ps(one,wx);
        const __m128 top=_mm_add_ps(_mm_mul_ps(SampleDelta(cell,sx.lower,sy.lower,nativeWidth,nativeHeight),inverseX),_mm_mul_ps(SampleDelta(cell,sx.lower+1,sy.lower,nativeWidth,nativeHeight),wx));
        const __m128 bottom=_mm_add_ps(_mm_mul_ps(SampleDelta(cell,sx.lower,sy.lower+1,nativeWidth,nativeHeight),inverseX),_mm_mul_ps(SampleDelta(cell,sx.lower+1,sy.lower+1,nativeWidth,nativeHeight),wx));
        _mm_storeu_ps(pixels.data()+(static_cast<size_t>(y)*width+x)*4,_mm_add_ps(_mm_mul_ps(top,inverseY),_mm_mul_ps(bottom,wy)));
      }
    }
    cell.resized.delta=std::move(pixels);cell.resized.width=width;cell.resized.height=height;return cell.resized.delta.data();
  }catch(...){return nullptr;}
}
inline void PaintGlyph(State& state,LetterPixels& cell,double alpha,double scale) noexcept {
  const int w=cell.rect.right-cell.rect.left,h=cell.rect.bottom-cell.rect.top;
  if(w<=0 || h<=0 || cell.delta.size()!=static_cast<size_t>(w)*h*4)return;
  const int sw=std::max(1,static_cast<int>(std::lround(w*scale))),sh=std::max(1,static_cast<int>(std::lround(h*scale)));
  const int xStart=std::max<int>(0,-cell.rect.left),xEnd=std::min<int>(w,state.frame.width-cell.rect.left),yStart=std::max<int>(0,-cell.rect.top),yEnd=std::min<int>(h,state.frame.height-cell.rect.top);
  if(xStart>=xEnd || yStart>=yEnd)return;
  if(sw==w && sh==h){
    if(alpha>=.999999)return;
    const __m128 amount=_mm_set1_ps(static_cast<float>(alpha-1));
    for(int y=yStart;y<yEnd;y++){
      auto* row=state.composition.pixels+(static_cast<size_t>(cell.rect.top+y)*state.frame.width+cell.rect.left+xStart)*4;
      const auto* delta=cell.delta.data()+(static_cast<size_t>(y)*w+xStart)*4;
      for(int x=xStart;x<xEnd;x++,row+=4,delta+=4)StorePixel(row,_mm_add_ps(PixelValue(row),_mm_mul_ps(DeltaValue(delta),amount)));
    }
    return;
  }
  const float* resized=ResizedGlyph(cell,sw,sh,w,h);if(!resized)return;
  const __m128i zero=_mm_setzero_si128();
  for(int y=yStart;y<yEnd;y++){
    auto* row=state.composition.pixels+(static_cast<size_t>(cell.rect.top+y)*state.frame.width+cell.rect.left+xStart)*4;
    const auto* delta=cell.delta.data()+(static_cast<size_t>(y)*w+xStart)*4;int x=xStart;
    for(;x+1<xEnd;x+=2,row+=8,delta+=8){
      const __m128i before=_mm_unpacklo_epi8(_mm_loadl_epi64(reinterpret_cast<const __m128i*>(row)),zero);
      _mm_storel_epi64(reinterpret_cast<__m128i*>(row),_mm_packus_epi16(_mm_sub_epi16(before,_mm_loadu_si128(reinterpret_cast<const __m128i*>(delta))),zero));
    }
    if(x<xEnd)for(int c=0;c<3;c++)row[c]=static_cast<unsigned char>(std::max(0,std::min(255,static_cast<int>(row[c])-delta[c])));
  }
  const int left=cell.rect.left+(w-sw)/2,top=cell.rect.top+(h-sh)/2;
  const int sxStart=std::max(0,-left),sxEnd=std::min(sw,state.frame.width-left),syStart=std::max(0,-top),syEnd=std::min(sh,state.frame.height-top);
  const __m128 amount=_mm_set1_ps(static_cast<float>(alpha));
  for(int y=syStart;y<syEnd;y++){
    auto* row=state.composition.pixels+(static_cast<size_t>(top+y)*state.frame.width+left+sxStart)*4;
    const auto* delta=resized+(static_cast<size_t>(y)*sw+sxStart)*4;
    for(int x=sxStart;x<sxEnd;x++,row+=4,delta+=4)StorePixel(row,_mm_add_ps(PixelValue(row),_mm_mul_ps(_mm_loadu_ps(delta),amount)));
  }
}
inline const Surface& LetterFrame(State& state,ULONGLONG now) noexcept {
  state.strippedCandidates=false;
  const bool popupScaling=state.phase==Phase::Entering && !state.candidates.empty();
  if(!Enabled() || (!LettersActive(state,now) && !CandidatesActive(state,now) && !popupScaling)){InterlockedExchange(&MasonMotionDiagnostics.activeLetters,0);InterlockedExchange(&MasonMotionDiagnostics.activeCandidates,0);state.composition.Reset();return state.frame;}
  if(!state.composition.Copy(state.frame))return state.frame;
  LONG activeCount=0,candidateCount=0;
  for(auto& cell:state.letters){
    const double elapsed=static_cast<double>(now-cell.began);if(!cell.began || elapsed>=EnterMs)continue;
    activeCount++;double alpha=1,scale=1;PopValues(elapsed,alpha,scale);
    const double localAlpha=state.phase==Phase::Entering && state.alpha>0?Unit(alpha/state.alpha):alpha;
    PaintGlyph(state,cell,localAlpha,scale);
    InterlockedExchange(&MasonMotionDiagnostics.lastLetterAlpha,static_cast<LONG>(std::lround(alpha*255)));
    InterlockedExchange(&MasonMotionDiagnostics.lastLetterScale,static_cast<LONG>(std::lround(scale*1000)));
  }
  for(auto& item:state.candidates){
    const double elapsed=static_cast<double>(now-item.began);const bool animating=item.began && elapsed<OpacityMs;
    if(!animating && !popupScaling)continue;
    double alpha=1,scale=1;if(animating){candidateCount++;CandidateValues(elapsed,alpha,scale);}
    const double localAlpha=state.phase==Phase::Entering && state.alpha>0?Unit(alpha/state.alpha):alpha;
    // While the panel scales, remove its candidate ink here and put the
    // original glyph pixels back after the panel transform at their native size.
    for(auto& cell:item.glyphs)PaintGlyph(state,cell,popupScaling?0:localAlpha,1);
    if(animating){InterlockedExchange(&MasonMotionDiagnostics.lastCandidateAlpha,static_cast<LONG>(std::lround(alpha*255)));InterlockedExchange(&MasonMotionDiagnostics.lastCandidateScale,static_cast<LONG>(std::lround(scale*1000)));}
  }
  InterlockedExchange(&MasonMotionDiagnostics.activeLetters,activeCount);if(activeCount)InterlockedIncrement(&MasonMotionDiagnostics.letterFrames);
  InterlockedExchange(&MasonMotionDiagnostics.activeCandidates,candidateCount);if(candidateCount)InterlockedIncrement(&MasonMotionDiagnostics.candidateFrames);
  state.strippedCandidates=popupScaling;
  return state.composition;
}
inline const Surface& NativeCandidateFrame(State& state,const Surface& source,ULONGLONG now) noexcept {
  if(!state.strippedCandidates || !source.pixels)return source;
  Surface* output=&source==&state.presentation?&state.presentation:(&source==&state.composition?&state.composition:nullptr);
  if(!output)return source;
  for(const auto& item:state.candidates){
    double alpha=1,scale=1;if(item.began)CandidateValues(static_cast<double>(now-item.began),alpha,scale);
    if(state.alpha>0)alpha=Unit(alpha/state.alpha);
    const __m128 amount=_mm_set1_ps(static_cast<float>(alpha));
    for(const auto& cell:item.glyphs){
      const int width=cell.rect.right-cell.rect.left,height=cell.rect.bottom-cell.rect.top;
      if(width<=0 || height<=0 || cell.delta.size()!=static_cast<size_t>(width)*height*4)continue;
      const int left=static_cast<int>(std::lround((cell.rect.left+width*.5)*state.paintedScaleX-width*.5));
      const int top=static_cast<int>(std::lround((cell.rect.top+height*.5)*state.paintedScaleY-height*.5));
      const int xStart=std::max(0,-left),xEnd=std::min(width,output->width-left),yStart=std::max(0,-top),yEnd=std::min(height,output->height-top);
      if(xStart>=xEnd || yStart>=yEnd)continue;
      for(int y=yStart;y<yEnd;y++){
        auto* pixel=output->pixels+(static_cast<size_t>(top+y)*output->width+left+xStart)*4;
        const auto* delta=cell.delta.data()+(static_cast<size_t>(y)*width+xStart)*4;
        for(int x=xStart;x<xEnd;x++,pixel+=4,delta+=4)StorePixel(pixel,_mm_add_ps(PixelValue(pixel),_mm_mul_ps(DeltaValue(delta),amount)));
      }
    }
  }
  return source;
}
inline bool OwnedWindow(HWND window) noexcept {
  const HMODULE instance=reinterpret_cast<HMODULE>(GetWindowLongPtrW(window,GWLP_HINSTANCE));
  bool found=false;AcquireSRWLockShared(&moduleLock);
  for(unsigned i=0;i<ownerCount;i++)if(ownerModules[i]==instance){found=true;break;}
  ReleaseSRWLockShared(&moduleLock);return found;
}
inline bool Candidate(HWND window,bool layered=true) noexcept {
  if(!active || !window)return false;
  DWORD process=0;const DWORD thread=GetWindowThreadProcessId(window,&process);
  if(process!=GetCurrentProcessId() || thread!=GetCurrentThreadId() || !OwnedWindow(window))return false;
  const LONG_PTR required=WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE;
  const LONG_PTR ex=GetWindowLongPtrW(window,GWL_EXSTYLE);
  if(!(GetWindowLongPtrW(window,GWL_STYLE)&WS_POPUP) || (ex&required)!=required || (layered && !(ex&WS_EX_LAYERED)))return false;
  // Only ATL popups owned by the verified Mason client/server module qualify.
  // The host application's import table, windows and other IMEs are excluded.
  wchar_t name[96]{};
  return GetClassNameW(window,name,96)>4 && wcsncmp(name,L"ATL:",4)==0 && GetWindowTextLengthW(window)==0;
}
inline void ClickThrough(State& state,bool value) noexcept {
  LONG_PTR style=GetWindowLongPtrW(state.window,GWL_EXSTYLE);
  if(value){
    if(!state.clickThrough){state.restoreTransparent=(style&WS_EX_TRANSPARENT)!=0;state.clickThrough=true;}
    if(!(style&WS_EX_TRANSPARENT))SetWindowLongPtrW(state.window,GWL_EXSTYLE,style|WS_EX_TRANSPARENT);
  }else if(state.clickThrough){
    const LONG_PTR restored=state.restoreTransparent?style|WS_EX_TRANSPARENT:style&~WS_EX_TRANSPARENT;
    if(restored!=style)SetWindowLongPtrW(state.window,GWL_EXSTYLE,restored);
    state.clickThrough=false;
  }
}
inline void StopTimer(State& state) noexcept {
  if(state.clock){auto* clock=state.clock;state.clock=nullptr;state.timer=0;delete clock;}
  else if(state.timer){KillTimer(state.window,state.timer);state.timer=0;}
}
inline void Spring(double& value,double& velocity,double target,double response,double seconds) noexcept {
  if(seconds<=0)return;
  const double omega=6.283185307179586/response,offset=value-target,term=velocity+omega*offset;
  const double decay=std::exp(-omega*seconds);
  value=target+(offset+term*seconds)*decay;
  velocity=(velocity-omega*term*seconds)*decay;
}
inline void Advance(State& state,ULONGLONG now) noexcept {
  if(state.pendingOpen)return;
  now=std::max(now,state.last);
  const double elapsed=now>=state.start?static_cast<double>(now-state.start):0;
  const double seconds=now>=state.last?static_cast<double>(now-state.last)/1000:0;state.last=now;
  if(state.phase==Phase::Entering){
    PopValues(elapsed,state.alpha,state.scale);
    if(elapsed>=EnterMs){state.phase=Phase::Steady;state.alpha=state.scale=1;state.alphaVelocity=state.scaleVelocity=0;}
  }else if(state.phase==Phase::Leaving){
    Spring(state.alpha,state.alphaVelocity,0,ExitResponse,seconds);
    Spring(state.scale,state.scaleVelocity,StartScale,ExitResponse,seconds);
    if(elapsed>=OpacityMs){state.phase=Phase::Hidden;state.alpha=0;state.alphaVelocity=state.scaleVelocity=0;}
  }
}
inline const Surface& Compose(State& state,ULONGLONG now) noexcept {
  if(!state.mixing)return state.frame;
  const double progress=Unit(static_cast<double>(now-state.mixStart)/LookMs);
  InterlockedExchange(&MasonMotionDiagnostics.lastSelection,static_cast<LONG>(std::lround(progress*1000)));
  if(progress==1){state.mixing=false;state.blendFrom.Reset();state.composition.Reset();return state.frame;}
  if(!state.composition.Ensure(state.frame.width,state.frame.height)){state.mixing=false;return state.frame;}
  const unsigned amount=static_cast<unsigned>(std::lround(Cubic(progress,.215,.61,.355,1)*256));
  for(size_t i=0;i<state.frame.Bytes();i++)
    state.composition.pixels[i]=static_cast<unsigned char>((state.blendFrom.pixels[i]*(256-amount)+state.frame.pixels[i]*amount+128)>>8);
  return state.composition;
}
inline const Surface& Scaled(State& state,const Surface& source) noexcept {
  state.paintedLeft=state.paintedTop=0;state.paintedScaleX=state.paintedScaleY=1;
  if(std::abs(state.scale-1)<.0001){state.presentation.Reset();return source;}
  const int width=std::max(1,static_cast<int>(std::lround(source.width*state.scale)));
  const int height=std::max(1,static_cast<int>(std::lround(source.height*state.scale)));
  if(!state.presentation.Ensure(width,height))return source;
  auto& output=state.presentation;
  const int left=static_cast<int>(std::lround((source.width-width)*state.anchorX));
  const int top=static_cast<int>(std::lround((source.height-height)*state.anchorY));
  try{
    auto map=[](std::vector<ScaleSample>& samples,int target,int native){
      samples.resize(target);
      for(int at=0;at<target;at++){
        const double value=std::max(0.0,(at+.5)*native/target-.5);auto& sample=samples[at];
        sample.lower=std::min(native-1,static_cast<int>(value));sample.upper=std::min(native-1,sample.lower+1);sample.weight=static_cast<unsigned>((value-sample.lower)*256);
      }
    };
    if(state.sampleX.size()!=static_cast<size_t>(width) || state.sampleWidth!=source.width){map(state.sampleX,width,source.width);state.sampleWidth=source.width;}
    if(state.sampleY.size()!=static_cast<size_t>(height) || state.sampleHeight!=source.height){map(state.sampleY,height,source.height);state.sampleHeight=source.height;}
  }catch(...){return source;}
  state.paintedLeft=left;state.paintedTop=top;
  state.paintedScaleX=static_cast<double>(width)/source.width;state.paintedScaleY=static_cast<double>(height)/source.height;
  // Reuse coordinate maps and sample all four premultiplied BGRA channels
  // together. Integer weights keep the original bilinear edge pixels exact.
  const __m128 rounding=_mm_set1_ps(32768),normalization=_mm_set1_ps(1.f/65536);const __m128i zero=_mm_setzero_si128();
  for(int y=0;y<height;y++){
    const auto& sy=state.sampleY[y];const auto* topRow=source.pixels+static_cast<size_t>(sy.lower)*source.width*4;const auto* bottomRow=source.pixels+static_cast<size_t>(sy.upper)*source.width*4;
    auto* pixel=output.pixels+static_cast<size_t>(y)*width*4;
    for(int x=0;x<width;x++,pixel+=4){
      const auto& sx=state.sampleX[x];
      const __m128 top=_mm_add_ps(_mm_mul_ps(PixelValue(topRow+sx.lower*4),_mm_set1_ps(static_cast<float>((256-sx.weight)*(256-sy.weight)))),_mm_mul_ps(PixelValue(topRow+sx.upper*4),_mm_set1_ps(static_cast<float>(sx.weight*(256-sy.weight)))));
      const __m128 bottom=_mm_add_ps(_mm_mul_ps(PixelValue(bottomRow+sx.lower*4),_mm_set1_ps(static_cast<float>((256-sx.weight)*sy.weight))),_mm_mul_ps(PixelValue(bottomRow+sx.upper*4),_mm_set1_ps(static_cast<float>(sx.weight*sy.weight))));
      const __m128i value=_mm_cvttps_epi32(_mm_mul_ps(_mm_add_ps(_mm_add_ps(top,bottom),rounding),normalization));
      const unsigned packed=static_cast<unsigned>(_mm_cvtsi128_si32(_mm_packus_epi16(_mm_packs_epi32(value,zero),zero)));std::memcpy(pixel,&packed,sizeof(packed));
    }
  }
  return output;
}
inline void DestroyEntry(State& state) noexcept {
  if(state.entryWindow){const HWND window=state.entryWindow;state.entryWindow=nullptr;DestroyWindow(window);}
  state.engineInvisible=false;
}
inline LRESULT CALLBACK EntryProcedure(HWND window,UINT message,WPARAM wparam,LPARAM lparam) noexcept {
  auto* state=reinterpret_cast<State*>(GetWindowLongPtrW(window,GWLP_USERDATA));
  if(message==WM_NCCREATE){state=static_cast<State*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(state));}
  if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
  if(state && (message==WM_MOUSEMOVE || message==WM_LBUTTONDOWN || message==WM_LBUTTONUP || message==WM_LBUTTONDBLCLK)){
    const int x=static_cast<int>(std::lround(static_cast<short>(LOWORD(lparam))/state->paintedScaleX));
    const int y=static_cast<int>(std::lround(static_cast<short>(HIWORD(lparam))/state->paintedScaleY));
    const HWND target=state->window;state->forwardingMouse=true;
    const LRESULT result=SendMessageW(target,message,wparam,MAKELPARAM(x,y));
    if(auto* remaining=Find(target))remaining->forwardingMouse=false;
    return result;
  }
  if(message==WM_NCDESTROY && state && state->entryWindow==window)state->entryWindow=nullptr;
  return DefWindowProcW(window,message,wparam,lparam);
}
inline bool EnsureEntry(State& state) noexcept {
  if(state.entryWindow)return true;
  HMODULE own=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&EntryProcedure),&own);
  WNDCLASSW cls{};cls.lpfnWndProc=&EntryProcedure;cls.hInstance=own;cls.lpszClassName=L"Mason.CandidateMotion.Entry";RegisterClassW(&cls);
  state.entryWindow=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE|WS_EX_LAYERED,cls.lpszClassName,nullptr,WS_POPUP,0,0,1,1,state.window,nullptr,own,&state);
  return state.entryWindow!=nullptr;
}
inline bool Present(State& state,ULONGLONG now) noexcept {
  if(!state.frame.pixels || state.awaitingFrame || state.pendingOpen)return false;
  Advance(state,now);
  const Surface& letters=LetterFrame(state,now);
  const Surface& transformed=state.phase==Phase::Entering?Scaled(state,letters):letters;
  const Surface& source=NativeCandidateFrame(state,transformed,now);
  const SIZE size{source.width,source.height};const POINT origin{0,0};
  auto blend=state.blend;blend.SourceConstantAlpha=static_cast<BYTE>(std::lround(state.blend.SourceConstantAlpha*Unit(state.alpha)));
  // The temporary visual can grow beyond 100% without cropping or changing
  // the engine HWND's bounds. It forwards candidate clicks without activation.
  BOOL result=FALSE;
  if(state.phase==Phase::Entering){
    if(!EnsureEntry(state)){InterlockedIncrement(&MasonMotionDiagnostics.failures);return false;}
    RECT bounds{};GetWindowRect(state.window,&bounds);
    const POINT position{bounds.left+state.paintedLeft,bounds.top+state.paintedTop};
    const SIZE engineSize{state.frame.width,state.frame.height};auto invisible=state.blend;invisible.SourceConstantAlpha=0;
    result=state.engineInvisible?TRUE:originalLayer(state.window,nullptr,nullptr,&engineSize,state.frame.dc,&origin,state.colorKey,&invisible,state.flags);
    if(result)state.engineInvisible=true;
    if(result)result=originalLayer(state.entryWindow,nullptr,&position,&size,source.dc,&origin,state.colorKey,&blend,state.flags);
    if(result && state.requested && IsWindowVisible(state.window) && !IsWindowVisible(state.entryWindow))originalShow(state.entryWindow,SW_SHOWNOACTIVATE);
  }else{
    result=originalLayer(state.window,nullptr,nullptr,&size,source.dc,&origin,state.colorKey,&blend,state.flags);
    DestroyEntry(state);state.paintedLeft=state.paintedTop=0;state.paintedScaleX=state.paintedScaleY=1;
  }
  if(!result){InterlockedIncrement(&MasonMotionDiagnostics.failures);return false;}
  state.paintedScale=state.scale;
  InterlockedExchange(&MasonMotionDiagnostics.lastAlpha,blend.SourceConstantAlpha);
  InterlockedExchange(&MasonMotionDiagnostics.lastScale,static_cast<LONG>(std::lround(state.scale*1000)));
  InterlockedExchange(&MasonMotionDiagnostics.lastPhase,static_cast<LONG>(state.phase));
  if(state.phase==Phase::Leaving)ClickThrough(state,true);
  return true;
}
inline void FinishHidden(State& state) noexcept {
  ClearCandidates(state);
  ClearLetters(state);
  DestroyEntry(state);
  if(state.ghost){StopTimer(state);DestroyWindow(state.window);return;}
  StopTimer(state);originalShow(state.window,SW_HIDE);ClickThrough(state,false);
  state.phase=Phase::Hidden;state.alpha=0;state.scale=StartScale;state.alphaVelocity=state.scaleVelocity=0;state.mixing=false;state.pendingOpen=false;
  state.blendFrom.Reset();state.composition.Reset();state.presentation.Reset();
}
inline void FinishImmediate(State& state) noexcept {
  ClearCandidates(state);
  ClearLetters(state);
  DestroyEntry(state);
  StopTimer(state);ClickThrough(state,false);state.mixing=false;state.pendingOpen=false;
  state.blendFrom.Reset();state.composition.Reset();state.presentation.Reset();
  if(state.requested){state.phase=Phase::Steady;state.alpha=state.scale=1;Present(state,Now());}
  else FinishHidden(state);
}
inline VOID CALLBACK Tick(HWND window,UINT,UINT_PTR timer,DWORD) noexcept {
  State* state=Find(window);if(!state || state->timer!=timer)return;
  InterlockedIncrement(&MasonMotionDiagnostics.ticks);
  if(state->clock && state->clock->failed.load(std::memory_order_acquire)){
    StopTimer(*state);state->timer=SetTimer(window,timer,16,&Tick);
    if(!state->timer){InterlockedIncrement(&MasonMotionDiagnostics.failures);FinishImmediate(*state);return;}
  }
  if(!Enabled()){FinishImmediate(*state);return;}
  const ULONGLONG now=Now();Advance(*state,now);
  if(state->phase==Phase::Hidden){Present(*state,now);FinishHidden(*state);return;}
  if(!Present(*state,now) && !state->awaitingFrame){FinishImmediate(*state);return;}
  if(state->phase==Phase::Steady && !state->mixing && !LettersActive(*state,now) && !CandidatesActive(*state,now))StopTimer(*state);
}
inline void Arm(State& state) noexcept {
  if(state.timer)return;
  constexpr ULONG_PTR timerTag=static_cast<ULONG_PTR>(1)<<(sizeof(ULONG_PTR)*8-1);
  const ULONG_PTR token=timerTag|((frameSerial.fetch_add(1,std::memory_order_relaxed)+1)&~timerTag);
  if(FrameMessage()){
    auto* clock=new(std::nothrow) FrameClock();
    if(clock){
      clock->window=state.window;clock->token=token;
      clock->stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);
      // Windows 10 1803+ supports this local high-resolution timer. It does
      // not change the system-wide timer period or affect another application.
      clock->timer=CreateWaitableTimerExW(nullptr,nullptr,0x00000002,TIMER_ALL_ACCESS);
      if(!clock->timer)clock->timer=CreateWaitableTimerExW(nullptr,nullptr,0,TIMER_ALL_ACCESS);
      if(clock->stop && clock->timer){
        state.clock=clock;state.timer=token;
        clock->thread=CreateThread(nullptr,0,&FrameWorker,clock,0,nullptr);
        if(clock->thread)return;
        state.clock=nullptr;state.timer=0;
      }
      delete clock;
    }
  }
  state.timer=SetTimer(state.window,token,16,&Tick);
  if(!state.timer){InterlockedIncrement(&MasonMotionDiagnostics.failures);FinishImmediate(state);}
}
inline void CancelExitWindow() noexcept {if(exitWindow){const HWND window=exitWindow;exitWindow=nullptr;DestroyWindow(window);}}
inline void ResolveAnchor(State& state) noexcept {
  state.anchorX=state.anchorY=0;
  GUITHREADINFO gui{sizeof(GUITHREADINFO)};RECT bounds{};
  if(!GetGUIThreadInfo(GetCurrentThreadId(),&gui) || !gui.hwndCaret || !GetWindowRect(state.window,&bounds))return;
  POINT caret{gui.rcCaret.left,gui.rcCaret.bottom};
  if(!ClientToScreen(gui.hwndCaret,&caret))return;
  if(bounds.right>bounds.left)state.anchorX=Unit(static_cast<double>(caret.x-bounds.left)/(bounds.right-bounds.left));
  state.anchorY=bounds.bottom<=caret.y?1:0;
}
inline void Open(State& state,bool paint=true) noexcept {
  CancelExitWindow();
  if(state.requested)return;
  const ULONGLONG now=Now();Advance(state,now);ClickThrough(state,false);
  if(state.phase==Phase::Hidden){state.alpha=StartAlpha;state.scale=StartScale;state.alphaVelocity=state.scaleVelocity=0;state.mixing=false;}
  // Weasel can show its non-layered ATL window before DoPaint supplies the
  // first bitmap. Start the clock when that bitmap exists, not on a blank HWND.
  if(!state.frame.pixels){state.requested=true;state.phase=Phase::Entering;state.pendingOpen=true;return;}
  state.pendingOpen=false;
  state.requested=true;state.phase=Phase::Entering;state.start=state.last=now;ResolveAnchor(state);
  state.fromAlpha=state.alpha;state.fromScale=state.scale;
  state.alphaDuration=OpacityMs*(1-state.alpha);
  state.scaleDuration=EnterMs*Unit((1-state.scale)/(1-StartScale));
  InterlockedIncrement(&MasonMotionDiagnostics.opens);if(paint)Present(state,now);Arm(state);
}
inline bool Close(State& state) noexcept {
  if(state.requested)InterlockedIncrement(&MasonMotionDiagnostics.closes);
  state.requested=false;state.pendingOpen=false;FinishHidden(state);return false;
}
inline void CreateExitWindow(State& state) noexcept;
inline LRESULT CALLBACK Subclass(HWND window,UINT message,WPARAM wparam,LPARAM lparam,UINT_PTR id,DWORD_PTR reference) noexcept {
  State* state=reinterpret_cast<State*>(reference);
  if(message==FrameMessage()){
    if(state->clock && state->timer==wparam && state->clock->token==wparam){
      state->clock->pending.store(false,std::memory_order_release);Tick(window,0,wparam,0);
    }
    return 0;
  }
  if(message==WM_NCDESTROY){
    ClearCandidates(*state);
    ClearLetters(*state);
    StopTimer(*state);RemovePropW(window,StateProperty);RemoveWindowSubclass(window,&Subclass,id);
    delete state;InterlockedIncrement(&MasonMotionDiagnostics.detached);InterlockedDecrement(&MasonMotionDiagnostics.liveWindows);
    return DefSubclassProc(window,message,wparam,lparam);
  }
  if(message==WM_SETTINGCHANGE && !Enabled())FinishImmediate(*state);
  if(message==WM_NCHITTEST && !state->requested)return HTTRANSPARENT;
  if(state->requested && state->entryWindow && !state->forwardingMouse && (message==WM_MOUSEMOVE || message==WM_LBUTTONDOWN || message==WM_LBUTTONUP || message==WM_LBUTTONDBLCLK)){
    const double x=static_cast<short>(LOWORD(lparam)),y=static_cast<short>(HIWORD(lparam));
    const int mappedX=static_cast<int>(std::lround((x-state->paintedLeft)/state->paintedScaleX));
    const int mappedY=static_cast<int>(std::lround((y-state->paintedTop)/state->paintedScaleY));
    lparam=MAKELPARAM(mappedX,mappedY);
  }
  return DefSubclassProc(window,message,wparam,lparam);
}
inline State* Attach(HWND window,bool beforePaint=false) noexcept {
  if(State* state=Find(window))return state;
  if(!Candidate(window,!beforePaint) || MasonMotionDiagnostics.liveWindows>=8)return nullptr;
  auto* state=new(std::nothrow) State();if(!state)return nullptr;state->window=window;
  state->requested=IsWindowVisible(window)!=FALSE;
  if(state->requested){state->phase=Phase::Entering;state->pendingOpen=true;}
  if(!SetPropW(window,StateProperty,state) || !SetWindowSubclass(window,&Subclass,reinterpret_cast<UINT_PTR>(&Subclass),reinterpret_cast<DWORD_PTR>(state))){
    RemovePropW(window,StateProperty);delete state;return nullptr;
  }
  InterlockedIncrement(&MasonMotionDiagnostics.attached);InterlockedIncrement(&MasonMotionDiagnostics.liveWindows);return state;
}
inline BOOL WINAPI Layer(HWND window,HDC destination,const POINT* position,const SIZE* size,HDC source,const POINT* origin,COLORREF key,const BLENDFUNCTION* blend,DWORD flags) noexcept {
  InterlockedIncrement(&MasonMotionDiagnostics.layerCalls);
  if(!active || !source || !size || !blend || !(flags&ULW_ALPHA) || !Candidate(window)){
    if(textFrame.ready){ResetGlyphScratch();glyphFrame=GlyphFrame{};glyphPending.clear();candidateFrame.clear();}
    return originalLayer(window,destination,position,size,source,origin,key,blend,flags);
  }
  State* state=Attach(window);if(!state)return originalLayer(window,destination,position,size,source,origin,key,blend,flags);
  const Signature signature=TakeTextFrame();const POINT point=origin?*origin:POINT{};
  const bool firstFrame=!state->frame.pixels;
  if(!state->incoming.Copy(source,point,size->cx,size->cy))return originalLayer(window,destination,position,size,source,origin,key,blend,flags);
  const ULONGLONG now=Now();Advance(*state,now);
  const bool sameSize=state->frame.width==size->cx && state->frame.height==size->cy && state->frame.pixels;
  const bool selection=false && Enabled() && state->requested && state->phase==Phase::Steady && sameSize && signature.valid && state->signature.valid && signature.hash==state->signature.hash &&
      std::memcmp(state->frame.pixels,state->incoming.pixels,state->frame.Bytes())!=0;
  if(selection && state->blendFrom.Copy(Compose(*state,now))){state->mixing=true;state->mixStart=now;InterlockedIncrement(&MasonMotionDiagnostics.selections);}
  else {state->mixing=false;state->blendFrom.Reset();state->composition.Reset();}
  state->frame.Swap(state->incoming);state->signature=signature;state->blend=*blend;state->colorKey=key;state->flags=flags;state->awaitingFrame=false;state->engineInvisible=false;
  UpdateLetters(*state,TakeGlyphs(),point,now);
  UpdateCandidates(*state,point,now);
  InterlockedIncrement(&MasonMotionDiagnostics.frames);
  if(!Enabled()){
    StopTimer(*state);ClickThrough(*state,false);state->mixing=false;state->pendingOpen=false;state->alpha=state->scale=1;
    state->phase=state->requested?Phase::Steady:Phase::Hidden;
    return originalLayer(window,destination,position,size,source,origin,key,blend,flags);
  }
  // Apply the caller's exact position before presenting cached frames. Timers
  // pass a null position, so caret movement never jumps back to an old location.
  if(position)originalPosition(window,nullptr,position->x,position->y,size->cx,size->cy,SWP_NOACTIVATE|SWP_NOZORDER|SWP_NOREDRAW);
  if(firstFrame && state->pendingOpen && state->requested){state->pendingOpen=false;state->requested=false;state->phase=Phase::Hidden;Open(*state,false);}
  const bool result=Present(*state,now);
  if(state->mixing || state->phase==Phase::Entering || state->phase==Phase::Leaving || LettersActive(*state,now) || CandidatesActive(*state,now))Arm(*state);
  if(result)return TRUE;
  FinishImmediate(*state);return originalLayer(window,destination,position,size,source,origin,key,blend,flags);
}
inline BOOL WINAPI Show(HWND window,int command) noexcept {
  InterlockedIncrement(&MasonMotionDiagnostics.showCalls);
  if(!active || !Candidate(window,false))return originalShow(window,command);
  State* state=Attach(window,true);
  if(!state || !Enabled()){
    if(state){state->requested=command!=SW_HIDE;FinishImmediate(*state);}
    return originalShow(window,command);
  }
  const BOOL previous=IsWindowVisible(window);
  if(command==SW_HIDE){if(Close(*state))return previous;return originalShow(window,command);}
  Open(*state);const BOOL result=originalShow(window,command);if(state->entryWindow && !IsWindowVisible(state->entryWindow))originalShow(state->entryWindow,SW_SHOWNOACTIVATE);return result;
}
inline BOOL WINAPI Position(HWND window,HWND after,int x,int y,int width,int height,UINT flags) noexcept {
  InterlockedIncrement(&MasonMotionDiagnostics.positionCalls);
  if(!active || !Candidate(window,false))return originalPosition(window,after,x,y,width,height,flags);
  State* state=Find(window);
  if(!state && (flags&(SWP_SHOWWINDOW|SWP_HIDEWINDOW)))state=Attach(window,true);
  if(state && !(flags&SWP_NOSIZE) && (width!=state->frame.width || height!=state->frame.height))state->awaitingFrame=true;
  if(state && Enabled()){
    if((flags&SWP_HIDEWINDOW) && Close(*state))flags&=~SWP_HIDEWINDOW;
    if(flags&SWP_SHOWWINDOW)Open(*state);
  }
  return originalPosition(window,after,x,y,width,height,flags);
}
inline bool Patch(IMAGE_THUNK_DATA* slot,void* expected,void* replacement) noexcept {
  if(reinterpret_cast<void*>(slot->u1.Function)!=expected)return false;
  DWORD protection=0;if(!VirtualProtect(&slot->u1.Function,sizeof(void*),PAGE_READWRITE,&protection))return false;
  const bool replaced=InterlockedCompareExchangePointer(reinterpret_cast<PVOID volatile*>(&slot->u1.Function),replacement,expected)==expected;
  DWORD unused=0;VirtualProtect(&slot->u1.Function,sizeof(void*),protection,&unused);return replaced;
}
struct ExitCreation {State* state;bool attached=false;};
inline LRESULT CALLBACK ExitProcedure(HWND window,UINT message,WPARAM wparam,LPARAM lparam) noexcept {
  if(message==WM_NCCREATE){
    auto* creation=static_cast<ExitCreation*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
    auto* state=creation->state;state->window=window;
    if(!SetPropW(window,StateProperty,state))return FALSE;
    creation->attached=true;
    InterlockedIncrement(&MasonMotionDiagnostics.attached);InterlockedIncrement(&MasonMotionDiagnostics.liveWindows);
  }
  if(message==WM_NCHITTEST)return HTTRANSPARENT;
  if(message==FrameMessage()){
    if(auto* state=Find(window))if(state->clock && state->timer==wparam && state->clock->token==wparam){
      state->clock->pending.store(false,std::memory_order_release);Tick(window,0,wparam,0);
    }
    return 0;
  }
  if(message==WM_NCDESTROY){
    if(auto* state=Find(window)){StopTimer(*state);RemovePropW(window,StateProperty);delete state;
      InterlockedIncrement(&MasonMotionDiagnostics.detached);InterlockedDecrement(&MasonMotionDiagnostics.liveWindows);}
    if(exitWindow==window)exitWindow=nullptr;
  }
  return DefWindowProcW(window,message,wparam,lparam);
}
inline void CreateExitWindow(State& source) noexcept {
  // TSF destroys its candidate HWND at commit/cancel. Never defer engine teardown:
  // a short, independent, click-through snapshot supplies the visual dismissal.
  CancelExitWindow();RECT bounds{};if(!GetWindowRect(source.window,&bounds))return;
  const ULONGLONG now=Now();Advance(source,now);
  auto* next=new(std::nothrow) State();if(!next)return;
  if(!next->frame.Copy(Compose(source,now))){delete next;return;}
  next->ghost=true;next->phase=Phase::Leaving;next->start=next->last=now;
  next->alpha=source.alpha;next->scale=source.scale;next->alphaVelocity=source.alphaVelocity;next->scaleVelocity=source.scaleVelocity;
  next->anchorX=source.anchorX;next->anchorY=source.anchorY;next->blend=source.blend;next->colorKey=source.colorKey;next->flags=source.flags;
  HMODULE own=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&ExitProcedure),&own);
  WNDCLASSW cls{};cls.lpfnWndProc=&ExitProcedure;cls.hInstance=own;cls.lpszClassName=L"Mason.CandidateMotion.Exit";RegisterClassW(&cls);
  ExitCreation creation{next};
  const HWND window=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE|WS_EX_LAYERED|WS_EX_TRANSPARENT,
      cls.lpszClassName,nullptr,WS_POPUP,bounds.left,bounds.top,bounds.right-bounds.left,bounds.bottom-bounds.top,nullptr,nullptr,own,&creation);
  if(!window){if(!creation.attached)delete next;return;}
  exitWindow=window;InterlockedIncrement(&MasonMotionDiagnostics.exitWindows);
  if(!Present(*next,now)){DestroyWindow(window);return;}
  originalShow(window,SW_SHOWNOACTIVATE);Arm(*next);
}
inline bool AdapterImport(HMODULE module) noexcept {
  auto* base=reinterpret_cast<unsigned char*>(module);auto* dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
  if(dos->e_magic!=IMAGE_DOS_SIGNATURE)return false;
  auto* nt=reinterpret_cast<IMAGE_NT_HEADERS*>(base+dos->e_lfanew);if(nt->Signature!=IMAGE_NT_SIGNATURE)return false;
  const auto directory=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];if(!directory.VirtualAddress)return false;
  auto* imports=reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base+directory.VirtualAddress);
  for(size_t i=0;i<directory.Size/sizeof(IMAGE_IMPORT_DESCRIPTOR) && imports[i].Name;i++){
    const char* name=reinterpret_cast<const char*>(base+imports[i].Name);
    if(_stricmp(name,"Mason64.dll")==0 || _stricmp(name,"Mason32.dll")==0 || _stricmp(name,"Mason.dll")==0)return true;
  }
  return false;
}
inline void InstallModule(HMODULE module) noexcept {
  wchar_t path[MAX_PATH]{};if(!GetModuleFileNameW(module,path,MAX_PATH))return;
  const wchar_t* file=wcsrchr(path,L'\\');file=file?file+1:path;
  const bool server=module==GetModuleHandleW(nullptr) && _wcsicmp(file,L"WeaselServer.exe")==0;
  const bool client=(_wcsicmp(file,L"weasel.dll")==0 || _wcsicmp(file,L"weaselx64.dll")==0 ||
      _wcsicmp(file,L"weasel.ime")==0 || _wcsicmp(file,L"weaselx64.ime")==0) && AdapterImport(module);
  if(!server && !client)return;
  AcquireSRWLockExclusive(&moduleLock);
  for(unsigned i=0;i<ownerCount;i++)if(ownerModules[i]==module){ReleaseSRWLockExclusive(&moduleLock);return;}
  if(ownerCount==8){ReleaseSRWLockExclusive(&moduleLock);return;}
  const HMODULE user=GetModuleHandleW(L"user32.dll");if(!user){ReleaseSRWLockExclusive(&moduleLock);return;}
  if(!originalLayer){
    originalLayer=reinterpret_cast<LayerFunction>(GetProcAddress(user,"UpdateLayeredWindow"));
    originalShow=reinterpret_cast<ShowFunction>(GetProcAddress(user,"ShowWindow"));
    originalPosition=reinterpret_cast<PositionFunction>(GetProcAddress(user,"SetWindowPos"));
  }
  if(!originalLayer || !originalShow || !originalPosition){ReleaseSRWLockExclusive(&moduleLock);return;}
  auto* base=reinterpret_cast<unsigned char*>(module);
  auto* dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);if(dos->e_magic!=IMAGE_DOS_SIGNATURE){ReleaseSRWLockExclusive(&moduleLock);return;}
  auto* nt=reinterpret_cast<IMAGE_NT_HEADERS*>(base+dos->e_lfanew);if(nt->Signature!=IMAGE_NT_SIGNATURE){ReleaseSRWLockExclusive(&moduleLock);return;}
  const auto directory=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];if(!directory.VirtualAddress){ReleaseSRWLockExclusive(&moduleLock);return;}
  auto* imports=reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base+directory.VirtualAddress);
  unsigned patched=0;
  for(size_t i=0;i<directory.Size/sizeof(IMAGE_IMPORT_DESCRIPTOR) && imports[i].Name;i++){
    if(!imports[i].FirstThunk)continue;
    auto* table=reinterpret_cast<IMAGE_THUNK_DATA*>(base+imports[i].FirstThunk);
    for(size_t j=0;table[j].u1.Function && j<4096;j++){
      if(Patch(table+j,reinterpret_cast<void*>(originalLayer),reinterpret_cast<void*>(&Layer)))patched|=1;
      else if(Patch(table+j,reinterpret_cast<void*>(originalShow),reinterpret_cast<void*>(&Show)))patched|=2;
      else if(Patch(table+j,reinterpret_cast<void*>(originalPosition),reinterpret_cast<void*>(&Position)))patched|=4;
    }
  }
  if((patched&3)==3){
    // Keep only this Mason-owned module alive, like the retained text adapter.
    HMODULE pinned=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(module),&pinned);
    ownerModules[ownerCount++]=module;active=true;
    InterlockedOr(&MasonMotionDiagnostics.importHooks,patched);
    InterlockedIncrement(server?&MasonMotionDiagnostics.serverModules:&MasonMotionDiagnostics.clientModules);
    InterlockedExchange(&MasonMotionDiagnostics.initialized,1);
  }
  ReleaseSRWLockExclusive(&moduleLock);
}
inline void Install() noexcept {InstallModule(GetModuleHandleW(nullptr));}
inline void InstallFromCaller(void* address) noexcept {
  HMODULE module=nullptr;
  if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(address),&module))InstallModule(module);
}
}
