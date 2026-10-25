import json
import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path


def now():
    return datetime.now(timezone.utc).isoformat()


class Store:
    """One local account per data directory; each row is scoped to that account."""

    def __init__(self, directory: Path):
        self.directory = directory
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        directory.chmod(0o700)
        self.lock = threading.RLock()
        self.db = sqlite3.connect(directory / "learning.sqlite3", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        with self.db:
            self.db.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS records(
              owner TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL,
              body TEXT NOT NULL, updated TEXT NOT NULL,
              PRIMARY KEY(owner,kind,id));
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """)
        (directory / "learning.sqlite3").chmod(0o600)

    def put(self, owner, kind, identifier, body):
        with self.lock, self.db:
            self.db.execute(
                "INSERT INTO records VALUES(?,?,?,?,?) ON CONFLICT(owner,kind,id) "
                "DO UPDATE SET body=excluded.body,updated=excluded.updated",
                (owner, kind, str(identifier), json.dumps(body, ensure_ascii=False), now()),
            )

    def get(self, owner, kind, identifier, default=None):
        with self.lock:
            row = self.db.execute(
                "SELECT body FROM records WHERE owner=? AND kind=? AND id=?",
                (owner, kind, str(identifier)),
            ).fetchone()
        return json.loads(row[0]) if row else default

    def list(self, owner, kind):
        with self.lock:
            rows = self.db.execute(
                "SELECT body FROM records WHERE owner=? AND kind=? ORDER BY updated DESC,id",
                (owner, kind),
            ).fetchall()
        return [json.loads(row[0]) for row in rows]

    def setting(self, key, default=None):
        with self.lock:
            row = self.db.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
        return json.loads(row[0]) if row else default

    def set_setting(self, key, value):
        with self.lock, self.db:
            self.db.execute(
                "INSERT INTO settings VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (key, json.dumps(value, ensure_ascii=False)),
            )

    def close(self):
        self.db.close()
