# Translated

Translated 提供 Windows 和 macOS 两个独立版本。选择适合自己系统的版本：

- **macOS 15 或更新版本**：[下载安装包](https://github.com/xiaoxianjj/translated/releases) · [macOS 使用与构建说明](macOS/README.md)。
- **Windows 10/11**：参见下方说明和 [Windows 使用说明](Windows/使用说明.txt)。

## Windows

Translated 是 Windows 10 和 Windows 11 上的划词翻译工具。选中文字后按 **Ctrl + Shift + F8**，或点击选区旁的 **Translate**，默认在选区旁弹出译文卡片，直接查看、选取和复制译文。卡片及主界面可选择 21 种目标语言，切换后使用同一份原文重新翻译，并记住选择；也保留自动中英互译。

卡片模式保留原文，不自动覆盖剪贴板。在 Settings 关闭“划词后弹出译文卡片”，可恢复自动交付：可编辑输入框核对选区后原位替换，只读区域复制完整译文；不适合自动交付时从系统托盘的 **Translation result** 取回结果。[译文卡片与多语言说明](docs/译文卡片与多语言.md)。

Windows 的 Settings 现在提供 **自定义快捷键…**：可通过实体按键录制，或点击拟物键盘与鼠标中键、M4/M5 侧键组合，预览点亮后保存。支持键盘多键组合及键鼠混合组合，并兼容旧快捷键配置。[查看使用与验证说明](docs/自定义快捷键.md)。

## 配置翻译服务

运行 `Translated.exe`，在 Settings 中选择 DeepSeek、OpenAI 或兼容 Chat Completions 的自定义服务，填写模型和自己的凭据。也支持本机回环地址上的服务。发送给所选服务的是选中文字，长文分块时会附上相邻上下文。

API key 存在 Windows 凭据管理器中，并按服务和接口地址区分。个人设置与最近一次选区核对失败的诊断信息保存在当前用户的 `%LOCALAPPDATA%\Translated`。诊断文件只包含版本、时间、步骤、状态和耗时。详见 [Windows 使用说明](Windows/使用说明.txt)。

## 构建与测试

应用使用 WPF、WinForms 和 .NET Framework 4.8。在安装了 .NET 8 或更新 SDK 的 Windows 上，从仓库根目录运行构建脚本。

```powershell
.\build-local.cmd
```

这个命令构建 Release 版本并运行核心、本地回环及无可见窗口的生命周期测试，覆盖重复退出、关窗取消、配置快照和小工作区布局。可执行文件位于 `Windows\bin\Release\net48\Translated.exe`。在可交互的 Windows 桌面上运行 `.\build-local.cmd --desktop`，可以额外测试快捷键、选区和剪贴板。

安装 Python 3 后，可以用打包脚本生成便携包。

```powershell
py -3 scripts/build-windows.py
```

输出为 `dist/Translated-Windows.zip`。界面字体 Nunito 使用 [SIL Open Font License](Windows/Assets/Nunito-OFL.txt)。
