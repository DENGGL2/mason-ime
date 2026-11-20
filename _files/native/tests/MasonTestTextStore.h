#pragma once
#include <windows.h>
#include <olectl.h>
#include <textstor.h>
#include <algorithm>
#include <string>

// In-memory text store for the isolated TSF integration test. It owns only the
// generated test input; it never injects keystrokes or reads another application.
class MasonTestTextStore final : public ITextStoreACP {
  LONG references = 1;
  ITextStoreACPSink* sink = nullptr;
  DWORD lock = 0, pending = 0;
  LONG start = 0, end = 0;
  std::wstring text;
  HWND window;
public:
  explicit MasonTestTextStore(HWND owner) : window(owner) {}
  ~MasonTestTextStore() { if (sink) sink->Release(); }
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override {
    if (!output) return E_POINTER; *output = nullptr;
    if (iid != IID_IUnknown && iid != IID_ITextStoreACP) return E_NOINTERFACE;
    *output = static_cast<ITextStoreACP*>(this); AddRef(); return S_OK;
  }
  ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
  ULONG STDMETHODCALLTYPE Release() override {
    LONG count = InterlockedDecrement(&references); if (!count) delete this; return count;
  }
  HRESULT STDMETHODCALLTYPE AdviseSink(REFIID iid, IUnknown* object, DWORD) override {
    if (iid != IID_ITextStoreACPSink || !object) return E_INVALIDARG;
    if (sink) return CONNECT_E_ADVISELIMIT;
    return object->QueryInterface(iid, reinterpret_cast<void**>(&sink));
  }
  HRESULT STDMETHODCALLTYPE UnadviseSink(IUnknown*) override {
    if (!sink) return CONNECT_E_NOCONNECTION;
    sink->Release(); sink = nullptr; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE RequestLock(DWORD flags, HRESULT* result) override {
    if (!result || !sink) return E_INVALIDARG;
    if (lock) {
      if (flags & TS_LF_SYNC) *result = TS_E_SYNCHRONOUS;
      else { pending |= flags; *result = TS_S_ASYNC; }
      return S_OK;
    }
    lock = flags; *result = sink->OnLockGranted(flags); lock = 0;
    while (pending) { DWORD next = pending; pending = 0; lock = next; sink->OnLockGranted(next); lock = 0; }
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetStatus(TS_STATUS* status) override {
    if (!status) return E_POINTER;
    status->dwDynamicFlags = 0; status->dwStaticFlags = TS_SS_NOHIDDENTEXT; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE QueryInsert(LONG a, LONG b, ULONG, LONG* outA, LONG* outB) override {
    if (!outA || !outB || a < 0 || b < a || b > static_cast<LONG>(text.size())) return E_INVALIDARG;
    *outA = a; *outB = b; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetSelection(ULONG index, ULONG count, TS_SELECTION_ACP* value, ULONG* fetched) override {
    if (!value || !fetched) return E_POINTER; *fetched = 0;
    if ((index != 0 && index != TS_DEFAULT_SELECTION) || count < 1) return E_INVALIDARG;
    value->acpStart = start; value->acpEnd = end;
    value->style.ase = TS_AE_END; value->style.fInterimChar = FALSE; *fetched = 1; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SetSelection(ULONG count, const TS_SELECTION_ACP* value) override {
    if (count != 1 || !value || value->acpStart < 0 || value->acpEnd < value->acpStart ||
        value->acpEnd > static_cast<LONG>(text.size())) return E_INVALIDARG;
    start = value->acpStart; end = value->acpEnd; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetText(LONG a, LONG b, WCHAR* buffer, ULONG capacity,
      ULONG* copied, TS_RUNINFO* runs, ULONG runCapacity, ULONG* runCount, LONG* next) override {
    if (!copied || !runCount || !next) return E_POINTER;
    if (b == -1) b = static_cast<LONG>(text.size());
    if (a < 0 || b < a || b > static_cast<LONG>(text.size())) return TS_E_INVALIDPOS;
    ULONG length = std::min<ULONG>(b - a, capacity);
    if (buffer && length) std::copy_n(text.data() + a, length, buffer);
    *copied = length; *runCount = 0; *next = a + length;
    if (runs && runCapacity && length) { runs->uCount = length; runs->type = TS_RT_PLAIN; *runCount = 1; }
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SetText(DWORD, LONG a, LONG b, const WCHAR* buffer,
      ULONG count, TS_TEXTCHANGE* change) override {
    if (!change || (count && !buffer)) return E_POINTER;
    if (a < 0 || b < a || b > static_cast<LONG>(text.size())) return TS_E_INVALIDPOS;
    text.replace(a, b-a, buffer ? buffer : L"", count);
    change->acpStart = a; change->acpOldEnd = b; change->acpNewEnd = a + count;
    start = end = change->acpNewEnd; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetFormattedText(LONG, LONG, IDataObject**) override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE GetEmbedded(LONG, REFGUID, REFIID, IUnknown**) override { return TS_E_NOOBJECT; }
  HRESULT STDMETHODCALLTYPE QueryInsertEmbedded(const GUID*, const FORMATETC*, BOOL* allowed) override {
    if (!allowed) return E_POINTER; *allowed = FALSE; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE InsertEmbedded(DWORD, LONG, LONG, IDataObject*, TS_TEXTCHANGE*) override { return TS_E_NOOBJECT; }
  HRESULT STDMETHODCALLTYPE InsertTextAtSelection(DWORD flags, const WCHAR* buffer, ULONG count,
      LONG* outA, LONG* outB, TS_TEXTCHANGE* change) override {
    if (outA) *outA = start; if (outB) *outB = end;
    if (flags & TS_IAS_QUERYONLY) return S_OK;
    return SetText(0, start, end, buffer, count, change);
  }
  HRESULT STDMETHODCALLTYPE InsertEmbeddedAtSelection(DWORD, IDataObject*, LONG*, LONG*, TS_TEXTCHANGE*) override { return TS_E_NOOBJECT; }
  HRESULT STDMETHODCALLTYPE RequestSupportedAttrs(DWORD, ULONG, const TS_ATTRID*) override { return S_OK; }
  HRESULT STDMETHODCALLTYPE RequestAttrsAtPosition(LONG, ULONG, const TS_ATTRID*, DWORD) override { return S_OK; }
  HRESULT STDMETHODCALLTYPE RequestAttrsTransitioningAtPosition(LONG, ULONG, const TS_ATTRID*, DWORD) override { return S_OK; }
  HRESULT STDMETHODCALLTYPE FindNextAttrTransition(LONG, LONG halt, ULONG, const TS_ATTRID*, DWORD,
      LONG* next, BOOL* found, LONG* offset) override {
    if (!next || !found || !offset) return E_POINTER;
    *next = halt; *found = FALSE; *offset = 0; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE RetrieveRequestedAttrs(ULONG, TS_ATTRVAL*, ULONG* fetched) override {
    if (!fetched) return E_POINTER; *fetched = 0; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetEndACP(LONG* length) override {
    if (!length) return E_POINTER; *length = static_cast<LONG>(text.size()); return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetActiveView(TsViewCookie* view) override {
    if (!view) return E_POINTER; *view = 0; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetACPFromPoint(TsViewCookie, const POINT*, DWORD, LONG* value) override {
    if (!value) return E_POINTER; *value = start; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetTextExt(TsViewCookie, LONG a, LONG b, RECT* bounds, BOOL* clipped) override {
    if (!bounds || !clipped) return E_POINTER;
    POINT origin{24, 145}; ClientToScreen(window, &origin);
    *bounds = RECT{origin.x+a*9, origin.y, origin.x+std::max<LONG>(a+1,b)*9, origin.y+25};
    *clipped = FALSE; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetScreenExt(TsViewCookie, RECT* bounds) override {
    if (!bounds) return E_POINTER; GetClientRect(window, bounds);
    POINT a{bounds->left,bounds->top}, b{bounds->right,bounds->bottom};
    ClientToScreen(window,&a); ClientToScreen(window,&b); *bounds = RECT{a.x,a.y,b.x,b.y}; return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetWnd(TsViewCookie, HWND* output) override {
    if (!output) return E_POINTER; *output = window; return S_OK;
  }
};
