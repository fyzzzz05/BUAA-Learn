import argparse
from pathlib import Path

import uvicorn
from dotenv import load_dotenv


def load_local_environment():
    # Prefer the launch directory; fall back to the source project directory.
    local = Path.cwd() / ".env.local"
    if not local.is_file():
        local = Path(__file__).resolve().parent.parent / ".env.local"
    load_dotenv(local, override=False)


def main():
    load_local_environment()
    parser = argparse.ArgumentParser(description="BUAA Learning Agent — local Web application")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    uvicorn.run("buaa_agent.app:app", host="127.0.0.1", port=args.port, access_log=False)


if __name__ == "__main__":
    main()
