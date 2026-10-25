# 项目界面约定

用户指定：今后所有新增或修改的下拉单选弹窗，统一使用 **shadcn/ui 的 Base UI Combobox**。

官方组件：https://ui.shadcn.com/docs/components/base/combobox

- 统一封装并复用该组件，课程、学习方式及后续新增的单选下拉均遵循此约定。
- 使用 Base UI 版本；保留其键盘操作、焦点管理、选中状态、搜索和空结果处理。
- 已通过 React islands 接入官方 Base UI Combobox 与 Sidebar；组件位于 frontend/src/shared/ui，来源记录在 frontend/shadcn-sources.json 与 frontend/sidebar-sources.json。
- 侧栏统一使用 SidebarProvider/Sidebar/SidebarTrigger，触发器位于顶栏左侧，不恢复旧 CSS 类控制的两套折叠按钮。

## UI 文案与布局

Avoid unnecessary secondary labels and descriptions. Let the layout do the explaining.

- 优先用布局、清晰的按钮和状态表达用途，避免重复副标题、操作教学和功能介绍。
- 保留影响用户判断的错误、材料缺失、费用和登录条件提示。
- 学习笔记仅展示课堂时间轴，不重复展示独立的摘要、知识点、作业、事项和复习建议区块。

- 课程自动化使用官方 Base UI Checkbox/Switch 的多课程列表；勾选先形成草稿，统一保存后生效，未开启自动获取时禁止切换自动笔记。
