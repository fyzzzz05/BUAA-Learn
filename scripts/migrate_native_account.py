"""Back up and merge one legacy account into the native SQLite database.

Only additive changes are automatic. Different existing values are retained;
their complete legacy versions remain in the private backup and conflict report.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import stat
import uuid
from datetime import datetime, timezone

SAFE_MERGE_KINDS = {"course", "lesson", "meta"}
EXPORT_PATH = re.compile(r"^[0-9a-f]{24}/slides-[0-9a-f]{24}\.pdf$")


def backup_database(source: Path, destination: Path) -> None:
    with sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True) as reader:
        with sqlite3.connect(destination) as writer:
            reader.backup(writer)
            if writer.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                raise ValueError("数据库备份完整性检查失败")
    destination.chmod(0o600)


def backup_exports(source: Path, destination: Path) -> int:
    if not source.is_dir():
        return 0
    copied = 0
    for path in source.glob("*/*.pdf"):
        relative = path.relative_to(source).as_posix()
        if not EXPORT_PATH.fullmatch(relative) or path.parent.is_symlink():
            raise ValueError("旧版导出目录包含不支持的路径，迁移已停止")
        fd = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
        try:
            if not stat.S_ISREG(os.fstat(fd).st_mode):
                raise ValueError("旧版导出文件不是普通文件")
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            with os.fdopen(fd, "rb", closefd=False) as src, target.open("xb") as dst:
                shutil.copyfileobj(src, dst)
            target.chmod(0o600)
            copied += 1
        finally:
            os.close(fd)
    return copied


def migrate(legacy: Path, native: Path, account_file: Path, backups: Path, exports: Path | None = None) -> dict:
    legacy, native = legacy.resolve(strict=True), native.resolve(strict=True)
    if os.path.samefile(legacy, native):
        raise ValueError("新旧数据库不能是同一个文件")
    account = json.loads(account_file.read_text(encoding="utf-8"))
    if not isinstance(account, str) or not account.strip():
        raise ValueError("当前原生账号无效")
    old_owner = hashlib.sha256(account.encode("utf-8")).hexdigest()[:24]
    backups.mkdir(parents=True, exist_ok=True, mode=0o700)
    backups.chmod(0o700)
    folder = backups / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex[:8])
    folder.mkdir(mode=0o700)
    legacy_copy, native_copy = folder / "legacy.sqlite3", folder / "native-before.sqlite3"
    backup_database(legacy, legacy_copy)
    copied_exports = backup_exports(exports, folder / "legacy-exports") if exports else 0
    lock_fd = os.open(native.parent / "automation.lock", os.O_CREAT | os.O_RDWR, 0o600)
    try:
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc:
            raise RuntimeError("自动化正在运行，请等待当前任务完成后重试") from exc
        with sqlite3.connect(legacy_copy.as_uri() + "?mode=ro", uri=True) as source, sqlite3.connect(native, timeout=5, isolation_level=None) as target:
            target.execute("BEGIN IMMEDIATE")
            try:
                backup_database(native, native_copy)
                rows = source.execute("SELECT kind,id,body,updated FROM records WHERE owner=? ORDER BY kind,id", (old_owner,)).fetchall()
                if not rows:
                    raise ValueError("旧库中没有匹配当前学校账号的数据")
                stats = {"inserted":0, "identical":0, "enriched":0, "conflicts":0, "disabled_automations":0, "exports_backed_up":copied_exports}
                conflicts = []
                for kind, identifier, raw, updated in rows:
                    old = json.loads(raw)
                    present = target.execute("SELECT body FROM records WHERE owner=? AND kind=? AND id=?", (account, kind, identifier)).fetchone()
                    if present is None:
                        if kind == "automation" and isinstance(old, dict) and old.get("enabled"):
                            old["migration_original_enabled"] = True
                            old["migration_original_auto_analyze"] = bool(old.get("auto_analyze"))
                            old["enabled"] = False; old["auto_analyze"] = False; old["next_run"] = None
                            raw = json.dumps(old, ensure_ascii=False)
                            stats["disabled_automations"] += 1
                        target.execute("INSERT INTO records(owner,kind,id,body,updated) VALUES(?,?,?,?,?)", (account, kind, identifier, raw, updated))
                        stats["inserted"] += 1
                        continue
                    current = json.loads(present[0])
                    if current == old:
                        stats["identical"] += 1
                        continue
                    if kind not in SAFE_MERGE_KINDS or not isinstance(current, dict) or not isinstance(old, dict):
                        raise ValueError("存在需要人工处理的笔记、材料或任务冲突；未写入任何记录")
                    merged = dict(current)
                    for key, value in old.items():
                        if kind == "course" and "checked_at" in current and key in {"checked_at", "recording_id", "lesson_count", "recording_count", "sync_error"}:
                            continue
                        if key not in merged:
                            merged[key] = value
                    conflicts.append({"kind":kind, "id":identifier, "fields":sorted(key for key in old.keys() & current.keys() if old[key] != current[key])})
                    stats["conflicts"] += 1
                    if merged != current:
                        target.execute("UPDATE records SET body=? WHERE owner=? AND kind=? AND id=?", (json.dumps(merged, ensure_ascii=False), account, kind, identifier))
                        stats["enriched"] += 1
                report = {"counts":stats, "conflicts":conflicts, "policy":"保留原生值；仅补充旧版独有字段；旧版配置和导出文件保留于备份。"}
                report_path = folder / "report.json"
                report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                report_path.chmod(0o600)
                target.commit()
            except BaseException:
                target.rollback()
                raise
    finally:
        fcntl.flock(lock_fd, fcntl.LOCK_UN)
        os.close(lock_fd)
    return {**stats, "backup_dir":str(folder)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--legacy", required=True, type=Path)
    parser.add_argument("--native", required=True, type=Path)
    parser.add_argument("--account-file", required=True, type=Path)
    parser.add_argument("--backup-root", required=True, type=Path)
    parser.add_argument("--exports", type=Path)
    args = parser.parse_args()
    print(json.dumps(migrate(args.legacy, args.native, args.account_file, args.backup_root, args.exports), ensure_ascii=False))
