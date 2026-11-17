//! Read-only legacy account snapshots for the Windows migration host.
use crate::store::StoreError;
use rusqlite::{
    backup::{Backup, StepResult},
    Connection, OpenFlags,
};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{path::Path, time::Duration};

fn open(path: &Path, write: bool) -> Result<Connection, StoreError> {
    let flags = if write {
        OpenFlags::SQLITE_OPEN_READ_WRITE
    } else {
        OpenFlags::SQLITE_OPEN_READ_ONLY
    };
    let connection = Connection::open_with_flags(path, flags)?;
    connection.busy_timeout(Duration::from_secs(2))?;
    Ok(connection)
}

pub fn snapshot(source: &Path, destination: &Path, require_idle: bool) -> Result<(), StoreError> {
    let _guard = if require_idle {
        let guard = open(source, true)?;
        guard.execute_batch("BEGIN IMMEDIATE")?;
        Some(guard)
    } else {
        None
    };
    let source = open(source, false)?;
    let file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(destination)?;
    drop(file);
    let result = (|| {
        let mut destination = open(destination, true)?;
        let backup = Backup::new(&source, &mut destination)?;
        if !matches!(backup.step(-1)?, StepResult::Done) {
            return Err(StoreError::UnsafeImport);
        }
        drop(backup);
        let integrity: String =
            destination.query_row("PRAGMA quick_check", [], |row| row.get(0))?;
        if integrity != "ok" {
            return Err(StoreError::UnsupportedSchema);
        }
        Ok(())
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(destination);
    }
    result
}

pub fn rows(source: &Path, owner: &str) -> Result<Value, StoreError> {
    if owner.trim().is_empty() {
        return Err(StoreError::InvalidIdentifier);
    }
    let hash: String = Sha256::digest(owner.as_bytes())[..12]
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect();
    let mut db = open(source, false)?;
    let read = db.transaction()?;
    let other: u64 = read.query_row(
        "SELECT count(*) FROM records WHERE owner != ?1",
        [&hash],
        |row| row.get(0),
    )?;
    let records = {
        let mut statement =
            read.prepare("SELECT kind,id,body FROM records WHERE owner=?1 ORDER BY kind,id")?;
        let mut rows = statement.query([&hash])?;
        let mut records = Vec::new();
        while let Some(row) = rows.next()? {
            let kind: String = row.get(0)?;
            let id: String = row.get(1)?;
            let body: String = row.get(2)?;
            records.push(json!({"kind":kind,"id":id,"body":serde_json::from_str::<Value>(&body)?}));
        }
        records
    };
    Ok(json!({"legacy_owner":hash,"other_account_records":other,"records":records}))
}
