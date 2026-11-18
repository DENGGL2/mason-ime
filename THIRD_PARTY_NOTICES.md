# 麻绳输入法 Mason · 开源与第三方说明

Mason 采用 [GNU General Public License v3.0](LICENSE)（GPL-3.0）。
项目地址：<https://github.com/DENGGL2/mason-ime>。

Mason 在 Rime / 小狼毫引擎和白霜拼音方案之上定制 Windows 界面、安装流程、菜单与配置。Mason 新增的 WPF 界面、安装器、原生菜单桥接、Python 设置后端和 Lua 扩展源码包含在本仓库中。

| 开源项目 | 在 Mason 中的用途 | 来源 |
| --- | --- | --- |
| Rime / 小狼毫（Weasel） | Windows 输入法与系统集成；包含 0.17.4 引擎安装器，安装时定制品牌、菜单与候选窗 | [rime/weasel](https://github.com/rime/weasel)，对应版本 0.17.4 |
| librime | 拼音解析、候选生成、个人词条学习等核心引擎 | [rime/librime](https://github.com/rime/librime) |
| librime-lua | 引擎中的 Lua 扩展支持 | [hchunhui/librime-lua](https://github.com/hchunhui/librime-lua) |
| 白霜拼音（rime-frost） | 默认拼音方案、词库与部分 Lua 功能 | [gaboolic/rime-frost](https://github.com/gaboolic/rime-frost)；许可保留于 [_files/rime/LICENSE](_files/rime/LICENSE) |
| 雾凇拼音（rime-ice） | 白霜拼音的上游方案与词库基础 | [iDvel/rime-ice](https://github.com/iDvel/rime-ice) |
| librime-cloud | 云候选及 HTTP/Lua 扩展 | [hchunhui/librime-cloud](https://github.com/hchunhui/librime-cloud) |
| OpenCC | 简繁、地域字形及其他转换 | [BYVoid/OpenCC](https://github.com/BYVoid/OpenCC) |
| WinSparkle | 上游 Windows 引擎附带的更新组件；Mason 设置中的版本检查使用 GitHub Releases API | [vslavik/winsparkle](https://github.com/vslavik/winsparkle) |
| FlClash | 设置和安装界面的布局、控件及动效参考，参考版本 0.9.3；Mason 前端使用 WPF 编写 | [chen08209/FlClash](https://github.com/chen08209/FlClash) |

第三方组件继续适用各自的开源协议与版权说明，本项目的 GPL-3.0 不替代这些协议。上游 Rime / 小狼毫与白霜拼音的许可及原始版权保留。引擎自带的 7-Zip、curl、libiconv 等工具和依赖也保留随引擎分发的许可文件。

导航图标来自项目所有者提供的 SVG 路径；M 标识为本项目定制。公共词库、模型与原有 Lua 脚本的作者说明保留在相应文件及上游文档中。
