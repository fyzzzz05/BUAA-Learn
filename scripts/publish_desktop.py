"""Publish only complete, successful builds from this repository's desktop workflow."""
import base64
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", path], text=True))


def main():
    repository = os.environ["GH_REPO"]
    run_id = int(os.environ["BUILD_RUN_ID"])
    run = api(f"repos/{repository}/actions/runs/{run_id}")
    assert run["repository"]["full_name"] == repository
    assert run["path"] == ".github/workflows/desktop.yml"
    assert run["status"] == "completed" and run["conclusion"] == "success"
    revision = run["head_sha"]
    package = api(f"repos/{repository}/contents/desktop/package.json?ref={revision}")
    version = json.loads(base64.b64decode(package["content"]))["version"]
    assert re.fullmatch(r"\d+\.\d+\.\d+", version)
    expected = [f"BUAA-Learn-{version}-{target}" for target in (
        "mac-arm64.dmg", "mac-x64.dmg", "win-x64.exe")]
    assets = []
    lines = []
    for name in expected:
        found = list(Path("release-assets").rglob(name))
        assert len(found) == 1, f"Expected exactly one installer: {name}"
        file = found[0]
        assert file.stat().st_size > 10_000_000, f"Installer appears incomplete: {name}"
        with file.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        assets.append(str(file))
        lines.append(f"{digest}  {name}\n")
    checksums = Path("release-assets/SHA256SUMS.txt")
    checksums.write_text("".join(lines), encoding="utf-8")
    notes = Path("release-assets/release-notes.md")
    notes.write_text(f"""BUAA Learn 首个桌面测试版。安装包内置 Python 服务与登录浏览器，无需安装 Python、Node.js、Ego 或插件。

| 系统 | 安装包 |
| --- | --- |
| Apple Silicon Mac（M 系列） | mac-arm64.dmg |
| Intel Mac | mac-x64.dmg |
| Windows x64 | win-x64.exe |

安装后点击 **SSO 登录**；在 **模型与设置** 填写模型，或从菜单 **文件 → 打开配置目录** 编辑 `.env.local` 后重启。课程、笔记及配置保存在本机，安装包不含作者账户、API 密钥或课堂数据。

支持课程同步、课堂时间轴、来源引用、作业与考试/考勤/小测事项整理、课程问答、去重课件 PDF，以及上课次日起每 2 小时检查新回放的自动化。

三种系统构建均通过后端测试、内置 Chromium 启动、冻结环境 PDF 去重导出和实际桌面窗口启动测试。[构建记录]({run['html_url']}) · 源码 `{revision}`。学校 SSO 需要用户本人操作，自动测试不代替各校网环境下的实机登录验证。

当前未配置 Apple Developer ID / 公证或 Windows 代码签名，系统可能显示来源或信誉提示。应用没有自动更新器；下载新版本升级即可，本机学习数据保留。

macOS 关闭窗口后应用继续运行，⌘Q 退出；Windows 关闭最后一个窗口即退出。自动化需要应用运行且学校登录有效；生成笔记会发送课堂文本至你配置的模型服务，并可能产生调用费用。

[配置、数据迁移及自行构建说明](https://github.com/{repository}/blob/main/docs/desktop.md)。非北航官方应用，AI 整理结果请结合课堂原文核对。安装包的 SHA-256 校验值见 `SHA256SUMS.txt`。
""", encoding="utf-8")
    # A separate desktop tag avoids triggering the source-tag build a second time.
    subprocess.run([
        "gh", "release", "create", f"desktop-v{version}", "--repo", repository,
        "--target", revision, "--title", f"BUAA Learn {version} · 桌面测试版",
        "--prerelease", "--notes-file", str(notes), *assets, str(checksums),
    ], check=True)


if __name__ == "__main__":
    main()
