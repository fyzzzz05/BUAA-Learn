//! Rehearse merging two SQLite snapshots into a newly created third file.
use buaa_core::store::Store;
use std::{
    env,
    fs::{File, OpenOptions},
    io,
    path::Path,
};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args_os().skip(1);
    let legacy = args.next().ok_or("缺少旧版数据库备份")?;
    let native = args.next().ok_or("缺少原生数据库备份")?;
    let target = args.next().ok_or("缺少新建演练目标")?;
    if args.next().is_some() {
        return Err("参数过多".into());
    }
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut destination = options.open(Path::new(&target))?;
    io::copy(&mut File::open(Path::new(&native))?, &mut destination)?;
    destination.sync_all()?;
    drop(destination);
    let report = Store::open(Path::new(&target))?.merge_legacy(Path::new(&legacy))?;
    println!(
        "新增记录 {} 条，已有相同记录 {} 条；新增设置 {} 条，已有相同设置 {} 条。",
        report.records_inserted,
        report.records_unchanged,
        report.settings_inserted,
        report.settings_unchanged
    );
    Ok(())
}
