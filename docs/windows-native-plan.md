# Windows 原生适配方案

状态：2026-11-01 按用户 D01 决策更新；W12 全文分析本机链路已验证，Windows 真机验证延后。

后续执行以 [Windows 原生迁移 PRD](windows-native-prd.md) 为准，包含当前功能对照、界面约定决策、分阶段任务及验收用例。用户已确认 Windows 继续使用 React Base UI：业务页面由本地 WebView2 承载，WinUI 负责窗口与系统能力，不再以全部业务界面原生作为交付承诺。

## 1. 技术路线

采用 **C# + WinUI 3 + 现有 Rust 业务内核**。学校登录使用独立 WebView2 窗口；课程工作区、阅读器、设置与任务状态使用本地 React 页面。业务 WebView2 通过仅允许明确业务命令的消息桥调用 C# 服务，不能任意读写文件或数据库；学校页面不获得此桥。发布程序无需启动 Python 服务或监听本地端口。

首发以 Windows 11 x64 为验收目标，ARM64 在完整链路通过后单独构建和验证。Windows 10 兼容性另立测试目标，不直接由框架兼容性推定。使用当期稳定且受支持的 .NET / Windows App SDK，立项时锁定具体版本。

现有 Electron Windows 版继续维护，原生版通过验收后再切换默认下载入口。

微软将 WinUI 3 作为新建原生 Windows 应用的推荐框架：[Windows 应用开发文档](https://learn.microsoft.com/en-us/windows/apps/)。

## 2. 现有代码的复用边界

| 现有部分 | Windows 处理方式 |
| --- | --- |
| `native/core` 的学校协议、课程/材料解析、来源校验、分析、问答、时间轴和存储 | 复用，补 Windows 构建及跨语言测试 |
| Rust 自动化规划与执行模块 | 规划已有 C ABI；执行目前是 Rust trait 接口，需要补宿主可调用入口，不能视为已经可直接调用 |
| `native/core/include/buaa_core.h` | 作为 C# 互操作的起点，新增 ABI 版本查询和契约测试 |
| Swift 学校传输、模型请求、调度和学习工作流 | 参考行为与测试，分别改写为 C# 宿主服务 |
| SwiftUI、AppKit、Keychain、ImageIO/CoreText 导出 | Windows 平台实现，不能直接复用 |
| 旧版 Python PDF 去重与导出 | 作为结果对照；图像去重算法宜下沉 Rust，PDF 编码器另做技术验证 |
| React Base UI 组件 | 按下述 UI 约定保留，不将其误当作 XAML 控件 |

原始基线的 Rust crate 只配置 `lib` 和 `staticlib`；M0 已增加 `cdylib` 与 ABI 查询。Windows `buaa_core.dll` 仍须在 Windows 构建验证，现有 macOS 静态库不能直接当 Windows DLL 加载。

## 3. 平台结构与互操作

建议新增 `native/windows/`：

```text
BUAALearn.App/          WinUI 窗口、页面、ViewModel
BUAALearn.Platform/     学校登录、HTTP、凭据、文件和调度
BUAALearn.CoreInterop/  ABI 声明、SafeHandle、UTF-8、回调
BUAALearn.Tests/        宿主及互操作测试
build.ps1              Windows 本机构建与打包入口
```

- C ABI 统一 C 调用约定、NUL 结尾 UTF-8 与 JSON envelope；Rust 输出保留为指针，读取后在 `finally` 调用 `buaa_core_string_free`，恰好一次。
- `CoreSchool` / `CoreStore` 使用 `SafeHandle`。学校回调、GCHandle 上下文与委托在整个会话期间保持有效，关闭会话后释放。
- C# 回调分配的响应由配套 release 回调释放，不混用 Rust 和 .NET 分配器；托管异常不能穿越 ABI。
- 审计 Rust 导出边界的参数验证与 panic 行为，错误转换为明确结果；内存分配失败等不可恢复故障不承诺可捕获。
- 现有传输回调为同步接口；在专用工作线程执行 Rust 调用，HTTP 使用异步实现并在适配边界等待。回调不得依赖 UI 线程，进度通过 DispatcherQueue 投递。
- 同一会话串行调用；账号切换先停止调度并等待旧任务安全结束。取消、超时和退出等待边界需显式实现，不能假设现有 ABI 已支持取消。

互操作依据：[.NET 原生互操作最佳实践](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)。

## 4. 首先验证学校登录

这是首个技术关卡，验证通过后才铺完整页面。

1. 创建独立 WebView2 登录窗口，使用应用专属、可写的临时用户数据目录，不读取用户 Edge 资料。
2. 用户在学校页面完成 SSO；按现有协议获取教学空间会话，读取真实学期和课程。
3. 完成录播授权握手，并实际成功读取所选课程的录播目录，才确认连接成功。
4. 会话交给内存中的学校服务；Cookie、token、完整授权链接不进入 SQLite、日志或崩溃附件。
5. 退出登录关闭会话和 WebView；等待浏览器进程释放后清理临时目录，异常退出遗留目录在下次启动清理。使用受支持的 InPrivate 模式还需验证所锁定 WinUI/WebView2 版本的初始化接口，不能把独立目录等同于纯内存存储。

页面导航按真实 SSO 跳转域逐项允许；学校 HTTP 传输保留现有精确主机限制、教学空间/录播凭据隔离、拒绝自动重定向和响应大小限制。远程页面不获得文件系统或任意本机调用桥。

必须在 Windows 校网/实际访问环境验证验证码、弹出窗口、跨域跳转、Cookie 行为和会话失效。若学校拒绝嵌入式浏览器，先验证其支持的系统浏览器回调方案；现有内部协议未证明支持标准 OAuth 回调，不能直接套用 MSAL/WAM。

参考：[WinUI WebView2](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/webview2)、[用户数据目录](https://github.com/MicrosoftDocs/edge-developer/blob/main/microsoft-edge/webview2/concepts/user-data-folder.md)。

## 5. 界面与项目约定

保留现有工作区的信息结构、暖白底色和留白；接入 Windows 的窗口缩放、DPI、键盘、焦点、高对比度及屏幕阅读器。笔记仅展示课堂时间轴，不增加独立摘要、知识点、作业或复习建议区块。

**现有 AGENTS.md 要求单选下拉统一使用 React 的 shadcn Base UI Combobox，侧栏统一使用 SidebarProvider/Sidebar/SidebarTrigger，自动化使用 Base UI Checkbox/Switch。它们无法直接变成 WinUI 控件。**

用户已选择保留该要求：完整业务工作区复用 React 共享组件，使用独立本地 WebView2，避免多处小型 WebView 造成焦点与布局割裂。静态资源随应用打包，不启动本地 HTTP 服务器。消息桥必须验证精确本地页面来源、版本、参数与账号上下文；禁止远程导航、弹窗、下载和任意网络/文件命令。业务 WebView2 全部子进程计入性能结果。

原全部 XAML 提案不再作为当前实施范围。先前基于无业务 WebView2 的内存估算不适用于此路线；包体、启动与内存收益需在 Windows 同机实测后报告。

自动化保留多课程草稿、统一保存、关闭自动获取同步关闭自动笔记的行为；缺少材料、模型费用与登录失效提示继续保留。

## 6. 本地数据、密钥与迁移

- 新数据库建议放在 `%LOCALAPPDATA%\BUAA Learn Native\learning.sqlite3`，导出和日志使用同目录下的独立子目录；通过系统目录 API 定位，不依赖当前工作目录。
- 学校会话仅驻留内存。模型 API Key 通过 Windows Credential Manager 保存，键由学校账号与规范化服务地址共同确定；更换地址不沿用旧密钥，日志不记录密钥。
- 数据文件使用当前 Windows 用户的 ACL；不把 POSIX `chmod` 当作 Windows 权限保护。安装目录只放程序，不保存数据库。
- SQLite 继续由 Rust 内核统一管理；单实例或明确的并发写入策略，退出时等待事务结束。升级前备份，数据库 schema 升级失败保留旧库。
- 旧版与原生版数据库分离。旧版账号键是账号 SHA-256 前 24 位，原生版使用学校账号，必须显式映射。
- 正式迁移流程：关闭旧版写入 → SQLite 一致性备份（包含 WAL 处理）→ 只读预检 → 展示数量与冲突 → 向目标事务导入 → 复制并校验导出文件。源库始终保留；失败回滚目标。
- 现有导入工具只完成部分底层能力，尚不构成完整迁移流程。Windows 同文件识别、路径大小写、中文用户名、文件占用和 ACL 均需真机验证。

## 7. 自动化、导出与分发

首版保持“应用运行且学校会话有效时执行”。使用 Rust 的北京时间规则，睡眠唤醒后检查到期任务，防止并发重复获取或重复模型计费；重启后要求重新登录再恢复学校请求。托盘、开机启动和系统后台服务后续单独做，首版不承诺退出后自动获取。

Markdown 依据时间轴数据生成。PDF 导出单列交付项：用合成图片验证去重、原始分辨率、无损嵌入、1000 张上限、内存上限、临时文件和原子替换后再确定编码库；不能以浏览器打印替代现有图片型 PDF 行为。失败时保留原有完整文件。

测试版先采用按用户安装的 EXE，打包 .NET 与 Windows App SDK 所需运行时、Rust DLL、静态资源和许可证；检查 WebView2 Runtime，提供在线安装及离线分发路径。自包含 Windows App SDK 不等于内置 WebView2 Runtime。正式版配置代码签名，再评估 MSIX/Store。发布包不得携带开发数据或学校登录资料。

参考：[Windows App SDK 部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview)、[WebView2 运行时](https://learn.microsoft.com/en-us/microsoft-edge/webview2)。

## 8. 分阶段交付与验收

| 阶段 | 交付 | 通过条件 |
| --- | --- | --- |
| P0 | Windows DLL、最小 WinUI 登录原型 | 中文路径可加载 DLL；字符串/句柄/回调释放测试通过；真机登录并读到学期、课程和录播目录 |
| P1 | 课程、课表、课次、原文与截图 | 获取材料并按账号落库；断网后重开可读；失效登录明确提示 |
| P2 | 时间轴分析、问答、模型设置、导出 | 来源校验与错误处理对齐；失败不覆盖有效结果；密钥隔离；PDF 保真测试通过 |
| P3 | 自动化、迁移和安装包 | 草稿保存与禁用关系正确；休眠恢复不重复计费；迁移可回滚；干净 Windows 可安装/升级/卸载且保留数据 |

Windows CI 运行 Rust、C# 互操作和宿主测试，构建安装包并做启动冒烟检查。真实学校 SSO、凭据管理器、DPI/读屏、睡眠恢复和升级迁移另做真机验收；CI 成功不能替代这些检查。

单人投入的初步排期为 P0 3–5 个工作日、P1 5–8 日、P2 7–10 日、P3 5–8 日，约 4–6 周，前提是现有协议在 WebView2 可用、UI 路线明确且 PDF/迁移无新增阻塞。P0 结束后依据实测重估；这不是已验证的工期承诺。

优先启动 P0。它同时验证跨平台内核能否被 C# 安全调用，以及学校登录能否在 Windows 原生宿主成立，是后续工作的准入条件。
