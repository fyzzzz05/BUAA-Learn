import hashlib
import json
import sqlite3

import pytest

from scripts.migrate_native_account import migrate


def database(path, rows):
    with sqlite3.connect(path) as db:
        db.executescript("CREATE TABLE records(owner TEXT,kind TEXT,id TEXT,body TEXT,updated TEXT,PRIMARY KEY(owner,kind,id)); CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT);")
        db.executemany("INSERT INTO records VALUES(?,?,?,?,?)", [(owner, kind, key, json.dumps(body), "now") for owner, kind, key, body in rows])


def test_migration_maps_account_preserves_current_and_backups(tmp_path):
    account = "test-account"
    legacy_owner = hashlib.sha256(account.encode()).hexdigest()[:24]
    legacy, native = tmp_path / "old.sqlite", tmp_path / "new.sqlite"
    database(legacy, [(legacy_owner, "course", "c", {"name":"old", "recording_id":"42"}), (legacy_owner, "lesson", "l", {"title":"old"}), ("other", "lesson", "x", {})])
    database(native, [(account, "course", "c", {"name":"current"})])
    account_file = tmp_path / "account.json"; account_file.write_text(json.dumps(account))
    result = migrate(legacy, native, account_file, tmp_path / "backups")
    assert (result["inserted"], result["conflicts"], result["enriched"]) == (1, 1, 1)
    with sqlite3.connect(native) as db:
        course = json.loads(db.execute("SELECT body FROM records WHERE owner=? AND kind='course'", (account,)).fetchone()[0])
        assert course == {"name":"current", "recording_id":"42"}
        assert db.execute("SELECT count(*) FROM records WHERE owner=? AND kind='lesson'", (account,)).fetchone()[0] == 1
        assert db.execute("SELECT count(*) FROM records WHERE id='x'").fetchone()[0] == 0
    assert (tmp_path / "backups" / result["backup_dir"].split("/")[-1] / "legacy.sqlite3").exists()
    again = migrate(legacy, native, account_file, tmp_path / "backups")
    assert again["inserted"] == 0


def test_sensitive_conflict_rolls_back_every_record(tmp_path):
    account = "test-account"; owner = hashlib.sha256(account.encode()).hexdigest()[:24]
    legacy, native = tmp_path / "old.sqlite", tmp_path / "new.sqlite"
    database(legacy, [(owner,"course","new",{"name":"new"}), (owner,"materials","l",{"text":"old"})])
    database(native, [(account,"materials","l",{"text":"current"})])
    account_file = tmp_path / "account.json"; account_file.write_text(json.dumps(account))
    with pytest.raises(ValueError, match="未写入任何记录"):
        migrate(legacy, native, account_file, tmp_path / "backups")
    with sqlite3.connect(native) as db:
        assert db.execute("SELECT count(*) FROM records WHERE kind='course'").fetchone()[0] == 0
