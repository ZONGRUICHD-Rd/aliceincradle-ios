# AliceInCradle iOS 移植（Metal）

把 AliceInCradle Windows 版（ver030，Unity 2022.3.62f2 / URP / Mono）移植到 iOS 的构建流水线。
GitHub Actions 会下载游戏原始文件，重建 Unity 工程，以 **Metal + IL2CPP arm64** 生成 iOS 工程，再用 **Xcode 26（iOS 26 SDK）** 打出**未签名 IPA**。

> 本仓库**不包含任何游戏文件**，也不提交反编译代码或转换后的着色器。所有游戏内容都在 CI 运行时从你提供的下载链接获取并现场转换。

## 移植做了什么

| 问题 | Windows 版情况 | 本移植的做法 |
| --- | --- | --- |
| 图形 API | 所有自定义着色器（约 165 个）只编译了 D3D11 字节码 | `tools/shaderport`：自写 DXBC(SM4) → HLSL 反编译器，按原名称/属性/混合状态/关键字重建 ShaderLab，由 Unity 编译为 Metal。CI 里先用 glslang 做类型检查 |
| 音频 | CRIWARE ADX2（Windows 原生 `cri_ware_unity.dll`） | `managed/CriMw.CriWare.Runtime`：纯 C# 替代实现，同名同 API（游戏 89 处引用全部解析）。包含 ACB/AWB 解析、HCA v3 解码（移植自 vgmstream clHCA）、软件混音器（`OnAudioFilterRead`）、交互音乐分块切换、AISAC 分层、包络淡出、节拍回调、PCM 采集 |
| AssetBundle | 为 StandaloneWindows64 构建 | AssetRipper 导出 → 按原 manifest 的资源列表与原文件名重新构建 iOS AssetBundle |
| 文件对话框 | SFB（user32/Windows Forms） | `managed/SFB` 空实现（仅影响小游戏的导入/导出功能） |
| 计算着色器 | 部分效果使用 compute shader | 打开游戏自带的 `nocomputeshader` 开关，走游戏内置的像素着色器回退路径 |
| 输入 | 键盘 / 手柄 | 触屏虚拟手柄（`unity/Assets/IOSPort/Runtime/TouchGamepad.cs`），映射为 Input System 的虚拟 Gamepad，游戏原有手柄键位直接生效；连接实体手柄（MFi/Xbox/PS）时自动隐藏 |
| Unity 包 | 导出为预编译 DLL | `tools/PortTool` 把场景/资源中对 URP、Input System、UGUI 脚本的引用重映射到真正的 UPM 包源码 |

存档写在 `Application.persistentDataPath`（应用的 Documents 目录），与原版逻辑相同。

## 使用方法

### 1. 准备 Unity 许可证（必需）

Unity 编辑器在 CI 中运行需要许可证（个人版免费）。在仓库 **Settings → Secrets and variables → Actions** 中添加：

- `UNITY_LICENSE`：`Unity_lic.ulf` 文件的完整内容。
  在自己电脑上用 Unity Hub 登录并激活个人版许可证后，文件位于：
  - Windows：`C:\ProgramData\Unity\Unity_lic.ulf`
  - macOS：`/Library/Application Support/Unity/Unity_lic.ulf`
  - Linux：`~/.local/share/unity3d/Unity/Unity_lic.ulf`
- 或者（Pro/Plus 序列号）：`UNITY_SERIAL`、`UNITY_EMAIL`、`UNITY_PASSWORD`

### 2. 提供游戏下载链接（必需）

- `GAME_ARCHIVE_URL`：Windows 版压缩包（.7z/.zip）的下载地址。
  支持直链，也支持绿联 NAS 分享链接（`ug.link/.../share-download/?id=...`，CI 会用无头浏览器下载）。

> ⚠️ 你最初提供的分享链接将于 **2026-10-26 09:31 UTC** 过期，请延长有效期或换一个长期链接。

### 3. 运行

推送到 `main` 会自动构建，也可以在 **Actions → Build iOS (unsigned IPA) → Run workflow** 手动运行（可临时填写 `game_url`）。
成功后在运行页面下载 `AliceInCradle-unsigned-ipa` 产物，用 AltStore / SideStore / TrollStore / Sideloadly 等自行签名安装。

可选仓库变量（Variables）：`IOS_BUNDLE_ID`（默认 `com.aliceincradle.iosport`）、`IOS_VERSION`（默认 `0.30.9`）。

## 流水线

```
ubuntu-24.04 (job: unity)
  下载并解压游戏 → 编译替代 DLL 并校验引用 → 重建着色器(+glslang 校验)
  → AssetRipper 导出工程 → Unity 解析包 → 合并/重映射 → Unity(docker) 构建 iOS AssetBundle 与 Xcode 工程
macos-26 (job: ipa)
  xcodebuild archive (CODE_SIGNING_ALLOWED=NO) → Payload/*.app → .ipa
```

## 目录

- `.github/workflows/build-ios.yml` — CI
- `managed/CriMw.CriWare.Runtime` — CRIWARE 托管替代实现
- `managed/SFB` — 文件对话框替代实现
- `tools/shaderport` — DXBC → HLSL/ShaderLab 转换器
- `tools/PortTool` — 脚本引用重映射 / 引用校验
- `tools/ci` — 下载、AssetRipper、合并、Unity docker 脚本
- `unity/` — iOS Unity 工程骨架（编辑器构建脚本、触屏控制、启动设置）

## 版权说明

AliceInCradle 版权归 NanameHacha（ななめはっちゃ）所有。本仓库只包含移植工具代码，仅供已合法获得游戏的个人在自己的设备上使用。
本仓库目前是**公开**的：公开仓库的 Actions 产物（包含游戏内容的 IPA）可被任何已登录的 GitHub 用户下载。为避免再分发游戏内容，建议把仓库设为私有（注意私有仓库的 Actions 存储配额较小）。

第三方代码见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
