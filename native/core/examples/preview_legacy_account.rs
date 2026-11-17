//! Compare two snapshots using the native account file without printing its value.
use buaa_core::store::Store;
use std::{env, fs::File, path::Path};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args_os().skip(1);
    let legacy = args.next().ok_or("缺少旧版备份")?;
    let native = args.next().ok_or("缺少原生备份")?;
    let account = args.next().ok_or("缺少原生账号记录")?;
    if args.next().is_some() {
        return Err("参数过多".into());
    }
    let owner: String = serde_json::from_reader(File::open(Path::new(&account))?)?;
    let store = Store::open(Path::new(&native))?;
    let report = store.preview_legacy_account(Path::new(&legacy), &owner)?;
    println!(
        "账号匹配记录 {} 条：可新增 {} 条、已有相同 {} 条、内容冲突 {} 条；其他账号记录 {} 条。",
        report.matching_records,
        report.new_records,
        report.identical_records,
        report.conflicting_records,
        report.other_account_records
    );
    for (kind, count) in report.conflicts_by_kind {
        println!("冲突类型 {kind}：{count} 条");
    }
    Ok(())
}
