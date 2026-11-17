# Windows 迁移 M1：本地工作区与任务队列

日期：2026-11-01 · 范围：W06/W07 共享服务 · 状态：本机服务层实现与合成验证完成，平台验收未完成

## 本轮执行调整

用户要求先跳过 Windows 编译并继续迁移。本轮按 [PRD D04](windows-native-prd.md) 推进共享存储和任务队列，没有调用 WinUI/XAML 构建器，没有运行 Windows 程序或真实学校认证。

M0 的 Windows DLL/窗口、学校登录、安装包与性能待验收项保持原状态。D01 的原生组件约定未修改，本轮没有新增正式业务页面。

## 已交付

### W06：本地工作区

- `NativeWorkspace` 持有独占工作区租约及同一 Rust SQLite 句柄，管理数据库、备份、导出、缓存和日志目录。
- Windows 默认目录通过系统 API 定位 LocalAppData；非 Windows 测试必须显式指定临时目录，未使用真实学习数据。
- 本地账号注册、账号清单与上次选择写入 SQLite 非敏感设置；重新打开后保持离线，不能把恢复账号等同于恢复学校登录。
- `AccountStore` 的 Owner 不可变；所有读写、列表和事务批次均使用该固定账号。
- Windows ACL 实现限制为当前用户、SYSTEM 与 Administrators；Unix 测试使用目录 0700、文件 0600。符号链接/重解析点在已知工作区路径上拒绝使用。
- Windows 专用版本入口使用 schema v1 和应用标识。无版本原生库先验证表结构，使用 SQLite `VACUUM INTO` 建立包含 WAL 已提交记录的备份，再事务设置版本；备份失败不升级。
- 未来版本、陌生应用标识、不支持的表结构拒绝写入；损坏的账号配置不会被静默重置。
- 新增同账号批量写入事务与非敏感设置 C ABI。现有 macOS 入口及 C ABI major version 1 保留，新增接口要求与宿主打包的内核一致。

这些能力属于原生工作区的 schema/状态管理，不代替 W18 的旧版账号映射、冲突报告和 PDF 迁移。

### W07：统一任务队列

- 一个工作区一个串行 worker，支持现有七类任务标识；处理器由后续同步/分析/导出服务注册，函数和会话不持久化。
- 按账号、任务类型、目标合并排队/运行项。先保存任务再入队；进度、终态与重试来源均持久化。
- 状态覆盖 queued、running、completed、failed、needs_login、interrupted；重试创建新 ID 并保留旧任务历史。
- 学校任务离线时直接标记需登录，不执行处理器；明确过期后撤销连接状态。离线分析是否可执行由处理器类型决定。
- 启动时恢复所有已登记账号的历史任务，将旧排队/运行项在各账号事务中标为中断，不自动重放；反复重启不会重复改写已中断项。
- 账号切换/退出先停止接收新任务、中断排队项、取消活动任务并等待其退出，再更新当前账号。旧任务始终写原账号，不会重定向到新账号。
- 处理器超时或切换/退出后撤销其存储和进度上下文；成功结束后也撤销逃逸的上下文，拒绝迟到写入。已进入 SQLite 的单次操作不强行中止。
- 处理器忽略取消时不并发开启下一项；账号切换等待超时则不改变当前账号，停止有界返回 false，不伪装成退出成功。
- 通知包含 Owner 与单调递增 Revision，供 UI 派发和过滤过期状态。订阅者抛错不停止队列；原始异常内容不写入任务消息。

队列不是业务结果的通用事务：处理器必须先校验结果，再用相应事务接口保存。人工重试可能重新调用外部服务；本地去重不能保证外部模型严格一次计费。当前处理器全部来自合成测试，真实同步与模型调用尚未接入。

## 代码入口

| 位置 | 内容 |
| --- | --- |
| [NativeWorkspace.cs](../native/windows/BUAALearn.Platform/NativeWorkspace.cs) | 路径、权限、租约、本地账号、固定账号存储 |
| [JobService.cs](../native/windows/BUAALearn.Platform/JobService.cs) | 串行队列、状态、恢复、账号生命周期和停止 |
| [CoreBridge.cs](../native/windows/BUAALearn.CoreInterop/CoreBridge.cs) | 版本化数据库、设置、列表及批量写入封装 |
| [store.rs](../native/core/src/store.rs) / [ffi.rs](../native/core/src/ffi.rs) | schema 验证、备份、事务与新增 C ABI |
| [WorkspaceJobChecks.cs](../native/windows/BUAALearn.Tests/WorkspaceJobChecks.cs) | 账号、恢复、并发、子进程租约及迟到写入检查 |
| [check-shared.sh](../native/windows/check-shared.sh) | 不构建 WinUI 的本地/CI 检查入口 |

## 验证

环境：Apple Silicon macOS，.NET SDK 10.0.403、Rust 1.99.1，使用合成记录与临时数据库。C# 服务和测试项目编译无警告、无错误。

| 检查 | 结果与范围 |
| --- | --- |
| Rust | 62 项通过；新增含 WAL 备份、未知/未来 schema 拒绝、备份失败不升级及批次回滚检查 |
| C# | 21 项通过，包含原 M0 的 11 项与新增的 10 项工作区/队列场景 |
| macOS 原生版回归 | 43 项通过，验证新增共享接口未破坏现有调用 |
| Rust 修改文件格式 / shell 语法 | 检查通过 |
| Windows ACL 分支 | 代码与专用检查已准备，未在 Windows 运行 |
| Windows 编译 / XAML / 真实学校链路 | 按用户要求本轮跳过，未标记通过 |
| CI | 配置已更新，但本地改动尚未推送，未声称远端 CI 通过 |

关键场景包括：重开恢复账号但不恢复在线状态；两个账号同 ID 记录互不覆盖；第二个进程被工作区租约阻止、释放后可打开；旧原生库备份后可读；错误批次整体回滚；20 个并发相同请求只执行一次；进度可从数据库读取；全账号重启恢复且不重放；登录过期时队列不继续请求；失败后保留原有效结果的合成处理器可用新 ID 重试；切账号取消活动/排队任务；忽略取消的处理器使切换/停止有界失败，迟到写入被拒绝。

执行：

```sh
sh native/windows/check-shared.sh
swift test --package-path native/macos-login
```

本机 Rust Release 构建仍可能显示已有的 objcopy/libLLVM 工具链警告；构建和测试成功，不将其作为 Windows 构建证据。检查日志/结果清单位于被忽略的 `dist/native/windows/m1-evidence/`。

## 后续顺序（后续进度见 [同步与阅读执行记录](windows-native-m1-sync.md)）

1. W08：把模拟学校同步接入队列，完成学期/课程/课表/课次/材料的持久化流程，并将登录过期错误映射为需登录状态。
2. W08 的学校/模型传输必须接收任务取消令牌；不能直接把当前登录原型的窗口生命周期令牌当成所有任务的取消边界。
3. 正式窗口接入时使用同一个状态源；关闭顺序为停止队列 → 等待退出 → 释放工作区。W09 等业务 UI 仍须处理 D01。
4. 后续补齐 Windows DLL、ACL、进程互斥、WinUI 编译/启动、真实 SSO、干净安装与性能验收。W06/W07 总任务保留未勾选，避免把服务层测试当成整个平台交付完成。
