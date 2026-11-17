# Windows 迁移：React 工作区与 WinUI 宿主

日期：2026-11-01。对应 W09/W10 的首条页面路径；迁移目标仍在进行中，不代表 Windows 功能验收完成。

## 本轮交付

- `frontend/src/apps/windows` 是独立 Windows 入口，默认本学期课程，没有首页问答。业务页面使用同一 React 树，复用项目 SidebarProvider/Sidebar/SidebarTrigger，触发器只有顶栏左侧一处。
- 新增共享 `SearchSelect`，Windows 和原 `mountCombobox` 适配器复用同一个 Base UI Combobox。保留搜索、空结果、键盘、焦点和选中行为。
- 页面覆盖本学期课程、真实课表缓存、课次状态、任务历史、时间轴、原始识别文本、截图索引和材料情况。时间轴只展示分段内容与来源，不重复独立摘要等区块。
- 阅读器通过消息桥读取固定账号的数据、解析旧 evidence 来源，并保存标签、搜索、锚点及位置。原文按 100 段分页展示并在全部原文中搜索，页码可根据锚点恢复；这不是 W20 的大列表性能验收。
- `WorkspaceWindow` 加载打包的本地资源，消息桥在后台执行，任务变化投递到 UI。禁止远程导航、子框架、弹窗、下载及权限请求；无 HostObject。CSP 和资源请求白名单只开放打包的 index/app.js/app.css，学校页面不使用此桥。
- WinUI 默认启动工作区；`--probe` 和 `--smoke-report` 保留原连接验证窗口。窗口关闭等待初始化及队列安全退出后释放工作区，停止失败时保持窗口并允许重试关闭。
- Windows 构建脚本新增前端类型检查和打包；Node 仅为开发机依赖，最终用户不运行 Node 服务。资源随应用发布，开发预览服务与合成数据位于 Tests，未引用到 App。

当前静态资源合计 566397 字节，仅为未压缩的 JS/CSS/HTML 大小，不是 EXE 或安装包大小，也不能证明内存或启动提升。

## 浏览器验证

目标路径：课程列表 → 课次 → 原文搜索/笔记来源 → 阅读记忆 → 切换账号。

环境：macOS，Ego Lite Chromium；实际 `WorkspaceBridge`、C# 服务和 Rust SQLite 动态库，使用临时合成数据库。测试专用 loopback HTTP 适配器仅替代 WebView2 消息传递，不是产品架构中的本地服务。生产 JS 不包含该适配器。

| 检查 | 结果与边界 |
| --- | --- |
| 页面身份、非空、无框架错误页 | 通过，标题 BUAA Learn，默认课程入口 |
| 课程与课次跳转 | 通过，显示数据库中的课程、日期与状态 |
| 中英文原文搜索 | 通过；搜索 `sliding` 找到对应原话 |
| 阅读记忆 | 刷新后恢复原始识别文本标签与搜索词 |
| 来源弹窗 | 从保存的 evidence 显示原话；Escape 关闭后焦点返回来源按钮 |
| Base UI 账号选择 | 无匹配状态可读，ArrowDown/Enter 可选择账号 |
| 账号隔离 | 切乙账号不显示甲课程，回到课程入口 |
| 640×720 窄窗口 | 无横向溢出；侧栏抽屉可导航，关闭动画结束后退出 |
| 控制台 | 捕获的 Runtime/Log 事件无相关异常或错误 |
| C# 共享检查 | 34 项通过，新增 reader/save_reading 参数与范围检查包含在消息桥场景 |
| 前端 | TypeScript、Windows 生产/预览打包、原网页打包均通过 |
| WinUI C# API 编译 | 使用已恢复的真实 SDK 引用与 InitializeComponent stub，通过；没有运行 XAML 编译器 |

截图位于本机 `/tmp/buaa-windows-courses.png`、`/tmp/buaa-windows-reader.png`、`/tmp/buaa-windows-narrow.png`，不作为长期发布证据。检查记录与源文件摘要位于忽略目录 `dist/native/windows/m1-workspace-evidence/`。

## 后续更新

正式学校登录、取消/退出与录播适配已在 [登录接线执行记录](windows-native-m1-login.md) 推进；下列列表保留为本轮历史边界，当前进度以最新记录为准。

## 本轮结束时尚未完成

- 正式工作区的学校登录/录播授权窗口适配还未接线，连接按钮当前禁用。M0 `--probe` 仍可单独验证学校流程，但其结果不会注册到正式工作区。下一步先补齐该适配器，不能把本轮离线合成链路说成完整登录路径。
- 截图当前只显示索引和未缓存状态；下载、校验、缓存、离线图片与原始回放系统入口待 W10 完成。
- 模型设置、分析/问答、作业、导出、自动化、迁移和正式安装器仍按后续 W11–W20 推进。
- Windows XAML 编译、启动、WebView2 实际隔离、关闭和资料目录释放、真实 SSO、DPI/Narrator/性能仍待 Windows 设备验证。

## 复现本机页面验证

```sh
cd frontend
npm run typecheck
npm run build:windows
node build-windows.mjs --preview
cd ../native/windows
../../dist/native/windows/tools/dotnet/dotnet run --project BUAALearn.Tests -c Release -- --workspace-preview ../../dist/native/windows/workspace-preview 48183
```

打开 `http://127.0.0.1:48183/index.html`。预览仅含合成账号，学校连接始终离线，退出进程后删除临时数据库。不得将预览入口作为发布服务。
