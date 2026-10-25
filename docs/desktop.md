# 桌面版构建

桌面版使用 Electron 窗口和独立 Python 服务，保留 Web 版的全部功能。Python、Node.js 和登录用 Chromium 随安装包提供；用户无需另行安装。Windows 构建 x64 安装 EXE，macOS 分别构建 Apple Silicon 和 Intel DMG。

## 本机构建

在**目标系统及目标架构**运行，使用 Python 3.12+、Node.js 24+：

```sh
python -m venv .venv
# 激活虚拟环境后：
python -m pip install -e ".[dev]" pyinstaller==6.22.3
python scripts/build_desktop_backend.py
python scripts/smoke_desktop_backend.py
cd desktop
npm ci
npm test
npm run pack
cd ..
python scripts/smoke_desktop_app.py
cd desktop
npm run dist:mac
# Windows 改为 npm run dist:win
```

产物位于 `dist/desktop/`。GitHub Actions 的 **Desktop installers** 可手动运行三平台构建，安装包和实际窗口截图附在构建结果中。发布 tag 也会触发构建。

构建全部通过后，可运行 **Publish desktop release**，填写成功构建的 run ID。发布流程校验来源与构建状态，收齐三个安装包后附上 SHA-256 校验值，发布为 `desktop-v版本号` 测试版。

## 配置与数据

通过菜单 **文件 → 打开配置目录** 找到 `.env.local`，填写 `BUAA_LLM_BASE_URL`、`BUAA_LLM_MODEL`、`BUAA_LLM_API_KEY` 后重启。已设置的系统环境变量优先。首次启动自动创建空模板；不会复制开发者的密钥或账户。

- macOS：`~/Library/Application Support/BUAA Learn/`
- Windows：`%APPDATA%\BUAA Learn\`

数据位于其 `data/` 子目录，诊断记录位于 `desktop.log`。也可在应用 **模型与设置** 中配置；页面填写的密钥仅在当次运行生效。旧 Web 版用户如需迁移，退出两个版本后，将旧 `data/` 内容复制至上述 `data/`，再把自己的 `.env.local` 模型配置复制到配置目录。

macOS 关闭窗口后应用仍运行，自动化可继续；使用菜单退出或 ⌘Q 才会停止服务。Windows 关闭最后一个窗口即退出。学校登录仍需用户在独立 Chromium 窗口完成，重启后重新登录。

## 构建与分发边界

打包仅包含应用代码、静态资源和运行环境，排除源码根目录 `.env.local`、数据库和课堂材料。启动时使用随机端口和随机会话令牌；渲染页面无 Node.js 权限，服务仅接受本次桌面进程的令牌。开发版 Web 启动方式保持不变。

当前构建未配置 Apple Developer ID、公证或 Windows 代码签名证书，属于未签名测试版。系统可能显示来源或信誉提示；面向广泛分发前需要维护者配置自己的签名身份。GitHub Release 安装包不包含自动更新器，升级时下载新版本；卸载默认保留本机学习数据。

macOS 的最低系统要求由 Electron 及随附 Chromium 决定；构建与冒烟测试不等同于每个系统版本的实机兼容测试。Windows 暂提供 x64，暂不提供 Windows ARM64 原生包。
