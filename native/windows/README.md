# Windows 迁移（平台宿主、React 工作区与共享服务）

这是迁移中的开发原型：C# 调用共享 Rust 动态库，WinUI 3 / WebView2 验证学校登录、明确选择学期、读取课程、录播目录和一节课堂材料。它不是正式版，不提供“开始学习”首页，不读取旧版本地数据库，不保存学校会话。正式工作区支持本地业务数据，连接验证窗不落库。

2026-11-01 起按用户要求延后 Windows 编译，先推进 M1 共享服务。新增 `NativeWorkspace` 和 `JobService` 可在 macOS/Linux 上使用临时合成数据库验证；正式工作区已接入离线读取、消息桥和任务状态；连接原型仍独立，不会因运行验证窗口而写入真实学习记录。

D01 已确认 Windows 继续使用 React Base UI；后续业务工作区由 WinUI 承载独立本地 WebView2，复用现有共享组件。当前默认打开 React 业务工作区，`--probe` 打开独立连接验证窗。正式业务页的学校登录/录播适配源码已接线，实际 Windows 页面与 SSO 链路仍未验证。学校远程页面不允许接入本地业务桥。

## 工程

- `BUAALearn.CoreInterop`：C 调用约定、UTF-8、ABI 版本检查、SafeHandle 和受 GC 保护的传输回调。Rust 输出只用 Rust 释放；托管回调输出只用 CoTaskMem 配套释放。
- `BUAALearn.Platform`：限域、凭据隔离、禁止重定向和限大小的学校 HTTP 传输；登录跳转、录播 Cookie 和签名入口规则。
- `NativeWorkspace`：系统数据目录、工作区独占租约、带版本/应用标识的 Rust 数据库、升级前一致性备份、本地账号注册与恢复；`AccountStore` 固定账号，并可绑定任务取消令牌。
- `JobService`：串行执行、持久化/进度、按账号/类型/目标去重、重试关联、全账号中断恢复，以及有界账号切换/停止；重启始终离线、不自动重放。
- `BUAALearn.App`：最小 WinUI 验证窗；只有学校登录页运行 WebView2，没有本地网页服务、HostObject 或消息桥。
- `BUAALearn.Tests`：无测试框架依赖的合成数据检查程序，验证真实动态库互操作、账号隔离、关闭句柄、GC、异常、网络策略及登录协议。**使用 `dotnet run`，不是 `dotnet test`**；任何失败返回非零。

工具版本锁定在 `global.json`、项目 PackageReference 及 CI 中。当前为 .NET SDK 10.0.403、Windows App SDK 2.6.4、Windows SDK BuildTools 10.0.28000.2957、Rust 1.99.1；这是本原型选择的版本，不代表最低支持版本。Windows/WinUI 编译仍需 Windows 验证，不应从 macOS 上的 C# 检查通过推定。

## Windows 构建

开发机需要 Windows 11 x64、PowerShell 7、Node.js 22（只用于编译静态资源）、以上 .NET SDK、Rust MSVC 工具链及 Visual Studio C++ 构建工具（含 MSVC 和 Windows SDK）。普通最终用户无需这些工具。

```powershell
cd native/windows
rustup toolchain install 1.99.1 --profile minimal
rustup override set 1.99.1
rustup target add x86_64-pc-windows-msvc
pwsh ./build.ps1 -Publish
```

脚本执行 Rust 测试、DLL 构建、C# 检查、WinUI 编译并发布到 `dist/native/windows/probe/`，同时生成 ZIP 和实际压缩包字节数。当前 ZIP 为**可解压测试包，不是 EXE 安装器**。`.cargo/config.toml` 只在从本目录调用 Cargo 时应用，用于静态链接 Rust DLL 的 MSVC CRT。

```powershell
../../dist/native/windows/probe/BUAALearn.Probe.exe --probe
pwsh ./smoke.ps1 -Executable ../../dist/native/windows/probe/BUAALearn.Probe.exe
```

启动需要 WebView2 Evergreen Runtime。原型暂未自动安装它；在干净系统手工验证运行时缺失、安装和初始化。自包含 .NET / App SDK 不代表 WebView2 已捆绑。

`smoke.ps1` 只验证原生窗口、ABI 与 InPrivate WebView2 初始化，不连接学校；报告始终标记 `school_verified=false`。CI 默认在相关源文件推送或 PR 时只执行 macOS 上的共享层检查；Windows 编译与测试包上传改为手动 dispatch 并启用 `verify_windows`。尚未推送的本地改动不会触发 CI。

## 手动学校验证

1. 打开程序，等待“内核和登录浏览器已就绪”。
2. 点击“连接学校”，在学校页面自行完成学生账号认证。
3. 选择学校返回的一个学期，再选择有回放的课程。
4. 等待实际目录返回后显示“录播目录已验证”，再选择一节回放。
5. 确认读到非空文本或截图数量。空材料只表示接口成功但材料未就绪，不算获取材料验收通过。
6. 验证“停止并退出登录”、重新连接、窗口关闭、临时资料目录清理及学校登录过期。记录允许列表缺少的脱敏主机名，不记录查询串或完整授权地址。

登录使用当前用户 LocalAppData 的 `BUAA Learn Native/probe-sessions/<随机编号>` 独立临时目录和 InPrivate profile；进程退出后清理，未完成清理的目录下次启动重试。`session.lock` 防止另一个原型清理正在使用的资料目录。原型不宣称浏览器完全不触盘。

没有打印登录数据、Cookie、完整授权地址或 HTTP 响应的诊断功能。失败提示不能代替测试证据；学校登录必须在实际 Windows 网络环境人工验收。

## macOS / Linux 的共享层检查

这些检查不运行 WinUI，也不能替代 Windows ABI 和学校登录测试：

```sh
sh native/windows/check-shared.sh
```

测试自动复制当前平台的动态库到运行目录。Windows 默认复制 MSVC x64 Release DLL；其他构建位置可用 `-p:NativeCoreFile=/absolute/path/to/library` 指定。

### 共享服务的宿主约定

`NativeWorkspace.Open()` 仅在 Windows 使用系统 LocalAppData；非 Windows 测试必须显式传入临时 `WorkspacePaths`，不能误写真实用户目录。单实例租约保持到工作区释放。数据库 v0→v1 使用 SQLite `VACUUM INTO` 建立含 WAL 已提交内容的一致性备份，再在事务中设置版本；未来版本、陌生应用标识或不支持的表结构拒绝写入。这是原生工作区 schema 升级，**不是旧版账号迁移工具**。

Windows 数据目录/文件 ACL 仅授予当前用户、SYSTEM 和 Administrators；macOS/Linux 测试使用 0700/0600。Windows ACL 分支仍需真机运行。账号注册接口只供已经验证学校身份的宿主调用，本身不验证登录；账号和上次选择只保存非敏感信息。

创建 `JobService` 时注册各类型处理器。处理器只能通过 `JobContext.Store` 访问该任务固定的账号，将取消令牌传给 HTTP/模型请求；不要捕获可变的“当前账号”，不要启动未跟踪的后台副作用。进度文案须为可展示的固定状态，目标只传业务记录 ID，不传 Cookie、token、密钥或授权链接。原始异常不会写入任务消息。

账号切换/退出通过 `SelectAccountAsync` / `DisconnectAsync` 协调；学校连接成功后由宿主调用 `ConnectAuthenticatedAsync`。切换先中断排队项、取消活动任务并等待退出，再保存当前账号。处理器忽略取消时，切换/停止有界返回失败，队列不并发启动下一项；任务上下文已撤销，迟到的持久化写入与进度被拒绝。已进入 SQLite 的单次操作不会被强行中止。

`Changed` 通知在后台线程发出，UI 需自行派发到 DispatcherQueue、按 Owner 过滤并忽略较低 Revision；订阅者不要同步等待队列的账号切换或停止操作。关闭时先 `await jobs.StopAsync()` 或 `DisposeAsync()`，确认退出后再释放工作区。停止超时不能视为已安全关闭，须等待处理器退出或由宿主结束进程；本服务不会在仍运行时释放内核句柄。

业务结果事务由处理器负责：分析结果应先完成校验，再用共享内核的事务保存接口提交；队列不会自动回滚处理器此前已经完成的写入。应用重启只中断旧任务，人工重试可能再次发起外部请求，不能将本地去重描述为模型服务的严格一次执行保证。

W08 已增加 `SchoolSyncRuntime`：固定账号会话、学期/课程/课表事务同步、录播授权接口、目录和材料持久化；`SchoolWorkspaceReader` 离线读取，`LessonReader` 提供搜索、旧 evidence 来源和阅读位置恢复。学校方法必须在后台线程执行，传入每项任务取消令牌。宿主学校身份验证后创建 `SchoolConnection`；录播授权器负责派发独立登录窗口。账号切换/退出使用 runtime 的方法，不绕过它直接操作 `Jobs`。

验证链路使用实际 Rust 动态库和合成 HttpMessageHandler/授权器，没有发起真实学校或模型请求。详见 [同步与阅读执行记录](../../docs/windows-native-m1-sync.md)。模型宿主后续在 W11 接入。

W09/W10 页面、开发预览与限制见 [工作区执行记录](../../docs/windows-native-m1-workspace.md)。发布版不包含 `--workspace-preview` 服务。

登录接线和取消/释放证据见 [登录执行记录](../../docs/windows-native-m1-login.md)。正式工作区默认启动后可使用“连接学校”；初次登录选学期后同步，进入课程再完成独立录播授权。切账号/退出将停止旧任务并释放该学校浏览器。W10 截图缓存、逐张浏览、旧证据完整原文和材料覆盖情况已接入；Windows 平台验收仍延后。

图片与阅读器检查见 [图片执行记录](../../docs/windows-native-m1-images.md)。`SlideImageCache` 仅接受固定账号的课次/截图 ID；使用独立无凭据的限域下载器，原图经 Rust 解码校验后缓存。关闭时必须等待缓存服务排空，再释放工作区。Windows 发布脚本会生成并复制 Rust `licenses/`；该步骤不代替后续完整发布审计。

模型配置、凭据与传输见 [W11 执行记录](../../docs/windows-native-m2-model.md)。默认 Windows 宿主使用 Credential Manager；预览与共享测试使用合成内存凭据库，不保存真实 Key。设置保存不会调用模型；分析与问答将在后续接入。Windows 检查程序额外执行临时合成凭据的原生写/读/删测试，非 Windows 明确跳过而不计为通过。

## M0 尚待完成

完整进度及证据见 [M0 执行记录](../../docs/windows-native-m0.md)。Windows 真机编译/运行、实际学校登录、干净机器安装器、Electron 同机性能基线和真实发布包依赖审计均不能由本机合成测试替代。

W06/W07 的实现与验证见 [M1 共享层执行记录](../../docs/windows-native-m1.md)。

官方接口依据：[WinUI WebView2 初始化](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.webview2.ensurecorewebview2async)、[.NET 互操作](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)、[WebView2 分发](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution)。

全文分析执行与验证边界见 [W12 执行记录](../../docs/windows-native-m2-analysis.md)：离线材料分析、模型回调、取消和摘要事务保存已接入，Windows/真实模型验收待完成。

W14 课程内问答可选择整门课或指定课次。Rust 使用本地识别文本检索并校验引用；引用保存提问时的文本和时间范围，展开后可打开对应课堂。每个账号及课程独立读取最近 20 条问答。取消或模型失败时保留已有历史；没有课堂识别文本时不调用模型。

W14 本机验证覆盖真实 Rust ABI 与合成模型 HTTP、课次和账号隔离、模型鉴权失败、取消及历史读取。C# 61 项、Swift 43 项、TypeScript 检查及资源构建通过，浏览器合成预览验证指定课次提问、刷新恢复历史和引用跳转。Windows 11 x64 与真实模型验收待执行。

W15 可从课堂阅读器离线导出 UTF-8 Markdown。文件保存在学习空间的 `exports` 下，按账号和课次分目录，名称保留课程和课次中文。导出包含时间轴、部分整理状态、作业与事项、引用原话及当前完整识别文本。重复导出同名文件时先完成临时写入再替换。C# 合成回归与浏览器预览已验证文件生成，Windows 真机和资源管理器入口待验证。

## W16–W20 测试版流程

- PPT 截图页通过任务队列导出去重 PDF，复用本地图片缓存。原始分辨率的 RGB 像素经 Flate 无损嵌入，按首次出现排序，页脚标记出现时间。截图变更后仍可打开旧导出。单次最多 1000 张，图片逐张读取。临时文件写完并检查截图快照后才发布。
- 课程自动化用一笔事务保存草稿，只在应用运行且学校连接有效时检查。使用真实课表和北京时间，先保存重试时间再请求。已有有效材料和同摘要分析可复用。Windows 睡眠唤醒与真实学校环境待验证。
- 旧数据迁移要求先关闭 Electron 和原生版，并在原生版建立对应学校账号。在发布程序目录使用 PowerShell 7 运行以下命令。第一次只预检，核对报告和冲突后加 `-Apply` 才写入。备份在原生数据目录的 `backups`。目标记录保留冲突值，笔记、材料等阻塞冲突使本次事务不写入。迁入规则默认禁用，旧任务不会自动重放。PDF 与 SQLite 通过持久迁移清单处理失败与重启恢复。缺失缓存需要重新获取，模型密钥需重新配置。

```powershell
pwsh -File ./migrate-legacy.ps1 -LegacyDatabase 'C:/path/to/learning.sqlite3' -SchoolAccount '学校账号'
```

Windows x64 测试安装器构建需要 NSIS。普通用户安装在 `%LOCALAPPDATA%/Programs/BUAA Learn Native`，升级切换开始菜单入口，卸载保留 `%LOCALAPPDATA%/BUAA Learn Native` 的学习数据。缺少 WebView2 时可通过 Microsoft 安装 Evergreen，离线机器需提前下载安装独立运行时。

```powershell
pwsh ./build.ps1 -Publish
pwsh ./package.ps1 -MakeNsis 'C:/Program Files (x86)/NSIS/makensis.exe'
pwsh ./startup-samples.ps1 -Executable ../../dist/native/windows/probe/BUAALearn.Probe.exe -Samples 20
```

`package.ps1` 会检查 x64 PE 文件、禁止随包附带数据或密钥类文件，并输出 SHA-256。正式签名使用证书存储中的签名身份，调用方提供 `-CertificateThumbprint`、`-TimestampUrl` 和可用的 SignTool。当前没有正式签名记录。

验收记录按 [Windows 原生 PRD](../../docs/windows-native-prd.md) 的 A01–A22 留存。CI 脚本覆盖构建、WebView2 启动、合成课程加载和安装生命周期。真实学校 SSO、模型、普通用户环境、DPI/读屏、8 小时稳定性及 Electron 同机性能比较需另行实测。

同机对照时，分别启动 Electron 和原生版，在同一 Windows 测试账号和同一场景下记录根进程 ID，运行 `pwsh ./process-samples.ps1 -RootProcess <PID> -DurationMinutes 5`。长时间验收用 `-DurationMinutes 480`，同时记录同步、阅读、休眠和唤醒操作。采样报告聚合根进程及采样时可见的子进程，短于采样间隔的进程可能遗漏。启动采样单独输出 p50/p95，测试数据由程序在临时空间生成。
