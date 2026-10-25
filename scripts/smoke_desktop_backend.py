"""Exercise the distributed executable in an empty account, including its browser."""
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path


def main():
    root = Path(__file__).resolve().parents[1]
    name = "buaa-backend.exe" if sys.platform == "win32" else "buaa-backend"
    executable = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else (
        root / "dist/backend/buaa-backend" / name)
    with tempfile.TemporaryDirectory(prefix="buaa-frozen-test-") as directory:
        env = {k: v for k, v in os.environ.items() if not k.startswith("BUAA_")}
        env["BUAA_DESKTOP_TOKEN"] = secrets.token_hex(32)
        # Configuration must load from user data, not the caller's .env.local.
        (Path(directory) / ".env.local").write_text(
            "BUAA_LLM_MODEL=desktop-smoke-model\nBUAA_BROWSER_CHANNEL=chromium\n", encoding="utf-8")
        subprocess.run([str(executable), "--user-data", directory, "--check-browser"],
                       env=env, check=True, timeout=90)
        process = subprocess.Popen([str(executable), "--user-data", directory], env=env,
                                   stdout=subprocess.PIPE, text=True)
        try:
            # Bound startup reading even when the frozen executable cannot start.
            import concurrent.futures

            with concurrent.futures.ThreadPoolExecutor() as pool:
                address = pool.submit(process.stdout.readline)
                try:
                    line = address.result(timeout=40).strip()
                except TimeoutError:
                    process.kill()
                    raise
            assert line.startswith("BUAA_PORT="), line
            origin = f"http://127.0.0.1:{int(line.split('=')[1])}"

            def request(route, *, authenticated=True, method="GET"):
                headers = {"X-BUAA-Local": "1"}
                if authenticated:
                    headers["X-BUAA-Desktop"] = env["BUAA_DESKTOP_TOKEN"]
                return urllib.request.urlopen(urllib.request.Request(
                    origin + route, headers=headers, method=method), timeout=2)

            for _ in range(100):
                try:
                    with request("/api/desktop/health") as response:
                        assert json.load(response)["status"] == "ready"
                    break
                except (OSError, urllib.error.URLError):
                    time.sleep(.2)
            else:
                raise AssertionError("Frozen service never became ready")
            with request("/api/state") as response:
                state = json.load(response)
                assert state["courses"] == [] and state["model"]["model"] == "desktop-smoke-model"
            with request("/") as response:
                assert b"BUAA" in response.read()
            with request("/app.js") as response:
                assert response.status == 200
            try:
                request("/api/state", authenticated=False)
                raise AssertionError("Desktop data was exposed without token")
            except urllib.error.HTTPError as error:
                assert error.code == 403
            with request("/api/desktop/quit", method="POST") as response:
                assert response.status == 200
            assert process.wait(timeout=20) == 0
            print("BUAA_FROZEN_SERVICE_OK")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)


if __name__ == "__main__":
    main()
