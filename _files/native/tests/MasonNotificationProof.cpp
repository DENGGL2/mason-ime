#define NOMINMAX
#include <windows.h>
#include <stdexcept>
#include <cstdio>
#include "../MasonServerNotifications.h"

namespace {
unsigned calls = 0;
DWORD lastMessage = 0, lastFlags = 0, lastInfoFlags = 0;
UINT lastId = 0, lastCallback = 0;
BOOL WINAPI Observe(DWORD message, PNOTIFYICONDATAW data) {
  ++calls; lastMessage = message; lastFlags = data ? data->uFlags : 0;
  lastInfoFlags = data ? data->dwInfoFlags : 0;
  lastId = data ? data->uID : 0; lastCallback = data ? data->uCallbackMessage : 0;
  return TRUE;
}
void Require(bool value, const char* error) { if (!value) throw std::runtime_error(error); }
}
int main() {
  try {
    MasonServerNotifications::originalNotify = &Observe;
    NOTIFYICONDATAW data{}; data.cbSize = sizeof(data); data.uFlags = NIF_INFO;
    data.uID = 7; data.uCallbackMessage = WM_APP + 12;
    wcscpy_s(data.szInfoTitle, L"小狼毫"); wcscpy_s(data.szInfo, L"部署完成");
    for (DWORD kind : {NIIF_NONE, NIIF_INFO, NIIF_USER}) {
      data.dwInfoFlags = kind;
      const unsigned before = calls;
      Require(MasonServerNotifications::Notify(NIM_MODIFY, &data), "Quiet notification failed");
      Require(calls == before, "Informational deployment balloon reached Shell");
      Require(data.uFlags == NIF_INFO && data.szInfo[0], "Caller notification data changed");
    }
    data.uFlags = NIF_INFO | NIF_MESSAGE | NIF_ICON | NIF_TIP;
    data.dwInfoFlags = NIIF_INFO;
    Require(MasonServerNotifications::Notify(NIM_MODIFY, &data), "Icon update failed");
    Require(lastFlags == (NIF_MESSAGE | NIF_ICON | NIF_TIP) && lastId == 7 && lastCallback == WM_APP + 12, "Menu/icon callbacks were lost");
    Require(MasonServerNotifications::Notify(NIM_ADD, &data) && lastMessage == NIM_ADD && !(lastFlags & NIF_INFO), "Icon registration changed");
    for (DWORD kind : {NIIF_WARNING, NIIF_ERROR}) {
      data.dwInfoFlags = kind;
      Require(MasonServerNotifications::Notify(NIM_MODIFY, &data) && (lastFlags & NIF_INFO) && lastInfoFlags == kind, "Native error/warning was suppressed");
    }
    data.uFlags = NIF_INFO; data.szInfo[0] = data.szInfoTitle[0] = 0;
    Require(MasonServerNotifications::Notify(NIM_MODIFY, &data) && lastFlags == NIF_INFO, "Balloon dismissal did not reach Shell");
    data.uFlags = NIF_ICON | NIF_TIP;
    Require(MasonServerNotifications::Notify(NIM_MODIFY, &data) && lastFlags == data.uFlags, "Ordinary tray update changed");
    Require(MasonServerNotifications::Notify(NIM_DELETE, &data) && lastMessage == NIM_DELETE, "Tray deletion changed");
    std::puts("passed: no informational balloons; menu, icon, dismissal and failure notifications preserved");
    return 0;
  } catch (const std::exception& error) { std::printf("failed: %s\n", error.what()); return 1; }
}
