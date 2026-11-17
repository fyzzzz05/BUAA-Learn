//! Verify a pre-existing SQLite backup by importing it into a new empty database.
//! This deliberately prints counts only, never record contents or account names.
use buaa_core::store::Store;
use rusqlite::{Connection, OpenFlags};
use sha2::{Digest, Sha256};
use std::{env, fs::OpenOptions, path::Path};

fn fingerprint(
    path: &Path,
    query: &str,
    columns: usize,
) -> Result<(u64, Vec<u8>), Box<dyn std::error::Error>> {
    let db = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    let integrity: String = db.query_row("PRAGMA integrity_check", [], |row| row.get(0))?;
    if integrity != "ok" {
        return Err("数据库完整性检查失败".into());
    }
    let mut statement = db.prepare(query)?;
    let mut rows = statement.query([])?;
    let mut hash = Sha256::new();
    let mut count = 0_u64;
    while let Some(row) = rows.next()? {
        for index in 0..columns {
            let value: String = row.get(index)?;
            hash.update((value.len() as u64).to_be_bytes());
            hash.update(value.as_bytes());
        }
        count += 1;
    }
    Ok((count, hash.finalize().to_vec()))
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args_os().skip(1);
    let source = args.next().ok_or("缺少备份数据库路径")?;
    let target = args.next().ok_or("缺少新建目标数据库路径")?;
    if args.next().is_some() {
        return Err("参数过多".into());
    }
    let source = Path::new(&source);
    let target = Path::new(&target);
    #[cfg(unix)]
    use std::os::unix::fs::OpenOptionsExt;
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    options.mode(0o600);
    drop(options.open(target)?);
    let copied = Store::open(target)?.import_legacy(source)?;
    let records = "SELECT owner,kind,id,body,updated FROM records ORDER BY owner,kind,id";
    let settings = "SELECT key,value FROM settings ORDER BY key";
    let before = fingerprint(source, records, 5)?;
    let after = fingerprint(target, records, 5)?;
    let settings_before = fingerprint(source, settings, 2)?;
    let settings_after = fingerprint(target, settings, 2)?;
    if before != after || settings_before != settings_after || before.0 != copied as u64 {
        return Err("导入后的数据库与备份不一致".into());
    }
    println!(
        "记录 {} 条、设置 {} 条，导入前后完整性一致。",
        before.0, settings_before.0
    );
    Ok(())
}
