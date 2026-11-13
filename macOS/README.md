# Translated for macOS

Translated 是一款原生 macOS 划词翻译工具。选中文字后按 **Control + Option + T**，或点击选区旁的 **Translate**：可编辑文字会在核对选区后原位替换，只读文字的译文会复制到剪贴板。快捷键可在 Settings 中修改。

## 下载和首次使用

1. 从 [Releases](https://github.com/xiaoxianjj/translated/releases) 下载 macOS 压缩包，解压后将 `Translated.app` 移到“应用程序”。支持 macOS 15 及更新版本，包含 Apple silicon 和 Intel 架构。
2. 首次打开时，macOS 可能阻止启动：可在 Finder 中按住 Control 点击应用并选择“打开”，或在“系统设置 → 隐私与安全性”中选择“仍要打开”。此构建未经过 Apple 公证。
3. 在应用中点击 **Open Settings**，在“系统设置 → 隐私与安全性 → 辅助功能”中允许 Translated。关闭窗口后应用仍留在菜单栏。
4. 默认使用 Apple 翻译。首次使用某些语言时，按系统提示下载语言资源；也可在 Settings 中选择 DeepSeek、OpenAI 或兼容服务并填写自己的 API key。

英文译为所选的简体或繁体中文，其他受支持语言译为英文。翻译期间如果选区或焦点发生变化，应用会保留结果供手动复制。不同应用提供的辅助功能接口不同，部分编辑器可能只能复制译文，无法原位替换。

## 隐私与权限

辅助功能权限用于读取选区和在可编辑位置写入译文。兼容部分应用时，程序会临时使用剪贴板；剪贴板管理工具可能观察到这些内容。

Apple 模式使用设备上的翻译功能。选择 API 服务时，选中文字和长文分块所需的相邻上下文会发送到所选服务；其数据处理规则由服务提供商决定。API key 保存在本机钥匙串中，不包含在应用或源码内。

## 构建与测试

需要 Xcode 26.4 或更新版本及 macOS 26.4 或更新版 SDK。无需第三方 Swift 包。

```sh
cd macOS
swift test
bash scripts/build-app.sh
open dist/Translated.app
```

构建脚本生成包含 Apple silicon 和 Intel 架构的应用，默认使用临时签名。可通过 `CODE_SIGN_IDENTITY` 显式指定签名身份；向其他 Mac 分发时，Apple Developer ID 签名和公证需要另行配置。

自动测试覆盖语言选择、选区投递、快捷键及 API 请求等核心逻辑。实际辅助功能行为和 Apple 语言资源仍需在目标 Mac 上检查。
