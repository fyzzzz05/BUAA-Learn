"""Frozen desktop service. Never load configuration from the build/source directory."""
import argparse
import os
import socket
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--user-data", type=Path, required=True)
    parser.add_argument("--check-browser", action="store_true")
    args = parser.parse_args()
    root = args.user_data.resolve()
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    config = root / ".env.local"
    if not config.exists():
        config.write_text(
            "# Restart BUAA Learn after editing. System environment takes precedence.\n"
            "BUAA_LLM_BASE_URL=\nBUAA_LLM_MODEL=\nBUAA_LLM_API_KEY=\n"
            "BUAA_BROWSER_CHANNEL=chromium\n", encoding="utf-8")
        config.chmod(0o600)
    from dotenv import load_dotenv

    load_dotenv(config, override=False)
    os.environ.setdefault("BUAA_BROWSER_CHANNEL", "chromium")
    os.environ.setdefault("BUAA_DATA_DIR", str(root / "data"))
    os.environ["PLAYWRIGHT_BROWSERS_PATH"] = "0"
    if args.check_browser:
        from playwright.sync_api import sync_playwright

        with sync_playwright() as pw:
            browser = pw.chromium.launch(headless=True, channel="chromium")
            page = browser.new_page()
            page.set_content("<title>BUAA browser check</title>")
            assert page.title() == "BUAA browser check"
            browser.close()
        print("BUAA_BROWSER_OK", flush=True)
        return
    if len(os.environ.get("BUAA_DESKTOP_TOKEN", "")) < 32:
        raise SystemExit("Desktop launch token is required")
    import uvicorn

    from buaa_agent.app import app

    server = uvicorn.Server(uvicorn.Config(
        app, host="127.0.0.1", access_log=False, log_level="warning",
        loop="asyncio", http="h11", ws="none", timeout_graceful_shutdown=8))
    app.state.desktop_shutdown = lambda: setattr(server, "should_exit", True)
    # Keep the OS-assigned socket open: no port allocation race with other apps.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        print(f"BUAA_PORT={listener.getsockname()[1]}", flush=True)
        try:
            server.run(sockets=[listener])
        finally:
            app.state.auth.logout()
            thread = app.state.auth.thread
            if thread and thread.is_alive():
                thread.join(timeout=4)


if __name__ == "__main__":
    main()
