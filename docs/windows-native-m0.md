# Windows 迁移 M0 执行记录

日期：2026-10-31 · 状态：进行中，未满足 M0 出口条件

2026-11-01：按用户要求延后 Windows 编译与真机验证，先推进 W06/W07；新进度见 [M1 共享层记录](windows-native-m1.md)。本页保留 M0 当时的验证结果，不将 Windows 待验收项改为通过。

需求依据：[Windows 原生迁移 PRD](windows-native-prd.md)。本轮只实施跨语言底座和学校连接原型，没有迁移正式页面或启用旧数据迁移。

## 基线

- Git HEAD：`e731cc61302080b4b3b4c4dc765acf81b94471c3`，当前分支 `main`。
- 开始时已有 Python/前端改动及未跟踪的 `native/`；这些内容没有被重置或覆盖。
- 迁移前源码快照：`dist/native/windows/m0-baseline-2026-10-31/source.tar.gz`。
- 快照 SHA-256：`6b8dfdf73dfcf5b8927f957a629cd7747a048e572c72e45b7217ba85cc83194c`。
- 同目录 `manifest.json` 记录开始时 Git 状态及 159 份源码/配置/文档的 SHA-256。快照只保存在本机被忽略的 dist 目录，不含真实数据库、课堂材料、学校会话或 `.env.local`；不是 Git 提交的替代物。
- 合成学期/课程夹具位于 `native/windows/BUAALearn.Tests/Fixtures/`；录播及材料夹具在测试程序内。完整分析/问答/PDF 基线夹具在进入 M2 前仍需整理。

## 当前交付

| 任务 | 已完成的代码或准备 | 尚未通过的条件 |
| --- | --- | --- |
| W00 基线 | 本地源码快照、哈希、Git 状态；合成学校互操作数据 | 完整学习/导出对照夹具及稳定提交基线 |
| W01 决策 | 推荐自包含 .NET / App SDK、学校会话仅内存；原型无侧栏/单选弹窗 | D01 的 Windows 原生组件例外仍待明确，不改写 AGENTS.md |
| W02 工程 | 独立 Windows 工程、DLL 输出、ABI major 查询、固定工具版本、Windows CI 定义 | Windows DLL/WinUI 实际构建及 CI 运行结果 |
| W03 互操作 | Cdecl、UTF-8、SafeHandle、静态委托与 GCHandle 根、配套释放、串行句柄操作、网络超时/取消和错误封装 | Windows 实际加载/运行及更完整的 panic/退出故障验证 |
| W04 登录 | WinUI / InPrivate WebView2；独立目录；选择学期/课程/课次；目录与材料验证；主机/凭据隔离 | Windows 校网真实账号、验证码、弹窗/重定向、Cookie 与过期验证 |
| W05 分发/测量 | PowerShell 构建、ZIP 发布、包体字节统计、无学校登录的原生启动冒烟脚本 | EXE 安装器、干净机器依赖验证、Electron 同机基准和签名 |

**没有将 W00–W05 全项勾选完成。** 新增 workflow 尚未提交/推送或调度，不能报告 CI 已通过。ZIP 发布脚本也不等同于安装器已完成。

## 本机验证

环境：Apple Silicon macOS。共享层测试不等于 Windows ABI、WinUI 或学校登录验证。

| 验证 | 结果 |
| --- | --- |
| 迁移前 Rust 回归 | 58 项通过 |
| 新增 ABI 版本查询后的 Rust 回归 | 59 项通过 |
| Rust Release 动态库与静态库 | 本机构建通过；dylib 约 3.1 MiB，不是 Windows DLL 大小 |
| macOS 原生原型回归 | 43 项通过 |
| macOS 链接依赖 | `otool -L` 确认未新增对 `libbuaa_core.dylib` 的依赖，继续显式链接 archive |
| C# 共享层编译与互操作检查 | .NET 10.0.403 Release 编译通过；11/11 项合成检查通过 |
| WinUI 窗口 C# API/类型检查 | 通过；使用恢复的 205 个真实托管引用，临时桩仅替代 XAML 生成的 `InitializeComponent`，按 SDK 默认规则忽略 1701/1702 引用统一警告；不是完整 WinUI 构建 |
| WinUI NuGet 依赖 | 恢复成功，生成各工程 `packages.lock.json`，Windows 构建脚本使用 locked mode |
| Windows WinUI 编译、启动、真实学校链路 | 未执行 |

构建环境曾提示 Rust objcopy 缺少 libLLVM、SQLite object 的 macOS deployment target 高于宿主声明；命令成功、测试通过。这些本机工具链提示不作为 Windows 构建证据，也不代表已重新验证 macOS 13 最低系统支持。

本机尝试 WinUI 完整构建，在调用 Windows 专用 `XamlCompiler.exe` 时以 `cannot execute binary file` 失败，未进入可运行产物生成。窗口 C# 静态检查在此后单独执行并通过；不能据此宣称 XAML 编译、资源生成、Windows 打包或启动成功。

验证命令与证据：

```sh
cargo test --locked --offline --manifest-path native/core/Cargo.toml
cargo build --release --locked --offline --manifest-path native/core/Cargo.toml
swift test --package-path native/macos-login
cd native/windows
dotnet run --project BUAALearn.Tests -c Release
dotnet restore BUAALearn.App/BUAALearn.App.csproj -p:Platform=x64 -p:EnableWindowsTargeting=true
```

本机 .NET SDK 为下载并按官方 SHA-512 验证后的项目内工具，位于被忽略的 `dist/native/windows/tools/dotnet`，未纳入发布内容。窗口 API 检查的响应文件、临时桩和结果在 `dist/native/windows/api-check/`；完整构建输入在应用被忽略的 `obj/` 目录。源代码中的 synthetic 值均为合成数据。

## Windows 验证入口

在具备工具链的 Windows 11 x64 开发机，从 `native/windows` 运行：

```powershell
pwsh ./build.ps1 -Publish
pwsh ./smoke.ps1 -Executable ../../dist/native/windows/probe/BUAALearn.Probe.exe
```

随后按照 [原型 README](../native/windows/README.md) 的手动流程验证真实学校登录，填写下表。只记录脱敏结果，不附完整 URL、Cookie、token、密码或课堂正文。

| 检查 | 结果 | 环境/日期/证据 |
| --- | --- | --- |
| Rust Windows x64 DLL、C# 合成检查 | 待验证 | — |
| WinUI 窗口与独立 InPrivate WebView2 | 待验证 | — |
| SSO 学生角色、学校返回的多学期 | 待验证 | — |
| 课程与实际录播目录 | 待验证 | — |
| 一节非空课堂材料 | 待验证 | — |
| 取消/退出/重新连接/临时资料清理 | 待验证 | — |
| 干净机器缺少/安装 WebView2 | 待验证 | — |
| 安装包、启动、常驻及登录峰值内存 | 待验证 | — |

上述出口检查保留。按 PRD 的 D04，先继续共享存储、任务队列和模拟接口工作，不将模拟请求成功当成学校登录成功；完整 Windows 页面、运行时及发布仍需平台验收。
