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
if browser_target.exists():
    shutil.rmtree(browser_target)
shutil.copytree(browser_source, browser_target, symlinks=True,
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
