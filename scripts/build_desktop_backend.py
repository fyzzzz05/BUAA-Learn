"""Build on the target OS/architecture. Only code, static assets and browser are bundled."""
import os
import shutil
import subprocess
import sys
from importlib.metadata import distributions
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
os.chdir(ROOT)
os.environ["PLAYWRIGHT_BROWSERS_PATH"] = "0"
subprocess.run([sys.executable, "-m", "playwright", "install", "chromium", "--no-shell"], check=True)
# Preserve Chromium's signed .app structure and symlinks. PyInstaller must not rewrite it.
hooks = ROOT / "build" / "desktop-hooks"
hooks.mkdir(parents=True, exist_ok=True)
hook = ('from PyInstaller.utils.hooks import collect_data_files\n'
        'datas = collect_data_files("playwright", excludes=["driver/package/.local-browsers/**"])\n')
for name in ("sync_api", "async_api"):
    (hooks / f"hook-playwright.{name}.py").write_text(hook, encoding="utf-8")
subprocess.run([
    sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--onedir",
    "--name", "buaa-backend", "--distpath", "dist/backend", "--workpath", "build/backend",
    "--specpath", "build", "--additional-hooks-dir", str(hooks),
    "--collect-data", "buaa_agent", "--collect-data", "tzdata",
    "--copy-metadata", "fastapi", "--collect-submodules", "buaa_agent",
    "--hidden-import", "uvicorn.logging", "--hidden-import", "uvicorn.loops.asyncio",
    "--hidden-import", "uvicorn.protocols.http.h11_impl", "--hidden-import", "uvicorn.lifespan.on",
    "desktop/backend_entry.py",
], check=True)
import playwright  # noqa: E402

browser_source = Path(playwright.__file__).parent / "driver/package/.local-browsers"
browser_target = ROOT / "dist/backend/buaa-backend/_internal/playwright/driver/package/.local-browsers"
from playwright.sync_api import sync_playwright  # noqa: E402

with sync_playwright() as pw:
    executable = Path(pw.chromium.executable_path)
browser_dir = next((parent for parent in executable.parents if parent.parent == browser_source), None)
if browser_dir is None or not executable.is_file():
    raise RuntimeError("Installed Chromium executable was not found in the Playwright browser directory")
if browser_target.exists():
    shutil.rmtree(browser_target)
browser_target.mkdir(parents=True)
# Login only uses Chromium. Do not distribute ffmpeg or stale browser revisions
# from a reused build environment; Playwright does not need them for login.
shutil.copytree(browser_dir, browser_target / browser_dir.name, symlinks=True,
                ignore=shutil.ignore_patterns(".links", "__pycache__"))
licenses = ROOT / "dist/backend/buaa-backend/licenses"
licenses.mkdir(exist_ok=True)
shutil.copy2(ROOT / "desktop/assets/PYTHON-LICENSE.txt", licenses / "PYTHON-LICENSE.txt")
for distribution in distributions():
    for file in distribution.files or []:
        if ".dist-info" not in str(file) or not any(
                word in str(file).lower() for word in ("license", "notice", "copying")):
            continue
        source = distribution.locate_file(file)
        if source.is_file():
            target = licenses / distribution.metadata["Name"] / str(file).split(".dist-info/", 1)[1]
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
print("Frozen backend and Chromium ready.")
