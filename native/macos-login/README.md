# BUAA Learn macOS 原生版

SwiftUI 与系统 WebKit 提供学校登录、课程、课堂材料、时间轴笔记、作业、PPT 浏览与导出；Rust 内核负责学校接口策略、分析、自动化规则与 SQLite 记录。当前仍为本地测试版。

## 构建

需要 macOS 13 或更新版本、Xcode 和 Rust：

```sh
native/macos-login/build-app.sh
swift test --package-path native/macos-login
open 'dist/native/BUAA Learn.app'
```

应用复用 `desktop/assets/icon.png` 的原版图标。可用 `BUAA_NATIVE_APP_PATH` 指定另一个输出路径。构建会进行本地临时签名，尚未进行开发者签名、公证和分发验证。

## 本地数据与学校连接

数据保存在 `~/Library/Application Support/BUAA Learn Native/learning.sqlite3`，按学校账号隔离。课堂文本、分析、任务和自动化配置保存在记录表；获取到的 PPT 图片以 BLOB 存在同库的 `slide_assets` 表中，按账号和图片 URL 隔离。浏览与 PDF 导出优先使用本地图片；未保存的图片会尝试重新获取。部分图片未能下载时保留已有文本和图片并显示提示，可刷新材料重试。

模型密钥和学校令牌分别保存在 macOS 钥匙串的独立服务项中，不写入 SQLite，不保存学校密码。学校令牌自动刷新，刷新后的令牌同步更新钥匙串；重启时先验证保存的会话，验证通过后恢复连接。运行期间每十分钟检查学校连接。学校或录播授权失效后，界面提示重新登录，暂停自动化；临时网络错误不当作授权失效清除令牌。钥匙串不可用时仍可查看本地材料。

工作区文件锁防止多个新版 App 同时恢复或修改同一个学习空间。启动时把遗留的运行中/排队任务标记为已中断，提供重新执行入口；若上次分析结果已保存且与任务的创建时间对应，则恢复为已完成。恢复不会自动调用收费模型。材料任务重新执行时复用已保存的图片，自动化继续按已保存的检查计划运行。

## 自动化与验证范围

课程自动化先形成草稿，保存后生效。App 打开且学校会话有效时，从上课次日起每两小时检查新回放；退出后不运行。自动笔记将课堂文本发给所配置的模型服务并可能产生费用。

学校接口、授权过期、刷新/重启恢复、任务恢复、账号隔离和离线 PPT 导出有模拟或本地自动测试；真实学校环境的跨天、休眠唤醒和持续运行验证仍待完成。正式工作区不提供课程问答、学期切换和退出登录入口；不为旧版无时间轴笔记增加兼容流程。

`BUAA_LOGIN_PROBE=1` 保留内部登录验证界面。DEBUG 构建可用 `BUAA_TEST_WORKSPACE_BASE` 指定隔离测试数据目录。迁移脚本 `scripts/migrate_native_account.py` 会先备份，再做账号映射与保留现有值的合并；App 启动不会自动导入旧库。

## 设置与更新

「设置 → 本地数据与诊断」可打开学习数据目录和 `desktop.log`。「应用更新」只检查原生 Mac 安装包，下载完成后校验 SHA-256，并在 Finder 打开 DMG。退出应用后将新版拖入「应用程序」即可替换，学习数据和钥匙串配置保留。

构建可分发的 DMG 时，在目标架构上设置 `BUAA_NATIVE_VERSION` 为三段版本号，并用 `BUAA_NATIVE_DMG_DIR` 指定输出目录。发布对应 `desktop-v版本号` 的 GitHub Release 后，用 Windows 桌面发布工作区的 `scripts/publish_update_feed.py` 写入更新清单。清单有独立的原生 Mac 和 Windows 条目。
