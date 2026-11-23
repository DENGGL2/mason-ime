#pragma once
#include <shellapi.h>
#include <cstddef>
#include <cstring>
#include "MasonUpdateIndicators.h"

// Numeric counters only, for validating the native save/deploy notification path.
struct MasonNotificationDiagnosticData {
  DWORD size;
  volatile LONG installed, suppressed, forwarded, lastFlags;
};
extern "C" { MasonNotificationDiagnosticData MasonNotificationDiagnostics = {sizeof(MasonNotificationDiagnosticData)}; }

namespace MasonServerNotifications {
using NotifyFunction = BOOL(WINAPI*)(DWORD, PNOTIFYICONDATAW);
inline NotifyFunction originalNotify = nullptr;

inline BOOL WINAPI Notify(DWORD message, PNOTIFYICONDATAW data) noexcept {
  if (!originalNotify) return FALSE;
  const bool supported = data && data->cbSize >= offsetof(NOTIFYICONDATAW, dwInfoFlags) + sizeof(DWORD)
      && data->cbSize <= sizeof(NOTIFYICONDATAW);
  if ((message == NIM_ADD || message == NIM_MODIFY) && supported && (data->uFlags & NIF_INFO)
      && (data->szInfo[0] || data->szInfoTitle[0])) {
    const DWORD kind = data->dwInfoFlags & NIIF_ICON_MASK;
    // The settings and installer already report successful jobs. Omit the
    // legacy progress/success balloon; native warnings and errors still appear.
    if (kind == NIIF_NONE || kind == NIIF_INFO || kind == NIIF_USER) {
      InterlockedIncrement(&MasonNotificationDiagnostics.suppressed);
      InterlockedExchange(&MasonNotificationDiagnostics.lastFlags, data->dwInfoFlags);
      NOTIFYICONDATAW quiet{};
      std::memcpy(&quiet, data, data->cbSize);
      quiet.uFlags &= ~NIF_INFO;
      if (message == NIM_MODIFY && !quiet.uFlags) return TRUE;
      // Preserve icon, callback, tooltip and GUID updates carried by the call.
      InterlockedIncrement(&MasonNotificationDiagnostics.forwarded);
      return MasonUpdateIndicators::Forward(message, &quiet, originalNotify);
    }
  }
  InterlockedIncrement(&MasonNotificationDiagnostics.forwarded);
  return MasonUpdateIndicators::Forward(message, data, originalNotify);
}

inline void Install() noexcept {
  const HMODULE module = GetModuleHandleW(nullptr);
  wchar_t path[MAX_PATH]{};
  if (!GetModuleFileNameW(module, path, MAX_PATH)) return;
  const wchar_t* name = wcsrchr(path, L'\\'); name = name ? name + 1 : path;
  // Only Mason's native server imports are changed. Host apps and their
  // notifications retain their original Shell API calls.
  if (_wcsicmp(name, L"WeaselServer.exe") != 0) return;
  const HMODULE shell = GetModuleHandleW(L"shell32.dll");
  if (!shell) return;
  originalNotify = reinterpret_cast<NotifyFunction>(GetProcAddress(shell, "Shell_NotifyIconW"));
  if (!originalNotify) return;
  auto* base = reinterpret_cast<BYTE*>(module);
  auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
  if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
  auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
  if (nt->Signature != IMAGE_NT_SIGNATURE) return;
  const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
  if (!directory.VirtualAddress) return;
  auto* imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
  for (size_t index = 0; index < directory.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR) && imports[index].Name; ++index) {
    if (!imports[index].FirstThunk) continue;
    auto* table = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imports[index].FirstThunk);
    for (size_t slot = 0; slot < 4096 && table[slot].u1.Function; ++slot) {
      if (reinterpret_cast<NotifyFunction>(table[slot].u1.Function) != originalNotify) continue;
      DWORD old = 0;
      if (!VirtualProtect(table + slot, sizeof(table[slot]), PAGE_READWRITE, &old)) return;
      InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(&table[slot].u1.Function), reinterpret_cast<void*>(&Notify));
      DWORD ignored = 0; VirtualProtect(table + slot, sizeof(table[slot]), old, &ignored);
      InterlockedExchange(&MasonNotificationDiagnostics.installed, 1);
      return;
    }
  }
}
}
