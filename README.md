# Translated

Translated 是 Windows 10 和 Windows 11 上的划词翻译工具。选中文字后按 **Ctrl + Shift + F8**，或点击选区旁的 **Translate**。英文译为简体或繁体中文，其他语言译为英文。快捷键、目标中文和选区按钮都可以在 Settings 中调整。

在可编辑的输入框中，程序会核对选区后原位替换文字。在只读区域中，译文会复制到剪贴板。如果焦点或选区在翻译过程中发生变化，可以从系统托盘的 **Translation result** 取回译文。

## 配置翻译服务

运行 `Translated.exe`，在 Settings 中选择 DeepSeek、OpenAI 或兼容 Chat Completions 的自定义服务，填写模型和自己的凭据。也支持本机回环地址上的服务。发送给所选服务的是选中文字，长文分块时会附上相邻上下文。

API key 存在 Windows 凭据管理器中，并按服务和接口地址区分。个人设置与最近一次选区核对失败的诊断信息保存在当前用户的 `%LOCALAPPDATA%\Translated`。诊断文件只包含版本、时间、步骤、状态和耗时。详见 [Windows 使用说明](Windows/使用说明.txt)。

## 构建与测试

应用使用 WPF、WinForms 和 .NET Framework 4.8。在安装了 .NET 8 或更新 SDK 的 Windows 上，从仓库根目录运行构建脚本。

```powershell
.\build-local.cmd
```

这个命令构建 Release 版本并运行核心及本地回环测试。可执行文件位于 `Windows\bin\Release\net48\Translated.exe`。在可交互的 Windows 桌面上运行 `.\build-local.cmd --desktop`，可以额外测试快捷键、选区和剪贴板。

安装 Python 3 后，可以用打包脚本生成便携包。

```powershell
py -3 scripts/build-windows.py
```

输出为 `dist/Translated-Windows.zip`。界面字体 Nunito 使用 [SIL Open Font License](Windows/Assets/Nunito-OFL.txt)。
