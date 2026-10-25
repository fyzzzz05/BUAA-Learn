"""Launch the actual packaged Electron app with isolated data and capture its first screen."""
import os
import platform
import subprocess
import sys
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64"
if sys.platform == "darwin":
    folder = "mac-arm64" if arch == "arm64" else "mac"
    executable = root / "dist/desktop" / folder / "BUAA Learn.app/Contents/MacOS/BUAA Learn"
else:
    executable = root / "dist/desktop/win-unpacked/BUAA Learn.exe"
with tempfile.TemporaryDirectory(prefix="buaa-app-test-") as directory:
    env = {k: v for k, v in os.environ.items() if not k.startswith("BUAA_")}
    env.update(BUAA_SMOKE_USER_DATA=directory,
               BUAA_SMOKE_SCREENSHOT=str(root / "dist/desktop/smoke.png"))
    result = subprocess.run([str(executable), "--smoke-test", "--disable-gpu"],
                            env=env, capture_output=True, text=True, timeout=100)
    print(result.stdout)
    if result.returncode or "BUAA_DESKTOP_OK" not in result.stdout:
        print(result.stderr)
        log = Path(directory) / "desktop.log"
        if log.exists():
            print(log.read_text(encoding="utf-8"))
        raise SystemExit("Packaged desktop smoke test failed")
