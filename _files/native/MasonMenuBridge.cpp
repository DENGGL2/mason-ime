#pragma comment(linker, "/export:win_sparkle_check_update_with_ui_and_install=WinSparkle.win_sparkle_check_update_with_ui_and_install,@2")
#pragma comment(linker, "/export:win_sparkle_check_update_without_ui=WinSparkle.win_sparkle_check_update_without_ui,@3")
#pragma comment(linker, "/export:win_sparkle_cleanup=WinSparkle.win_sparkle_cleanup,@4")
#pragma comment(linker, "/export:win_sparkle_clear_http_headers=WinSparkle.win_sparkle_clear_http_headers,@5")
#pragma comment(linker, "/export:win_sparkle_get_automatic_check_for_updates=WinSparkle.win_sparkle_get_automatic_check_for_updates,@6")
#pragma comment(linker, "/export:win_sparkle_get_last_check_time=WinSparkle.win_sparkle_get_last_check_time,@7")
#pragma comment(linker, "/export:win_sparkle_get_update_check_interval=WinSparkle.win_sparkle_get_update_check_interval,@8")
#pragma comment(linker, "/export:win_sparkle_init=WinSparkle.win_sparkle_init,@9")
#pragma comment(linker, "/export:win_sparkle_set_app_build_version=WinSparkle.win_sparkle_set_app_build_version,@10")
#pragma comment(linker, "/export:win_sparkle_set_app_details=WinSparkle.win_sparkle_set_app_details,@11")
#pragma comment(linker, "/export:win_sparkle_set_appcast_url=WinSparkle.win_sparkle_set_appcast_url,@12")
#pragma comment(linker, "/export:win_sparkle_set_automatic_check_for_updates=WinSparkle.win_sparkle_set_automatic_check_for_updates,@13")
#pragma comment(linker, "/export:win_sparkle_set_can_shutdown_callback=WinSparkle.win_sparkle_set_can_shutdown_callback,@14")
#pragma comment(linker, "/export:win_sparkle_set_config_methods=WinSparkle.win_sparkle_set_config_methods,@15")
#pragma comment(linker, "/export:win_sparkle_set_did_find_update_callback=WinSparkle.win_sparkle_set_did_find_update_callback,@16")
#pragma comment(linker, "/export:win_sparkle_set_did_not_find_update_callback=WinSparkle.win_sparkle_set_did_not_find_update_callback,@17")
#pragma comment(linker, "/export:win_sparkle_set_dsa_pub_pem=WinSparkle.win_sparkle_set_dsa_pub_pem,@18")
#pragma comment(linker, "/export:win_sparkle_set_error_callback=WinSparkle.win_sparkle_set_error_callback,@19")
#pragma comment(linker, "/export:win_sparkle_set_http_header=WinSparkle.win_sparkle_set_http_header,@20")
#pragma comment(linker, "/export:win_sparkle_set_lang=WinSparkle.win_sparkle_set_lang,@21")
#pragma comment(linker, "/export:win_sparkle_set_langid=WinSparkle.win_sparkle_set_langid,@22")
#pragma comment(linker, "/export:win_sparkle_set_registry_path=WinSparkle.win_sparkle_set_registry_path,@23")
#pragma comment(linker, "/export:win_sparkle_set_shutdown_request_callback=WinSparkle.win_sparkle_set_shutdown_request_callback,@24")
#pragma comment(linker, "/export:win_sparkle_set_update_cancelled_callback=WinSparkle.win_sparkle_set_update_cancelled_callback,@25")
#pragma comment(linker, "/export:win_sparkle_set_update_check_interval=WinSparkle.win_sparkle_set_update_check_interval,@26")
#pragma comment(linker, "/export:win_sparkle_set_update_dismissed_callback=WinSparkle.win_sparkle_set_update_dismissed_callback,@27")
#pragma comment(linker, "/export:win_sparkle_set_update_postponed_callback=WinSparkle.win_sparkle_set_update_postponed_callback,@28")
#pragma comment(linker, "/export:win_sparkle_set_update_skipped_callback=WinSparkle.win_sparkle_set_update_skipped_callback,@29")
#pragma comment(linker, "/export:win_sparkle_set_user_run_installer_callback=WinSparkle.win_sparkle_set_user_run_installer_callback,@30")
#include <windows.h>
#include <string>

// Only the explicit menu update action is handled here. All other WinSparkle
// exports are forwarded to the unchanged original library by the .def file.
static HMODULE module;
extern "C" void __cdecl win_sparkle_check_update_with_ui() {
  wchar_t buffer[32768]={};DWORD length=GetModuleFileNameW(module,buffer,32768);
  if(length==0 || length>=32768)return;
  std::wstring folder(buffer,length);size_t slash=folder.find_last_of(L"\\/");
  if(slash==std::wstring::npos)return;folder.resize(slash);
  std::wstring entry=folder+L"\\Mason.Deployer.exe",command=L"\""+entry+L"\" /updates";
  STARTUPINFOW startup={sizeof(startup)};PROCESS_INFORMATION child={};startup.dwFlags=STARTF_USESHOWWINDOW;startup.wShowWindow=SW_HIDE;
  if(CreateProcessW(entry.c_str(),&command[0],nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,folder.c_str(),&startup,&child)){
    CloseHandle(child.hThread);CloseHandle(child.hProcess);
  }else MessageBoxW(nullptr,L"暂时无法打开检查更新，请重新打开麻绳设置。",L"麻绳输入法",MB_OK|MB_ICONINFORMATION);
}
BOOL WINAPI DllMain(HMODULE instance,DWORD reason,LPVOID) {
  if(reason==DLL_PROCESS_ATTACH)module=instance;return TRUE;
}
