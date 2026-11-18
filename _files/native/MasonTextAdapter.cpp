#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <new>
#include <utility>
#include <vector>
#include "MasonCorrectionText.h"

// Numeric diagnostics only: no input text or personal data is recorded.
struct MasonTextDiagnosticData {
  DWORD size;
  volatile LONG dwriteFactories, d2dFactories, layouts, markedLayouts;
  volatile LONG wicTargets, hwndTargets, dxgiTargets, dcTargets;
  volatile LONG deviceTargets, deviceContexts, compatibleTargets;
  volatile LONG drawLayouts, drawMarkedLayouts, lastMarkedStart, lastMarkedLength;
  volatile LONG drawTexts, drawMarkedTexts;
};
extern "C" { MasonTextDiagnosticData MasonTextDiagnostics = {sizeof(MasonTextDiagnosticData)}; }

// Only the factories imported by Mason are wrapped. Shared system COM vtables
// are never changed. A marker belongs to its layout object, so releasing or
// reusing a layout cannot accidentally colour unrelated application text.
namespace {
using CreateDWrite = HRESULT(WINAPI*)(DWRITE_FACTORY_TYPE, REFIID, IUnknown**);
using CreateD2D = HRESULT(WINAPI*)(D2D1_FACTORY_TYPE, REFIID, const D2D1_FACTORY_OPTIONS*, void**);
INIT_ONCE initialize = INIT_ONCE_STATIC_INIT;
HMODULE systemDWrite = nullptr, systemD2D = nullptr;
CreateDWrite createDWrite = nullptr;
CreateD2D createD2D = nullptr;

BOOL CALLBACK Initialize(PINIT_ONCE, PVOID, PVOID*) {
  wchar_t directory[MAX_PATH]{};
  const UINT length = GetSystemDirectoryW(directory, MAX_PATH);
  if (!length || length >= MAX_PATH) return FALSE;
  const std::wstring base(directory, length);
  if (!systemDWrite) systemDWrite = LoadLibraryW((base + L"\\DWrite.dll").c_str());
  if (!systemD2D) systemD2D = LoadLibraryW((base + L"\\d2d1.dll").c_str());
  if (!systemDWrite || !systemD2D) return FALSE;
  createDWrite = reinterpret_cast<CreateDWrite>(GetProcAddress(systemDWrite, "DWriteCreateFactory"));
  createD2D = reinterpret_cast<CreateD2D>(GetProcAddress(systemD2D, "D2D1CreateFactory"));
  if (!createDWrite || !createD2D) return FALSE;
  // A client may be unloaded before a retained layout. Keep this small adapter
  // loaded until process exit so any outstanding proxy always has live code.
  HMODULE own = nullptr;
  return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
      reinterpret_cast<LPCWSTR>(&Initialize), &own);
}

// Direct2D resources accept render targets in other resource methods. Keep the
// genuine system object (and its complete COM identity) rather than forwarding
// a different object's interface into those native methods. Each Mason-created
// object gets its own copied vtable; no shared system vtable is writable here.
SRWLOCK targetLock = SRWLOCK_INIT;
struct NativeTable {
  void** original;
  std::vector<void*> storage;
  std::vector<DWRITE_TEXT_RANGE> errors;
};
std::vector<NativeTable*> nativeTables;
constexpr size_t TableLimit = 512;

bool Readable(const MEMORY_BASIC_INFORMATION& region) noexcept {
  return region.State == MEM_COMMIT && !(region.Protect & (PAGE_NOACCESS | PAGE_GUARD));
}
bool Executable(void* address) noexcept {
  MEMORY_BASIC_INFORMATION region{};
  if (!VirtualQuery(address, &region, sizeof(region)) || !Readable(region)) return false;
  return (region.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
}
template<class Function> Function Original(void* object, size_t index) noexcept {
  if (!object) return nullptr;
  void** current = *reinterpret_cast<void***>(object);
  Function result = nullptr;
  AcquireSRWLockShared(&targetLock);
  for (auto* table : nativeTables) if (table->storage.data() + 1 == current && index + 1 < table->storage.size()) {
    result = reinterpret_cast<Function>(table->original[index]); break;
  }
  ReleaseSRWLockShared(&targetLock); return result;
}
struct NativePatch { size_t index; void* method; };
bool AdaptNativeObject(void* object, size_t minimum, const NativePatch* patches, size_t patchCount, const std::vector<DWRITE_TEXT_RANGE>* errors = nullptr) noexcept {
  if (!object) return false;
  MEMORY_BASIC_INFORMATION objectRegion{};
  if (!VirtualQuery(object, &objectRegion, sizeof(objectRegion)) || !Readable(objectRegion) ||
      !(objectRegion.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
  AcquireSRWLockExclusive(&targetLock);
  auto** original = *reinterpret_cast<void***>(object);
  for (auto* table : nativeTables) if (table->storage.data() + 1 == original) {
    ReleaseSRWLockExclusive(&targetLock); return true;
  }
  auto* table = new(std::nothrow) NativeTable();
  bool adapted = false;
  if (table) try {
    table->original = original;
    if (errors) table->errors = *errors;
    MEMORY_BASIC_INFORMATION prefix{};
    void* previous = nullptr;
    if (VirtualQuery(original - 1, &prefix, sizeof(prefix)) && Readable(prefix)) previous = original[-1];
    // Preserve the RTTI prefix too: native Direct2D methods may cast between
    // internal resource classes when a bitmap uses another native render target.
    table->storage.push_back(previous);
    for (size_t index = 0; index < TableLimit; index++) {
      MEMORY_BASIC_INFORMATION region{};
      if (!VirtualQuery(original + index, &region, sizeof(region)) || !Readable(region) ||
          reinterpret_cast<ULONG_PTR>(original + index + 1) >
              reinterpret_cast<ULONG_PTR>(region.BaseAddress) + region.RegionSize) break;
      void* method = original[index];
      if (!Executable(method)) break;
      table->storage.push_back(method);
    }
    if (table->storage.size() >= minimum + 1) {
      for (size_t i = 0; i < patchCount; i++) table->storage[patches[i].index + 1] = patches[i].method;
      nativeTables.push_back(table);
      auto* field = reinterpret_cast<PVOID volatile*>(object);
      adapted = InterlockedCompareExchangePointer(field, table->storage.data() + 1, original) == original;
      if (!adapted) nativeTables.pop_back();
    }
  } catch (...) {}
  if (!adapted) delete table;
  // The adapter is pinned while native objects exist. Release removes the copy
  // after the genuine COM object reaches zero references; no layout data or
  // additional COM references are retained here.
  ReleaseSRWLockExclusive(&targetLock); return adapted;
}
using ReleaseFunction = ULONG(STDMETHODCALLTYPE*)(IUnknown*);
ULONG STDMETHODCALLTYPE NativeRelease(IUnknown* object) noexcept {
  void** current = *reinterpret_cast<void***>(object);
  const auto original = Original<ReleaseFunction>(object, 2);
  if (!original) return 0;
  const ULONG count = original(object);
  if (!count) {
    NativeTable* retired = nullptr;
    AcquireSRWLockExclusive(&targetLock);
    for (auto at = nativeTables.begin(); at != nativeTables.end(); ++at) {
      if ((*at)->storage.data() + 1 == current) { retired = *at; nativeTables.erase(at); break; }
    }
    ReleaseSRWLockExclusive(&targetLock); delete retired;
  }
  return count;
}

std::vector<DWRITE_TEXT_RANGE> LayoutRanges(IDWriteTextLayout* layout) {
  std::vector<DWRITE_TEXT_RANGE> ranges;
  if (!layout) return ranges;
  void** current = *reinterpret_cast<void***>(layout);
  AcquireSRWLockShared(&targetLock);
  try {
    for (auto* table : nativeTables) if (table->storage.data() + 1 == current) { ranges = table->errors; break; }
  } catch (...) { ReleaseSRWLockShared(&targetLock); throw; }
  ReleaseSRWLockShared(&targetLock); return ranges;
}
void RecordPlan(const MasonCorrectionText::Plan& plan, UINT32 length) noexcept {
  InterlockedIncrement(&MasonTextDiagnostics.layouts);
  if (plan.errors.empty()) return;
  InterlockedIncrement(&MasonTextDiagnostics.markedLayouts);
  InterlockedExchange(&MasonTextDiagnostics.lastMarkedStart, plan.errors.front().startPosition);
  InterlockedExchange(&MasonTextDiagnostics.lastMarkedLength, length);
}
void AdaptLayout(IDWriteTextLayout** output, const MasonCorrectionText::Plan& plan) noexcept {
  if (!output || !*output || plan.errors.empty()) return;
  const NativePatch patch = {2, reinterpret_cast<void*>(&NativeRelease)};
  AdaptNativeObject(*output, 67, &patch, 1, &plan.errors);
}
using CreateLayoutFunction = HRESULT(STDMETHODCALLTYPE*)(IDWriteFactory*, const WCHAR*, UINT32,
    IDWriteTextFormat*, FLOAT, FLOAT, IDWriteTextLayout**);
HRESULT STDMETHODCALLTYPE NativeTextLayout(IDWriteFactory* factory, const WCHAR* text, UINT32 length,
    IDWriteTextFormat* format, FLOAT width, FLOAT height, IDWriteTextLayout** output) noexcept {
  const auto original = Original<CreateLayoutFunction>(factory, 18); if (!original) return E_UNEXPECTED;
  if (!text || length > 1024) return original(factory, text, length, format, width, height, output);
  try {
    const auto plan = MasonCorrectionText::Parse(text, length); RecordPlan(plan, length);
    const HRESULT result = original(factory, plan.text.c_str(), length, format, width, height, output);
    if (SUCCEEDED(result)) AdaptLayout(output, plan); return result;
  } catch (...) { return original(factory, text, length, format, width, height, output); }
}
using CreateGdiLayoutFunction = HRESULT(STDMETHODCALLTYPE*)(IDWriteFactory*, const WCHAR*, UINT32,
    IDWriteTextFormat*, FLOAT, FLOAT, FLOAT, const DWRITE_MATRIX*, BOOL, IDWriteTextLayout**);
HRESULT STDMETHODCALLTYPE NativeGdiTextLayout(IDWriteFactory* factory, const WCHAR* text, UINT32 length,
    IDWriteTextFormat* format, FLOAT width, FLOAT height, FLOAT pixels,
    const DWRITE_MATRIX* transform, BOOL natural, IDWriteTextLayout** output) noexcept {
  const auto original = Original<CreateGdiLayoutFunction>(factory, 19); if (!original) return E_UNEXPECTED;
  if (!text || length > 1024) return original(factory, text, length, format, width, height, pixels, transform, natural, output);
  try {
    const auto plan = MasonCorrectionText::Parse(text, length); RecordPlan(plan, length);
    const HRESULT result = original(factory, plan.text.c_str(), length, format, width, height, pixels, transform, natural, output);
    if (SUCCEEDED(result)) AdaptLayout(output, plan); return result;
  } catch (...) { return original(factory, text, length, format, width, height, pixels, transform, natural, output); }
}
void AdaptWriteFactory(IDWriteFactory* factory) noexcept {
  const NativePatch patches[] = {{2, reinterpret_cast<void*>(&NativeRelease)},
      {18, reinterpret_cast<void*>(&NativeTextLayout)}, {19, reinterpret_cast<void*>(&NativeGdiTextLayout)}};
  AdaptNativeObject(factory, 24, patches, sizeof(patches)/sizeof(patches[0]));
}

void AdaptTarget(ID2D1RenderTarget* target) noexcept;
using DrawLayoutFunction = MasonCorrectionText::DrawTextLayoutFunction;
void STDMETHODCALLTYPE NativeDrawLayout(ID2D1RenderTarget* target, D2D1_POINT_2F origin,
    IDWriteTextLayout* layout, ID2D1Brush* normal, D2D1_DRAW_TEXT_OPTIONS options) noexcept {
  const auto original = Original<DrawLayoutFunction>(target, 28); if (!original) return;
  InterlockedIncrement(&MasonTextDiagnostics.drawLayouts);
  try {
    const auto ranges = LayoutRanges(layout);
    if (!ranges.empty()) {
      InterlockedIncrement(&MasonTextDiagnostics.drawMarkedLayouts);
      MasonCorrectionText::Draw(target, origin, layout, normal, ranges, options, original); return;
    }
  } catch (...) {}
  original(target, origin, layout, normal, options);
}
using DrawTextFunction = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, const WCHAR*, UINT32,
    IDWriteTextFormat*, const D2D1_RECT_F*, ID2D1Brush*, D2D1_DRAW_TEXT_OPTIONS, DWRITE_MEASURING_MODE);
void STDMETHODCALLTYPE NativeDrawText(ID2D1RenderTarget* target, const WCHAR* text, UINT32 length,
    IDWriteTextFormat* format, const D2D1_RECT_F* bounds, ID2D1Brush* normal,
    D2D1_DRAW_TEXT_OPTIONS options, DWRITE_MEASURING_MODE measuring) noexcept {
  const auto original = Original<DrawTextFunction>(target, 27); if (!original) return;
  InterlockedIncrement(&MasonTextDiagnostics.drawTexts);
  bool marked = false;
  if (text && length <= 1024) for (UINT32 i = 1; i < length; i++)
    if (text[i] == MasonCorrectionText::ErrorMarker && MasonCorrectionText::Letter(text[i-1])) { marked = true; break; }
  if (marked && format && bounds && bounds->right >= bounds->left && bounds->bottom >= bounds->top) {
    try {
      const auto plan = MasonCorrectionText::Parse(text, length);
      struct TextResources {
        IDWriteFactory* factory = nullptr; IDWriteTextLayout* layout = nullptr;
        ~TextResources() { if (layout) layout->Release(); if (factory) factory->Release(); }
      } resource;
      HRESULT result = createDWrite(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
          reinterpret_cast<IUnknown**>(&resource.factory));
      const FLOAT width = bounds->right - bounds->left, height = bounds->bottom - bounds->top;
      if (SUCCEEDED(result)) {
        if (measuring == DWRITE_MEASURING_MODE_NATURAL)
          result = resource.factory->CreateTextLayout(plan.text.c_str(), length, format, width, height, &resource.layout);
        else {
          FLOAT dpiX = 96, dpiY = 96; target->GetDpi(&dpiX, &dpiY);
          D2D1_MATRIX_3X2_F transform{}; target->GetTransform(&transform);
          const DWRITE_MATRIX matrix{transform._11, transform._12, transform._21, transform._22, transform._31, transform._32};
          result = resource.factory->CreateGdiCompatibleTextLayout(plan.text.c_str(), length, format, width, height,
              dpiX / 96.f, &matrix, measuring == DWRITE_MEASURING_MODE_GDI_NATURAL, &resource.layout);
        }
      }
      if (SUCCEEDED(result) && resource.layout) {
        InterlockedIncrement(&MasonTextDiagnostics.drawMarkedTexts);
        InterlockedExchange(&MasonTextDiagnostics.lastMarkedStart, plan.errors.front().startPosition);
        InterlockedExchange(&MasonTextDiagnostics.lastMarkedLength, length);
        MasonCorrectionText::Draw(target, D2D1::Point2F(bounds->left, bounds->top), resource.layout,
            normal, plan.errors, options, Original<DrawLayoutFunction>(target, 28)); return;
      }
    } catch (...) {}
  }
  original(target, text, length, format, bounds, normal, options, measuring);
}
using CreateCompatible = HRESULT(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, const D2D1_SIZE_F*,
    const D2D1_SIZE_U*, const D2D1_PIXEL_FORMAT*, D2D1_COMPATIBLE_RENDER_TARGET_OPTIONS, ID2D1BitmapRenderTarget**);
HRESULT STDMETHODCALLTYPE NativeCompatibleTarget(ID2D1RenderTarget* target, const D2D1_SIZE_F* size,
    const D2D1_SIZE_U* pixels, const D2D1_PIXEL_FORMAT* format, D2D1_COMPATIBLE_RENDER_TARGET_OPTIONS options,
    ID2D1BitmapRenderTarget** output) noexcept {
  const auto original = Original<CreateCompatible>(target, 12); if (!original) return E_UNEXPECTED;
  const HRESULT result = original(target, size, pixels, format, options, output);
  if (SUCCEEDED(result) && output && *output) { InterlockedIncrement(&MasonTextDiagnostics.compatibleTargets); AdaptTarget(*output); }
  return result;
}
void AdaptTarget(ID2D1RenderTarget* target) noexcept {
  const NativePatch patches[] = {{2, reinterpret_cast<void*>(&NativeRelease)}, {12, reinterpret_cast<void*>(&NativeCompatibleTarget)},
      {27, reinterpret_cast<void*>(&NativeDrawText)}, {28, reinterpret_cast<void*>(&NativeDrawLayout)}};
  AdaptNativeObject(target, 57, patches, sizeof(patches)/sizeof(patches[0]));
}
using CreateWicTarget = HRESULT(STDMETHODCALLTYPE*)(ID2D1Factory*, IWICBitmap*, const D2D1_RENDER_TARGET_PROPERTIES*, ID2D1RenderTarget**);
HRESULT STDMETHODCALLTYPE NativeWicTarget(ID2D1Factory* factory, IWICBitmap* bitmap,
    const D2D1_RENDER_TARGET_PROPERTIES* props, ID2D1RenderTarget** output) noexcept {
  const auto original = Original<CreateWicTarget>(factory, 13); if (!original) return E_UNEXPECTED;
  const HRESULT result = original(factory, bitmap, props, output);
  if (SUCCEEDED(result) && output && *output) { InterlockedIncrement(&MasonTextDiagnostics.wicTargets); AdaptTarget(*output); } return result;
}
using CreateHwndTarget = HRESULT(STDMETHODCALLTYPE*)(ID2D1Factory*, const D2D1_RENDER_TARGET_PROPERTIES*, const D2D1_HWND_RENDER_TARGET_PROPERTIES*, ID2D1HwndRenderTarget**);
HRESULT STDMETHODCALLTYPE NativeHwndTarget(ID2D1Factory* factory, const D2D1_RENDER_TARGET_PROPERTIES* props,
    const D2D1_HWND_RENDER_TARGET_PROPERTIES* window, ID2D1HwndRenderTarget** output) noexcept {
  const auto original = Original<CreateHwndTarget>(factory, 14); if (!original) return E_UNEXPECTED;
  const HRESULT result = original(factory, props, window, output);
  if (SUCCEEDED(result) && output && *output) { InterlockedIncrement(&MasonTextDiagnostics.hwndTargets); AdaptTarget(*output); } return result;
}
using CreateDxgiTarget = HRESULT(STDMETHODCALLTYPE*)(ID2D1Factory*, IDXGISurface*, const D2D1_RENDER_TARGET_PROPERTIES*, ID2D1RenderTarget**);
HRESULT STDMETHODCALLTYPE NativeDxgiTarget(ID2D1Factory* factory, IDXGISurface* surface,
    const D2D1_RENDER_TARGET_PROPERTIES* props, ID2D1RenderTarget** output) noexcept {
  const auto original = Original<CreateDxgiTarget>(factory, 15); if (!original) return E_UNEXPECTED;
  const HRESULT result = original(factory, surface, props, output);
  if (SUCCEEDED(result) && output && *output) { InterlockedIncrement(&MasonTextDiagnostics.dxgiTargets); AdaptTarget(*output); } return result;
}
using CreateDcTarget = HRESULT(STDMETHODCALLTYPE*)(ID2D1Factory*, const D2D1_RENDER_TARGET_PROPERTIES*, ID2D1DCRenderTarget**);
HRESULT STDMETHODCALLTYPE NativeDcTarget(ID2D1Factory* factory, const D2D1_RENDER_TARGET_PROPERTIES* props,
    ID2D1DCRenderTarget** output) noexcept {
  const auto original = Original<CreateDcTarget>(factory, 16); if (!original) return E_UNEXPECTED;
  const HRESULT result = original(factory, props, output);
  if (SUCCEEDED(result) && output && *output) { InterlockedIncrement(&MasonTextDiagnostics.dcTargets); AdaptTarget(*output); } return result;
}
void AdaptFactory(ID2D1Factory* factory) noexcept {
  const NativePatch patches[] = {{2, reinterpret_cast<void*>(&NativeRelease)}, {13, reinterpret_cast<void*>(&NativeWicTarget)}, {14, reinterpret_cast<void*>(&NativeHwndTarget)},
      {15, reinterpret_cast<void*>(&NativeDxgiTarget)}, {16, reinterpret_cast<void*>(&NativeDcTarget)}};
  AdaptNativeObject(factory, 17, patches, sizeof(patches)/sizeof(patches[0]));
}

}
extern "C" HRESULT WINAPI MasonDWriteCreateFactory(DWRITE_FACTORY_TYPE type, REFIID iid, IUnknown** output) noexcept {
  InterlockedIncrement(&MasonTextDiagnostics.dwriteFactories);
  if (!InitOnceExecuteOnce(&initialize, Initialize, nullptr, nullptr)) return E_FAIL;
  // A shared DirectWrite factory can already belong to the host application.
  // Mason receives an isolated native factory so only its own object is adapted.
  const auto nativeType = type == DWRITE_FACTORY_TYPE_SHARED ? DWRITE_FACTORY_TYPE_ISOLATED : type;
  const HRESULT result = createDWrite(nativeType, iid, output);
  if (SUCCEEDED(result) && output && *output) {
    IDWriteFactory* factory = nullptr;
    if (SUCCEEDED((*output)->QueryInterface(__uuidof(IDWriteFactory), reinterpret_cast<void**>(&factory)))) {
      AdaptWriteFactory(factory); factory->Release();
    }
  }
  return result;
}
extern "C" HRESULT WINAPI MasonD2D1CreateFactory(D2D1_FACTORY_TYPE type, REFIID iid,
    const D2D1_FACTORY_OPTIONS* options, void** output) noexcept {
  InterlockedIncrement(&MasonTextDiagnostics.d2dFactories);
  if (!InitOnceExecuteOnce(&initialize, Initialize, nullptr, nullptr)) return E_FAIL;
  const HRESULT result = createD2D(type, iid, options, output);
  if (SUCCEEDED(result) && output && *output) {
    ID2D1Factory* factory = nullptr;
    if (SUCCEEDED(static_cast<IUnknown*>(*output)->QueryInterface(__uuidof(ID2D1Factory), reinterpret_cast<void**>(&factory)))) {
      AdaptFactory(factory); factory->Release();
    }
  }
  return result;
}
