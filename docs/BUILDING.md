# 构建 Mason

## 构建环境

- Windows 10 / 11。
- .NET Framework 4.8，以及可用的 .NET Framework C# 编译器 `csc.exe`。
- Windows PowerShell 5.1。
- Python 3.10+，仅使用标准库。
- 如需重新编译原生菜单桥接：Visual Studio 2022 Build Tools 的 C++ 工具链和 Windows 10 SDK。

源码仓库带有公共词库、上游 0.17.4 引擎安装器、云输入二进制及 x86 / x64 菜单桥接。Mason 的托管程序从源码构建，安装包不包含开发源码、编译脚本或独立托盘入口。

## 构建运行安装包

在仓库根目录执行：

```powershell
python -B .\_files\scripts\build_release.py --output-dir .\release\Mason
```

该命令依次编译 WPF 设置程序、图形安装器和运行库，再按清单生成运行目录 `release/Mason`。输出目录必须不存在；如果已经构建过，选择一个新的目录名，保留旧结果。

生成的 `release/Mason/Mason.Install.exe` 是安装入口。发布时将整个 `Mason` 文件夹压缩，不能仅分发这个 EXE。包内 `_files/install-manifest.json` 记录每个运行文件的大小和 SHA-256。

如果托管程序已经编译好，可以传入 `--no-build` 仅生成运行目录。

## 重新编译原生菜单桥接

在已经具备 C++ 编译环境的机器上，先执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\_files\scripts\build_native_menu.ps1 -PackageDir . -BuildDir .\build\native
```

然后重新生成运行包。菜单桥接源码位于 `_files/native`，现成 x86 / x64 DLL 与源码一起提供。

## 源码结构

| 路径 | 内容 |
| --- | --- |
| `_files/ui` | WPF 设置与安装界面、运行库、程序入口、图标、项目版本和 GitHub 地址 |
| `_files/scripts` | 配置后端、模糊音、词库更新、运行时脚本和构建脚本 |
| `_files/native` | Windows 原生菜单桥接与 x86 / x64 DLL |
| `_files/rime` | 白霜拼音方案、公共词库、模型、转换和 Lua 扩展 |
| `_files/cloud` | 云候选脚本与依赖 |
| `docs/screenshots` | 当前设置界面截图 |

`project-info.json` 的 `github_url` 指向本仓库。“检查更新”请求 `https://api.github.com/repos/DENGGL2/mason-ime/releases/latest`，比较正式 Release 的 `v主版本.次版本.修订版本` 标签。

未发布到 Release 的源码修改不会被版本检查识别。发布新版时，应同时更新项目版本、程序版本信息和运行清单，并发布相应正式标签及安装包。

含中文的 PowerShell 脚本使用带 BOM 的 UTF-8 编码，以兼容 Windows PowerShell 5.1。上游组件的原始许可与来源说明保留于相应目录及 `THIRD_PARTY_NOTICES.md`。
