# AliceInCradle iOS 移植（Metal）

把 AliceInCradle Windows 版（ver030，Unity 2022.3.62f2 / URP / Mono）移植到 iOS 的构建流水线。
GitHub Actions 会下载游戏原始文件，重建 Unity 工程，以 **Metal + IL2CPP arm64** 生成 iOS 工程，再用 **Xcode 26（iOS 26 SDK）** 打出**未签名 IPA**。

> 游戏原始压缩包（ver030i，588 MB）分片存放在本仓库的 **`game-archive` 分支**（`archive/*.7z.part00..05`，附 SHA256 校验），CI 用 curl 逐个分片下载（断点续传、自动重试、SHA256 校验），拼好的压缩包缓存在你电脑的 `<盘符>\aicw-cache`，以后的构建不再重复下载。`main` 分支不提交反编译代码或转换后的着色器，这些都在 CI 中现场生成。

## 移植做了什么

| 问题 | Windows 版情况 | 本移植的做法 |
| --- | --- | --- |
| 图形 API | 所有自定义着色器（约 165 个）只编译了 D3D11 字节码 | `tools/shaderport`：自写 DXBC(SM4) → HLSL 反编译器，按原名称/属性/混合状态/关键字重建 ShaderLab，由 Unity 编译为 Metal。CI 里先用 glslang 做类型检查 |
| 音频 | CRIWARE ADX2（Windows 原生 `cri_ware_unity.dll`） | `managed/CriMw.CriWare.Runtime`：纯 C# 替代实现，同名同 API（游戏 89 处引用全部解析）。包含 ACB/AWB 解析、HCA v3 解码（移植自 vgmstream clHCA）、软件混音器（`OnAudioFilterRead`）、交互音乐分块切换、AISAC 分层、包络淡出、节拍回调、PCM 采集 |
| AssetBundle | 为 StandaloneWindows64 构建 | AssetRipper 导出 → 按原 manifest 的资源列表与原文件名重新构建 iOS AssetBundle |
| 文件对话框 | SFB（user32/Windows Forms） | `managed/SFB` 空实现（仅影响小游戏的导入/导出功能） |
| 计算着色器 | 部分效果使用 compute shader | 打开游戏自带的 `nocomputeshader` 开关，走游戏内置的像素着色器回退路径 |
| 输入 | 键盘 / 手柄 | 触屏虚拟手柄（`unity/Assets/IOSPort/Runtime/TouchGamepad.cs`），映射为 Input System 的虚拟 Gamepad，游戏原有手柄键位直接生效；连接实体手柄（MFi/Xbox/PS）时自动隐藏 |
| 启动环境检查 | `XX.IN.Awake` 会计算 Windows 版 exe 的 SHA1、核对 Unity 版本，并拦截安卓模拟器；iOS 上没有 exe，游戏停在启动阶段（灰屏） | `PortTool skip-environment-check` 跳过这一段检查，其余启动流程不变 |
| 文件名大小写 | Windows 不区分大小写（还有反斜杠路径），iOS 区分 | `managed/IOSPort.Compat` 提供按真实文件名解析路径的 `GameIO`；`PortTool redirect-io` 把游戏里的 `File.Exists`、`FileStream`、`AssetBundle.LoadFromFile` 等调用改为调用它 |
| 程序集名 | 游戏主程序集名为 `Assembly-CSharp`，与 Unity 保留名冲突，编辑器无法解析其中的脚本，场景数据被写成空组件，真机启动即崩溃 | `PortTool rename-assembly` 改名为 `AliceInCradle.Game`（保留 GUID，引用不变）；构建前检查场景中是否有丢失的脚本 |
| IL2CPP | 原版是 Mono，`Better.UnsafeGeneric.dll` 用手写 IL 直接比较泛型枚举值，IL2CPP 无法编译 | `PortTool fix-generic-comparers` 把这些方法改写为调用 `EqualityComparer<T>.Default`，结果相同 |
| Unity 包 | 导出为预编译 DLL | `tools/PortTool` 把场景/资源中对 URP、Input System、UGUI 脚本的引用重映射到真正的 UPM 包源码 |

存档写在 `Application.persistentDataPath`（应用的 Documents 目录），与原版逻辑相同。

## 使用方法

Unity 部分在**你自己的 Windows 电脑**上运行（自托管 runner），因为 Unity 个人版许可证绑定本机；Xcode 部分在 GitHub 的 macOS 服务器上运行。

### 1. 电脑准备（已完成）
- Unity Hub 登录并激活个人版许可证
- 安装 Unity **2022.3.62f2** + **iOS Build Support**
- 在仓库 Settings → Actions → Runners 添加 Windows 自托管 runner，并保持运行
  - runner 必须以**登录 Unity Hub 的那个 Windows 用户**运行（直接运行 `run.cmd` 即可；若装成服务，需把服务账户改成你的用户），否则 Unity 找不到许可证
  - 构建期间电脑需开机联网；磁盘需要约 25 GB 空闲空间（工作目录为 runner 所在盘的 `\aicw`）

### 2. 运行
推送到 `main` 会自动构建；也可在 **Actions → Build iOS (unsigned IPA) → Run workflow** 手动运行。
成功后在运行页面下载 `AliceInCradle-unsigned-ipa`，用 AltStore / SideStore / TrollStore / Sideloadly 等自行签名安装。

可选仓库变量（Settings → Variables）：
- `UNITY_EXE`：若自动找不到 Unity，填 `Unity.exe` 的完整路径
- `GAME_ARCHIVE_PATH`：电脑上（或 NAS 映射盘上）`AliceInCradle_Win_ver030i.7z` 的完整路径，例如 `Z:\AliceInCradle_Win_ver030i.7z`；设置后直接使用该文件，完全不从 GitHub 下载
- `IOS_BUNDLE_ID`（默认 `com.aliceincradle.iosport`）、`IOS_VERSION`（默认 `0.30.9`）

## 流水线

```
你的 Windows 电脑 (job: unity, self-hosted)
  下载 game-archive 分支的分片（或读取 GAME_ARCHIVE_PATH）并校验解压 → 编译替代 DLL 并校验引用 → 重建着色器
  → AssetRipper 导出工程 → Unity 解析包 → 合并/重映射 → Unity 构建 iOS AssetBundle 与 Xcode 工程
GitHub macos-26 (job: ipa)
  xcodebuild archive (CODE_SIGNING_ALLOWED=NO) → Payload/*.app → .ipa
```

## 目录

- `.github/workflows/build-ios.yml` — CI
- `managed/CriMw.CriWare.Runtime` — CRIWARE 托管替代实现
- `managed/SFB` — 文件对话框替代实现
- `managed/IOSPort.Compat` — 游戏文件访问的大小写兼容层
- `tools/shaderport` — DXBC → HLSL/ShaderLab 转换器
- `tools/PortTool` — 脚本引用重映射、引用校验、程序集补丁（改名、跳过环境检查、文件访问重定向、泛型比较器）
- `tools/ci` — 压缩包重组、AssetRipper、项目合并、Unity 定位/运行脚本（PowerShell / Python）
- `unity/` — iOS Unity 工程骨架（编辑器构建脚本、触屏控制、启动设置）

## 版权说明

AliceInCradle 版权归 NanameHacha（ななめはっちゃ）所有。本仓库只包含移植工具代码，仅供已合法获得游戏的个人在自己的设备上使用。
本仓库目前是**公开**的，`game-archive` 分支中的游戏压缩包以及 Actions 产物（IPA）都可被他人下载，相当于公开再分发游戏。建议把仓库设为私有（注意私有仓库的 Actions 存储配额与分钟数有限）。

第三方代码见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
