//! Account-scoped local records. Platform code owns the database path and secrets.
use std::path::Path;

use rusqlite::{params, Connection, OpenFlags, OptionalExtension};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum StoreError {
    #[error("本地数据操作失败")]
    Database(#[from] rusqlite::Error),
    #[error("本地数据格式异常")]
    InvalidJson(#[from] serde_json::Error),
    #[error("账号或记录标识为空")]
    InvalidIdentifier,
    #[error("不能将旧数据库导入自身或非空数据库")]
    UnsafeImport,
    #[error("旧数据与当前数据存在冲突，未导入任何记录")]
    MergeConflict,
    #[error("数据库路径不可用")]
    InvalidPath,
    #[error("无法创建本地数据库")]
    Io(#[from] std::io::Error),
    #[error("未知的记录类型")]
    InvalidKind,
    #[error("作业不存在或已被替代，请刷新列表")]
    InvalidTask,
    #[error("课堂分析或课程归属无效")]
    InvalidAnalysis,
    #[error("课堂材料已变化，请重新生成学习笔记")]
    MaterialsChanged,
    #[error("本地数据库版本或结构不受支持，请保留数据并更新应用")]
    UnsupportedSchema,
}

#[derive(Debug, Clone, Copy)]
pub enum RecordKind {
    Course,
    Lesson,
    Material,
    Analysis,
    Task,
    Automation,
    Job,
    Meta,
    Chat,
    SlidesPdf,
}
impl RecordKind {
    pub fn parse(value: &str) -> Result<Self, StoreError> {
        match value {
            "course" => Ok(Self::Course),
            "lesson" => Ok(Self::Lesson),
            "materials" => Ok(Self::Material),
            "analysis" => Ok(Self::Analysis),
            "task" => Ok(Self::Task),
            "automation" => Ok(Self::Automation),
            "job" => Ok(Self::Job),
            "meta" => Ok(Self::Meta),
            "chat" => Ok(Self::Chat),
            "slides_pdf" => Ok(Self::SlidesPdf),
            _ => Err(StoreError::InvalidKind),
        }
    }
    fn key(self) -> &'static str {
        match self {
            Self::Course => "course",
            Self::Lesson => "lesson",
            Self::Material => "materials",
            Self::Analysis => "analysis",
            Self::Task => "task",
            Self::Automation => "automation",
            Self::Job => "job",
            Self::Meta => "meta",
            Self::Chat => "chat",
            Self::SlidesPdf => "slides_pdf",
        }
    }
}

pub struct Store {
    db: Connection,
}

#[derive(Debug, Default, Clone, PartialEq, Eq, serde::Serialize)]
pub struct MergeReport {
    pub records_inserted: usize,
    pub records_unchanged: usize,
    pub settings_inserted: usize,
    pub settings_unchanged: usize,
}

#[derive(Debug, Default, Clone, PartialEq, Eq, serde::Serialize)]
pub struct LegacyAccountPreview {
    pub matching_records: usize,
    pub new_records: usize,
    pub identical_records: usize,
    pub conflicting_records: usize,
    pub other_account_records: usize,
    pub conflicts_by_kind: std::collections::BTreeMap<String, usize>,
}
impl Store {
    pub const WINDOWS_SCHEMA_VERSION: u32 = 1;
    const WINDOWS_APPLICATION_ID: i64 = 0x42554141;

    /// Windows workspace initialization. Existing unversioned native stores are
    /// backed up (including WAL contents) before any schema/version change.
    /// Hosts must hold their workspace lease and supply an unused backup path.
    pub fn open_versioned(path: &Path, backup: &Path) -> Result<(Self, bool), StoreError> {
        if path == backup || backup.exists() {
            return Err(StoreError::InvalidPath);
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .mode(0o600)
                .open(path)?;
        }
        let mut db = Connection::open(path)?;
        db.busy_timeout(std::time::Duration::from_secs(5))?;
        let version: u32 = db.pragma_query_value(None, "user_version", |r| r.get(0))?;
        let application: i64 = db.pragma_query_value(None, "application_id", |r| r.get(0))?;
        if version > Self::WINDOWS_SCHEMA_VERSION
            || (application != 0 && application != Self::WINDOWS_APPLICATION_ID)
            || (version != 0 && application != Self::WINDOWS_APPLICATION_ID)
        {
            return Err(StoreError::UnsupportedSchema);
        }
        let tables: Vec<String> = {
            let mut query = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")?;
            let rows = query.query_map([], |r| r.get(0))?;
            rows.collect::<Result<_, _>>()?
        };
        let existing = !tables.is_empty();
        if existing {
            if tables != ["records", "settings"] {
                return Err(StoreError::UnsupportedSchema);
            }
            for (table, expected) in [
                (
                    "records",
                    vec![
                        ("owner", 1, 1),
                        ("kind", 1, 2),
                        ("id", 1, 3),
                        ("body", 1, 0),
                        ("updated", 1, 0),
                    ],
                ),
                ("settings", vec![("key", 0, 1), ("value", 1, 0)]),
            ] {
                let mut query = db.prepare(&format!("PRAGMA table_info({table})"))?;
                let rows = query.query_map([], |r| {
                    Ok((
                        r.get::<_, String>(1)?,
                        r.get::<_, String>(2)?,
                        r.get::<_, i32>(3)?,
                        r.get::<_, i32>(5)?,
                    ))
                })?;
                let columns = rows.collect::<Result<Vec<_>, _>>()?;
                if columns.len() != expected.len()
                    || columns.iter().zip(expected).any(
                        |((name, ty, required, key), (want, req, pk))| {
                            name != want
                                || !ty.eq_ignore_ascii_case("TEXT")
                                || *required != req
                                || *key != pk
                        },
                    )
                {
                    return Err(StoreError::UnsupportedSchema);
                }
            }
        } else if version != 0 || application != 0 {
            return Err(StoreError::UnsupportedSchema);
        }
        let backed_up = existing && version < Self::WINDOWS_SCHEMA_VERSION;
        if backed_up {
            let target = backup.to_str().ok_or(StoreError::InvalidPath)?;
            #[cfg(unix)]
            {
                use std::os::unix::fs::OpenOptionsExt;
                std::fs::OpenOptions::new()
                    .create_new(true)
                    .write(true)
                    .mode(0o600)
                    .open(backup)?;
            }
            #[cfg(not(unix))]
            std::fs::OpenOptions::new()
                .create_new(true)
                .write(true)
                .open(backup)?;
            if let Err(error) = db.execute("VACUUM INTO ?1", [target]) {
                let _ = std::fs::remove_file(backup);
                return Err(StoreError::Database(error));
            }
        }
        if version < Self::WINDOWS_SCHEMA_VERSION {
            let tx = db.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
            tx.execute_batch("CREATE TABLE IF NOT EXISTS records(owner TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, updated TEXT NOT NULL, PRIMARY KEY(owner,kind,id)); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);")?;
            tx.pragma_update(None, "application_id", Self::WINDOWS_APPLICATION_ID)?;
            tx.pragma_update(None, "user_version", Self::WINDOWS_SCHEMA_VERSION)?;
            tx.commit()?;
        }
        db.pragma_update(None, "journal_mode", "WAL")?;
        Ok((Self { db }, backed_up))
    }

    pub fn get_setting(&self, key: &str) -> Result<Value, StoreError> {
        if key.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let raw: Option<String> = self
            .db
            .query_row("SELECT value FROM settings WHERE key=?1", [key], |r| {
                r.get(0)
            })
            .optional()?;
        raw.map(|v| serde_json::from_str(&v))
            .transpose()
            .map(|v| v.unwrap_or(Value::Null))
            .map_err(StoreError::from)
    }

    pub fn set_setting(&self, key: &str, body: &Value) -> Result<(), StoreError> {
        if key.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        self.db.execute("INSERT INTO settings(key,value) VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value", params![key, serde_json::to_string(body)?])?;
        Ok(())
    }

    /// Atomic same-account batch, used for interrupting queued jobs on recovery.
    pub fn put_batch(&mut self, owner: &str, records: &[Value]) -> Result<(), StoreError> {
        if owner.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let tx = self
            .db
            .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        for row in records {
            let kind = RecordKind::parse(
                row.get("kind")
                    .and_then(Value::as_str)
                    .ok_or(StoreError::InvalidKind)?,
            )?;
            let id = row
                .get("id")
                .and_then(Value::as_str)
                .filter(|v| !v.trim().is_empty())
                .ok_or(StoreError::InvalidIdentifier)?;
            let body = row.get("body").ok_or(StoreError::InvalidIdentifier)?;
            tx.execute("INSERT INTO records(owner,kind,id,body,updated) VALUES(?1,?2,?3,?4,strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(owner,kind,id) DO UPDATE SET body=excluded.body,updated=excluded.updated", params![owner,kind.key(),id,serde_json::to_string(body)?])?;
        }
        tx.commit()?;
        Ok(())
    }

    /// Open a new native database, or a database already using this exact schema.
    /// Do not point this at the legacy database during migration.
    pub fn open(path: &Path) -> Result<Self, StoreError> {
        #[cfg(unix)]
        if path != Path::new(":memory:") {
            use std::os::unix::fs::OpenOptionsExt;
            // SQLite's first create must respect private per-user permissions.
            std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .mode(0o600)
                .open(path)?;
        }
        let db = Connection::open(path)?;
        db.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS records(
              owner TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL,
              body TEXT NOT NULL, updated TEXT NOT NULL,
              PRIMARY KEY(owner, kind, id));
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);",
        )?;
        Ok(Self { db })
    }

    pub fn put(
        &mut self,
        owner: &str,
        kind: RecordKind,
        id: &str,
        body: &Value,
    ) -> Result<(), StoreError> {
        if owner.trim().is_empty() || id.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        self.db.execute("INSERT INTO records(owner,kind,id,body,updated) VALUES (?1,?2,?3,?4,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(owner,kind,id) DO UPDATE SET body=excluded.body,updated=excluded.updated",
            params![owner, kind.key(), id, serde_json::to_string(body)?])?;
        Ok(())
    }

    /// Merge synchronized school records in one transaction without discarding local fields.
    pub fn merge_school_records(
        &mut self,
        owner: &str,
        records: &[Value],
    ) -> Result<(), StoreError> {
        if owner.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let tx = self.db.transaction()?;
        let mut seen = HashSet::new();
        for record in records {
            let kind = record
                .get("kind")
                .and_then(Value::as_str)
                .ok_or(StoreError::InvalidKind)?;
            if !["course", "lesson", "meta"].contains(&kind) {
                return Err(StoreError::InvalidKind);
            }
            let id = record
                .get("id")
                .and_then(Value::as_str)
                .filter(|v| !v.trim().is_empty())
                .ok_or(StoreError::InvalidIdentifier)?;
            if !seen.insert((kind, id)) {
                return Err(StoreError::InvalidIdentifier);
            }
            let incoming = record
                .get("body")
                .and_then(Value::as_object)
                .ok_or(StoreError::InvalidAnalysis)?;
            let previous: Option<String> = tx
                .query_row(
                    "SELECT body FROM records WHERE owner=?1 AND kind=?2 AND id=?3",
                    params![owner, kind, id],
                    |row| row.get(0),
                )
                .optional()?;
            let mut merged = match previous {
                Some(body) => serde_json::from_str::<Value>(&body)?
                    .as_object()
                    .cloned()
                    .ok_or(StoreError::InvalidAnalysis)?,
                None => serde_json::Map::new(),
            };
            if kind == "lesson" {
                if let (Some(old), Some(new)) = (
                    merged.get("course_id").and_then(Value::as_str),
                    incoming.get("course_id").and_then(Value::as_str),
                ) {
                    if old != new {
                        return Err(StoreError::InvalidAnalysis);
                    }
                }
            }
            merged.extend(incoming.clone());
            tx.execute("INSERT INTO records(owner,kind,id,body,updated) VALUES (?1,?2,?3,?4,strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(owner,kind,id) DO UPDATE SET body=excluded.body,updated=excluded.updated", params![owner,kind,id,serde_json::to_string(&merged)?])?;
        }
        tx.commit()?;
        Ok(())
    }

    pub fn get(
        &self,
        owner: &str,
        kind: RecordKind,
        id: &str,
    ) -> Result<Option<Value>, StoreError> {
        if owner.trim().is_empty() || id.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let body: Option<String> = self
            .db
            .query_row(
                "SELECT body FROM records WHERE owner=?1 AND kind=?2 AND id=?3",
                params![owner, kind.key(), id],
                |row| row.get(0),
            )
            .optional()?;
        body.map(|body| serde_json::from_str(&body).map_err(StoreError::from))
            .transpose()
    }

    pub fn list(&self, owner: &str, kind: RecordKind) -> Result<Vec<Value>, StoreError> {
        if owner.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let mut stmt = self.db.prepare(
            "SELECT body FROM records WHERE owner=?1 AND kind=?2 ORDER BY updated DESC,id",
        )?;
        let rows = stmt.query_map(params![owner, kind.key()], |row| row.get::<_, String>(0))?;
        rows.map(|row| Ok(serde_json::from_str(&row?)?)).collect()
    }

    /// Save a complete material snapshot and its lesson status together. Empty,
    /// mismatched or tampered snapshots never replace a previously valid one.
    pub fn save_materials(
        &mut self,
        owner: &str,
        course: &str,
        lesson: &str,
        material: &Value,
    ) -> Result<(), StoreError> {
        if [owner, course, lesson].iter().any(|v| v.trim().is_empty()) {
            return Err(StoreError::InvalidIdentifier);
        }
        if material.get("lesson_id").and_then(Value::as_str) != Some(lesson)
            || material.get("course_id").and_then(Value::as_str) != Some(course)
        {
            return Err(StoreError::InvalidAnalysis);
        }
        let segments: Vec<crate::materials::Segment> = serde_json::from_value(
            material
                .get("segments")
                .cloned()
                .ok_or(StoreError::InvalidAnalysis)?,
        )?;
        let slides: Vec<crate::materials::Slide> = serde_json::from_value(
            material
                .get("slides")
                .cloned()
                .ok_or(StoreError::InvalidAnalysis)?,
        )?;
        if segments.is_empty() && slides.is_empty() {
            return Err(StoreError::InvalidAnalysis);
        }
        let mut ids = HashSet::new();
        for (i, row) in segments.iter().enumerate() {
            if row.id != format!("s{i}")
                || row.text.trim().is_empty()
                || !row.start.is_finite()
                || !row.end.is_finite()
                || row.start < 0.0
                || row.end < row.start
                || !ids.insert(&row.id)
            {
                return Err(StoreError::InvalidAnalysis);
            }
        }
        for row in &slides {
            if row.id.is_empty()
                || !row.start.is_finite()
                || row.start < 0.0
                || crate::materials::clean_school_url(&row.url).as_deref() != Some(row.url.as_str())
                || !ids.insert(&row.id)
            {
                return Err(StoreError::InvalidAnalysis);
            }
        }
        let duration = material
            .get("duration")
            .and_then(Value::as_f64)
            .filter(|v| v.is_finite() && *v >= 0.0)
            .ok_or(StoreError::InvalidAnalysis)?;
        let fetched = material
            .get("fetched_at")
            .and_then(Value::as_str)
            .ok_or(StoreError::InvalidAnalysis)?;
        chrono::DateTime::parse_from_rfc3339(fetched).map_err(|_| StoreError::InvalidAnalysis)?;
        let fingerprint =
            serde_json::to_vec(&serde_json::json!({"segments":segments,"slides":slides}))?;
        let digest = format!("{:x}", Sha256::digest(fingerprint));
        if material.get("digest").and_then(Value::as_str) != Some(digest.as_str()) {
            return Err(StoreError::MaterialsChanged);
        }
        let coverage = crate::materials::coverage(&segments, duration)
            .map_err(|_| StoreError::InvalidAnalysis)?;
        let mut stored = serde_json::json!({"lesson_id":lesson,"course_id":course,"segments":segments,"slides":slides,"duration":duration,"coverage":coverage,"digest":digest,"fetched_at":fetched});
        // Only normalized fields cross the durable boundary, never arbitrary host metadata.
        if let Some(rows) = material.get("videos") {
            let mut videos = Vec::new();
            for row in rows.as_array().ok_or(StoreError::InvalidAnalysis)? {
                let url = row
                    .get("url")
                    .and_then(Value::as_str)
                    .ok_or(StoreError::InvalidAnalysis)?;
                if crate::materials::clean_school_url(url).as_deref() != Some(url) {
                    return Err(StoreError::InvalidAnalysis);
                }
                let kind = row
                    .get("type")
                    .and_then(Value::as_str)
                    .filter(|v| ["PPT", "教师"].contains(v))
                    .ok_or(StoreError::InvalidAnalysis)?;
                let duration = row
                    .get("duration")
                    .and_then(Value::as_f64)
                    .filter(|v| v.is_finite() && *v >= 0.0)
                    .ok_or(StoreError::InvalidAnalysis)?;
                videos.push(serde_json::json!({"url":url,"type":kind,"duration":duration}));
            }
            stored["videos"] = serde_json::json!(videos);
        }
        if let Some(rows) = material.get("limitations") {
            if !rows
                .as_array()
                .ok_or(StoreError::InvalidAnalysis)?
                .iter()
                .all(Value::is_string)
            {
                return Err(StoreError::InvalidAnalysis);
            }
            stored["limitations"] = rows.clone();
        }
        let tx = self
            .db
            .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
        let raw: Option<String> = tx
            .query_row(
                "SELECT body FROM records WHERE owner=?1 AND kind='lesson' AND id=?2",
                params![owner, lesson],
                |r| r.get(0),
            )
            .optional()?;
        let mut row: Value = serde_json::from_str(&raw.ok_or(StoreError::InvalidAnalysis)?)?;
        let parent_exists: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM records WHERE owner=?1 AND kind='course' AND id=?2)",
            params![owner, course],
            |r| r.get(0),
        )?;
        if !parent_exists
            || row.get("course_id").and_then(Value::as_str) != Some(course)
            || row.get("has_recording").and_then(Value::as_bool) != Some(true)
            || row.get("recording_course_id").and_then(Value::as_str)
                != material.get("recording_course_id").and_then(Value::as_str)
            || row
                .get("recording_course_id")
                .and_then(Value::as_str)
                .filter(|v| !v.is_empty())
                .is_none()
        {
            return Err(StoreError::InvalidAnalysis);
        }
        stored["recording_course_id"] = material["recording_course_id"].clone();
        row["materials_at"] = Value::String(fetched.into());
        row["materials_digest"] = Value::String(digest);
        row["segment_count"] = serde_json::json!(segments.len());
        row["slide_count"] = serde_json::json!(slides.len());
        for (kind, body) in [("materials", stored), ("lesson", row)] {
            tx.execute("INSERT INTO records(owner,kind,id,body,updated) VALUES(?1,?2,?3,?4,strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(owner,kind,id) DO UPDATE SET body=excluded.body,updated=excluded.updated",params![owner,kind,lesson,serde_json::to_string(&body)?])?;
        }
        tx.commit()?;
        Ok(())
    }

    /// Atomically save a validated analysis, reconcile its tasks, and mark the lesson analyzed.
    pub fn save_analysis(
        &mut self,
        owner: &str,
        analysis: &Value,
    ) -> Result<Vec<Value>, StoreError> {
        self.save_analysis_checked(owner, analysis, None)
    }

    /// Project notices without copying every lesson transcript across the ABI.
    pub fn list_notice_groups(&self, owner: &str) -> Result<Vec<Value>, StoreError> {
        let mut statement = self.db.prepare("SELECT body FROM records WHERE owner=?1 AND kind='analysis' AND json_type(body,'$.notices')='array' AND json_array_length(body,'$.notices')>0 ORDER BY updated DESC, id")?;
        let bodies = statement.query_map([owner], |row| row.get::<_, String>(0))?;
        let mut result = Vec::new();
        for body in bodies {
            let analysis: Value = serde_json::from_str(&body?)?;
            let canonical = serde_json::to_string(&analysis)?;
            let version: String = Sha256::digest(canonical.as_bytes())
                .iter()
                .map(|b| format!("{b:02x}"))
                .collect();
            result.push(serde_json::json!({
                "course_id":analysis["course_id"], "course_name":analysis["course_name"],
                "lesson_id":analysis["lesson_id"], "lesson_title":analysis["lesson_title"],
                "notices":analysis["notices"], "source_version":version
            }));
        }
        Ok(result)
    }

    /// Update only completion inside a transaction; never overwrite a newer
    /// analysis' task fields with a host's stale read/modify/write snapshot.
    pub fn set_task_done(
        &mut self,
        owner: &str,
        id: &str,
        done: bool,
    ) -> Result<Value, StoreError> {
        if owner.trim().is_empty() || id.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let tx = self.db.transaction()?;
        let body: Option<String> = tx
            .query_row(
                "SELECT body FROM records WHERE owner=?1 AND kind='task' AND id=?2",
                params![owner, id],
                |row| row.get(0),
            )
            .optional()?;
        let mut task: Value = serde_json::from_str(&body.ok_or(StoreError::InvalidTask)?)?;
        if task.get("superseded").and_then(Value::as_bool) == Some(true) {
            return Err(StoreError::InvalidTask);
        }
        task.as_object_mut()
            .ok_or(StoreError::InvalidTask)?
            .insert("done".into(), Value::Bool(done));
        tx.execute("UPDATE records SET body=?3,updated=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE owner=?1 AND kind='task' AND id=?2", params![owner,id,serde_json::to_string(&task)?])?;
        tx.commit()?;
        Ok(task)
    }

    pub fn save_analysis_checked(
        &mut self,
        owner: &str,
        analysis: &Value,
        expected_digest: Option<&str>,
    ) -> Result<Vec<Value>, StoreError> {
        if owner.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let required = |key: &str| {
            analysis
                .get(key)
                .and_then(Value::as_str)
                .filter(|v| !v.trim().is_empty())
                .ok_or(StoreError::InvalidAnalysis)
        };
        let lesson_id = required("lesson_id")?;
        let course_id = required("course_id")?;
        let course_name = required("course_name")?;
        let lesson_title = required("lesson_title")?;
        let analyzed_at = required("created_at")?;
        let assignments = analysis
            .get("assignments")
            .and_then(Value::as_array)
            .ok_or(StoreError::InvalidAnalysis)?;
        if analysis.get("sections").and_then(Value::as_array).is_none()
            || analysis
                .get("evidence")
                .and_then(Value::as_object)
                .is_none()
        {
            return Err(StoreError::InvalidAnalysis);
        }
        let tx = self.db.transaction()?;
        if let Some(expected) = expected_digest {
            if expected.is_empty()
                || analysis.get("digest").and_then(Value::as_str) != Some(expected)
            {
                return Err(StoreError::MaterialsChanged);
            }
            let material: Option<String> = tx
                .query_row(
                    "SELECT body FROM records WHERE owner=?1 AND kind='materials' AND id=?2",
                    params![owner, lesson_id],
                    |row| row.get(0),
                )
                .optional()?;
            let current = material
                .as_deref()
                .map(serde_json::from_str::<Value>)
                .transpose()?;
            if current
                .as_ref()
                .and_then(|value| value.get("digest"))
                .and_then(Value::as_str)
                != Some(expected)
            {
                return Err(StoreError::MaterialsChanged);
            }
        }
        let lesson: Option<String> = tx
            .query_row(
                "SELECT body FROM records WHERE owner=?1 AND kind='lesson' AND id=?2",
                params![owner, lesson_id],
                |row| row.get(0),
            )
            .optional()?;
        let course_exists: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM records WHERE owner=?1 AND kind='course' AND id=?2)",
            params![owner, course_id],
            |row| row.get(0),
        )?;
        let mut lesson: Value = serde_json::from_str(&lesson.ok_or(StoreError::InvalidAnalysis)?)?;
        if !course_exists || lesson.get("course_id").and_then(Value::as_str) != Some(course_id) {
            return Err(StoreError::InvalidAnalysis);
        }
        let mut previous = HashMap::new();
        {
            let mut statement =
                tx.prepare("SELECT id,body FROM records WHERE owner=?1 AND kind='task'")?;
            let rows = statement.query_map([owner], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })?;
            for row in rows {
                let (id, body) = row?;
                previous.insert(id, serde_json::from_str::<Value>(&body)?);
            }
        }
        // One immutable original-text snapshot per analysis, shared by all its
        // tasks. Superseded tasks keep their original snapshot after reanalysis.
        fn snapshot(
            tx: &rusqlite::Transaction<'_>,
            owner: &str,
            analysis: &Value,
        ) -> Result<String, StoreError> {
            let encoded = serde_json::to_string(analysis)?;
            let hash: String = Sha256::digest(encoded.as_bytes())
                .iter()
                .map(|b| format!("{b:02x}"))
                .collect();
            let id = format!("task-source:{hash}");
            let body = serde_json::json!({"lesson_id":analysis["lesson_id"],"created_at":analysis["created_at"],"evidence":analysis["evidence"]});
            write(tx, owner, "meta", &id, &body)?;
            Ok(id)
        }
        let new_source = if assignments.is_empty() {
            None
        } else {
            Some(snapshot(&tx, owner, analysis)?)
        };
        let old_analysis: Option<String> = tx
            .query_row(
                "SELECT body FROM records WHERE owner=?1 AND kind='analysis' AND id=?2",
                params![owner, lesson_id],
                |row| row.get(0),
            )
            .optional()?;
        if let Some(old) = old_analysis {
            let old: Value = serde_json::from_str(&old)?;
            if old.get("evidence").is_some_and(Value::is_object) {
                let mut old_source: Option<String> = None;
                for task in previous.values_mut() {
                    if task.get("lesson_id").and_then(Value::as_str) == Some(lesson_id)
                        && task.get("superseded").and_then(Value::as_bool) != Some(true)
                        && task.get("source_snapshot").is_none()
                        && old
                            .get("assignments")
                            .and_then(Value::as_array)
                            .is_some_and(|rows| {
                                rows.iter().any(|row| {
                                    row.get("text") == task.get("text")
                                        && row.get("evidence_ids") == task.get("evidence_ids")
                                })
                            })
                    {
                        let source = match &old_source {
                            Some(id) => id.clone(),
                            None => {
                                let id = snapshot(&tx, owner, &old)?;
                                old_source = Some(id.clone());
                                id
                            }
                        };
                        task.as_object_mut()
                            .ok_or(StoreError::InvalidTask)?
                            .insert("source_snapshot".into(), Value::String(source));
                    }
                }
            }
        }
        let mut active = HashSet::new();
        let mut tasks = Vec::new();
        for assignment in assignments {
            let mut task = assignment
                .as_object()
                .cloned()
                .ok_or(StoreError::InvalidAnalysis)?;
            let text = task
                .get("text")
                .and_then(Value::as_str)
                .filter(|v| !v.trim().is_empty())
                .ok_or(StoreError::InvalidAnalysis)?;
            let refs = task
                .get("evidence_ids")
                .and_then(Value::as_array)
                .filter(|v| !v.is_empty())
                .ok_or(StoreError::InvalidAnalysis)?;
            let evidence = analysis["evidence"]
                .as_object()
                .ok_or(StoreError::InvalidAnalysis)?;
            if refs
                .iter()
                .any(|id| id.as_str().is_none_or(|id| !evidence.contains_key(id)))
            {
                return Err(StoreError::InvalidAnalysis);
            }
            // Preserve an existing identity only when both lesson and text
            // match. Frame new identities so ("ab","c") != ("a","bc").
            let digest = Sha256::digest(serde_json::to_vec(&(lesson_id, text))?);
            let generated: String = digest[..12]
                .iter()
                .map(|byte| format!("{byte:02x}"))
                .collect();
            let id = previous
                .iter()
                .filter(|(_, old)| {
                    old.get("lesson_id").and_then(Value::as_str) == Some(lesson_id)
                        && old.get("text").and_then(Value::as_str) == Some(text)
                })
                .min_by_key(|(id, old)| {
                    (
                        old.get("superseded").and_then(Value::as_bool) == Some(true),
                        *id,
                    )
                })
                .map(|(id, _)| id.clone())
                .unwrap_or(generated);
            if !active.insert(id.clone()) {
                continue;
            }
            let done = previous
                .get(&id)
                .and_then(|old| old.get("done"))
                .and_then(Value::as_bool)
                .unwrap_or(false);
            task.insert("id".into(), Value::String(id));
            task.insert("lesson_id".into(), Value::String(lesson_id.into()));
            task.insert("course_id".into(), Value::String(course_id.into()));
            task.insert("course_name".into(), Value::String(course_name.into()));
            task.insert("lesson_title".into(), Value::String(lesson_title.into()));
            task.insert("done".into(), Value::Bool(done));
            task.insert("superseded".into(), Value::Bool(false));
            task.insert(
                "source_snapshot".into(),
                Value::String(new_source.clone().ok_or(StoreError::InvalidAnalysis)?),
            );
            tasks.push(Value::Object(task));
        }
        fn write(
            tx: &rusqlite::Transaction<'_>,
            owner: &str,
            kind: &str,
            id: &str,
            body: &Value,
        ) -> Result<(), StoreError> {
            tx.execute("INSERT INTO records(owner,kind,id,body,updated) VALUES (?1,?2,?3,?4,strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(owner,kind,id) DO UPDATE SET body=excluded.body,updated=excluded.updated",params![owner,kind,id,serde_json::to_string(body)?])?;
            Ok(())
        }
        for (id, mut old) in previous {
            if old.get("lesson_id").and_then(Value::as_str) == Some(lesson_id)
                && !active.contains(&id)
            {
                old.as_object_mut()
                    .ok_or(StoreError::InvalidAnalysis)?
                    .insert("superseded".into(), Value::Bool(true));
                write(&tx, owner, "task", &id, &old)?;
            }
        }
        for task in &tasks {
            write(
                &tx,
                owner,
                "task",
                task["id"].as_str().ok_or(StoreError::InvalidAnalysis)?,
                task,
            )?;
        }
        lesson
            .as_object_mut()
            .ok_or(StoreError::InvalidAnalysis)?
            .insert("analysis_at".into(), Value::String(analyzed_at.into()));
        write(&tx, owner, "analysis", lesson_id, analysis)?;
        write(&tx, owner, "lesson", lesson_id, &lesson)?;
        tx.commit()?;
        Ok(tasks)
    }

    /// Copy an old database into an empty native database. The source is opened
    /// read-only; malformed rows or conflicts roll back the entire destination.
    /// The caller is responsible for backing up the old database before calling.
    pub fn import_legacy(&mut self, source: &Path) -> Result<usize, StoreError> {
        let destination = self.db.path().ok_or(StoreError::InvalidPath)?;
        let source_path = source.canonicalize().map_err(|_| StoreError::InvalidPath)?;
        let destination_path = Path::new(destination)
            .canonicalize()
            .map_err(|_| StoreError::InvalidPath)?;
        if source_path == destination_path {
            return Err(StoreError::UnsafeImport);
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            let left = source_path
                .metadata()
                .map_err(|_| StoreError::InvalidPath)?;
            let right = destination_path
                .metadata()
                .map_err(|_| StoreError::InvalidPath)?;
            if left.dev() == right.dev() && left.ino() == right.ino() {
                return Err(StoreError::UnsafeImport);
            }
        }
        let mut old = Connection::open_with_flags(source_path, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        let snapshot = old.transaction()?;
        let target = self.db.transaction()?;
        let occupied: i64 = target.query_row(
            "SELECT (SELECT count(*) FROM records) + (SELECT count(*) FROM settings)",
            [],
            |row| row.get(0),
        )?;
        if occupied != 0 {
            return Err(StoreError::UnsafeImport);
        }
        let mut count = 0;
        {
            let mut rows = snapshot.prepare("SELECT owner,kind,id,body,updated FROM records")?;
            let iter = rows.query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, String>(4)?,
                ))
            })?;
            for row in iter {
                let (owner, kind, id, body, updated) = row?;
                let _: Value = serde_json::from_str(&body)?;
                target.execute(
                    "INSERT INTO records(owner,kind,id,body,updated) VALUES (?1,?2,?3,?4,?5)",
                    params![owner, kind, id, body, updated],
                )?;
                count += 1;
            }
        }
        {
            let mut rows = snapshot.prepare("SELECT key,value FROM settings")?;
            let iter = rows.query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })?;
            for row in iter {
                let (key, value) = row?;
                let _: Value = serde_json::from_str(&value)?;
                target.execute(
                    "INSERT INTO settings(key,value) VALUES (?1,?2)",
                    params![key, value],
                )?;
            }
        }
        snapshot.commit()?;
        target.commit()?;
        Ok(count)
    }

    /// Merge a read-only snapshot without overwriting any existing record or setting.
    /// A conflicting value rolls back the whole import. The caller must first make
    /// independent backups of both databases and handle exported files separately.
    pub fn merge_legacy(&mut self, source: &Path) -> Result<MergeReport, StoreError> {
        let destination = self.db.path().ok_or(StoreError::InvalidPath)?;
        let source = source.canonicalize().map_err(|_| StoreError::InvalidPath)?;
        let destination = Path::new(destination)
            .canonicalize()
            .map_err(|_| StoreError::InvalidPath)?;
        if source == destination {
            return Err(StoreError::UnsafeImport);
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            let a = source.metadata().map_err(|_| StoreError::InvalidPath)?;
            let b = destination
                .metadata()
                .map_err(|_| StoreError::InvalidPath)?;
            if a.dev() == b.dev() && a.ino() == b.ino() {
                return Err(StoreError::UnsafeImport);
            }
        }
        let mut old = Connection::open_with_flags(source, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        let snapshot = old.transaction()?;
        let target = self.db.transaction()?;
        let mut result = MergeReport::default();
        {
            let mut stmt = snapshot.prepare("SELECT owner,kind,id,body,updated FROM records")?;
            let rows = stmt.query_map([], |r| {
                Ok((
                    r.get::<_, String>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, String>(2)?,
                    r.get::<_, String>(3)?,
                    r.get::<_, String>(4)?,
                ))
            })?;
            for row in rows {
                let (owner, kind, id, body, updated) = row?;
                let parsed: Value = serde_json::from_str(&body)?;
                let current: Option<String> = target
                    .query_row(
                        "SELECT body FROM records WHERE owner=?1 AND kind=?2 AND id=?3",
                        params![owner, kind, id],
                        |r| r.get(0),
                    )
                    .optional()?;
                if let Some(existing) = current {
                    if serde_json::from_str::<Value>(&existing)? != parsed {
                        return Err(StoreError::MergeConflict);
                    }
                    result.records_unchanged += 1;
                } else {
                    target.execute(
                        "INSERT INTO records(owner,kind,id,body,updated) VALUES (?1,?2,?3,?4,?5)",
                        params![owner, kind, id, body, updated],
                    )?;
                    result.records_inserted += 1;
                }
            }
        }
        {
            let mut stmt = snapshot.prepare("SELECT key,value FROM settings")?;
            let rows =
                stmt.query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))?;
            for row in rows {
                let (key, value) = row?;
                let parsed: Value = serde_json::from_str(&value)?;
                let current: Option<String> = target
                    .query_row(
                        "SELECT value FROM settings WHERE key=?1",
                        params![key],
                        |r| r.get(0),
                    )
                    .optional()?;
                if let Some(existing) = current {
                    if serde_json::from_str::<Value>(&existing)? != parsed {
                        return Err(StoreError::MergeConflict);
                    }
                    result.settings_unchanged += 1;
                } else {
                    target.execute(
                        "INSERT INTO settings(key,value) VALUES (?1,?2)",
                        params![key, value],
                    )?;
                    result.settings_inserted += 1;
                }
            }
        }
        snapshot.commit()?;
        target.commit()?;
        Ok(result)
    }

    /// The old Python application used SHA-256(account)[:24] as its owner.
    /// Compare only that account against the native owner without writing either database.
    pub fn preview_legacy_account(
        &self,
        source: &Path,
        account: &str,
    ) -> Result<LegacyAccountPreview, StoreError> {
        if account.trim().is_empty() {
            return Err(StoreError::InvalidIdentifier);
        }
        let destination = self.db.path().ok_or(StoreError::InvalidPath)?;
        let source = source.canonicalize().map_err(|_| StoreError::InvalidPath)?;
        let destination = Path::new(destination)
            .canonicalize()
            .map_err(|_| StoreError::InvalidPath)?;
        if source == destination {
            return Err(StoreError::UnsafeImport);
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            let a = source.metadata().map_err(|_| StoreError::InvalidPath)?;
            let b = destination
                .metadata()
                .map_err(|_| StoreError::InvalidPath)?;
            if a.dev() == b.dev() && a.ino() == b.ino() {
                return Err(StoreError::UnsafeImport);
            }
        }
        let digest = Sha256::digest(account.as_bytes());
        let old_owner: String = digest[..12].iter().map(|b| format!("{b:02x}")).collect();
        let old = Connection::open_with_flags(source, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        let mut result = LegacyAccountPreview::default();
        result.other_account_records = old.query_row(
            "SELECT count(*) FROM records WHERE owner != ?1",
            params![old_owner],
            |row| row.get(0),
        )?;
        let mut stmt = old.prepare("SELECT kind,id,body FROM records WHERE owner=?1")?;
        let rows = stmt.query_map(params![old_owner], |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, String>(2)?,
            ))
        })?;
        for row in rows {
            let (kind, id, body) = row?;
            let parsed: Value = serde_json::from_str(&body)?;
            result.matching_records += 1;
            let current: Option<String> = self
                .db
                .query_row(
                    "SELECT body FROM records WHERE owner=?1 AND kind=?2 AND id=?3",
                    params![account, kind, id],
                    |r| r.get(0),
                )
                .optional()?;
            match current {
                None => result.new_records += 1,
                Some(existing) if serde_json::from_str::<Value>(&existing)? == parsed => {
                    result.identical_records += 1
                }
                Some(_) => {
                    result.conflicting_records += 1;
                    *result.conflicts_by_kind.entry(kind).or_default() += 1;
                }
            }
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn school_batch_merges_and_rolls_back_if_any_record_is_invalid() {
        let folder = std::env::temp_dir().join(format!(
            "buaa-sync-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir(&folder).unwrap();
        let mut store = Store::open(&folder.join("data.sqlite")).unwrap();
        store
            .put(
                "alice",
                RecordKind::Course,
                "c",
                &serde_json::json!({"name":"old","local":"keep"}),
            )
            .unwrap();
        let rows = vec![
            serde_json::json!({"kind":"course","id":"c","body":{"name":"new"}}),
            serde_json::json!({"kind":"invalid","id":"x","body":{}}),
        ];
        assert!(store.merge_school_records("alice", &rows).is_err());
        assert_eq!(
            store
                .get("alice", RecordKind::Course, "c")
                .unwrap()
                .unwrap()["name"],
            "old"
        );
        store.merge_school_records("alice", &rows[..1]).unwrap();
        let record = store
            .get("alice", RecordKind::Course, "c")
            .unwrap()
            .unwrap();
        assert_eq!(record["name"], "new");
        assert_eq!(record["local"], "keep");
        assert!(store.get("bob", RecordKind::Course, "c").unwrap().is_none());
        store
            .put(
                "alice",
                RecordKind::Lesson,
                "l",
                &serde_json::json!({"id":"l","course_id":"c"}),
            )
            .unwrap();
        let changed_course =
            vec![serde_json::json!({"kind":"lesson","id":"l","body":{"course_id":"other"}})];
        assert!(store
            .merge_school_records("alice", &changed_course)
            .is_err());
        assert_eq!(
            store
                .get("alice", RecordKind::Lesson, "l")
                .unwrap()
                .unwrap()["course_id"],
            "c"
        );
        drop(store);
        std::fs::remove_dir_all(folder).unwrap();
    }
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};
    static TEST_SEQUENCE: AtomicU64 = AtomicU64::new(0);
    #[test]
    fn materials_refresh_is_validated_owner_scoped_and_atomic() {
        let path = temp_db();
        let mut store = Store::open(&path).unwrap();
        store
            .put("a", RecordKind::Course, "c", &serde_json::json!({"id":"c"}))
            .unwrap();
        let lesson = serde_json::json!({"id":"l","course_id":"c","has_recording":true,"recording_course_id":"r"});
        store.put("a", RecordKind::Lesson, "l", &lesson).unwrap();
        store
            .put(
                "a",
                RecordKind::Analysis,
                "l",
                &serde_json::json!({"evidence":"old immutable snapshot"}),
            )
            .unwrap();
        let segments = serde_json::json!([{"id":"s0","start":0.0,"end":10.0,"text":"合成文本"}]);
        let slides = serde_json::json!([]);
        let digest = format!(
            "{:x}",
            Sha256::digest(
                serde_json::to_vec(&serde_json::json!({"segments":segments,"slides":slides}))
                    .unwrap()
            )
        );
        let material = serde_json::json!({"lesson_id":"l","course_id":"c","recording_course_id":"r","segments":segments,"slides":slides,"duration":60.0,"digest":digest,"fetched_at":"2026-11-01T00:00:00Z","unexpected_secret":"discard"});
        store.save_materials("a", "c", "l", &material).unwrap();
        let saved = store.get("a", RecordKind::Material, "l").unwrap().unwrap();
        assert!(saved.get("unexpected_secret").is_none());
        let saved_lesson = store.get("a", RecordKind::Lesson, "l").unwrap().unwrap();
        for field in ["digest", "course_id", "recording_course_id"] {
            let mut bad = material.clone();
            bad[field] = serde_json::json!("tampered");
            assert!(store.save_materials("a", "c", "l", &bad).is_err());
        }
        assert!(store.save_materials("b", "c", "l", &material).is_err());
        let mut empty = material.clone();
        empty["segments"] = serde_json::json!([]);
        assert!(store.save_materials("a", "c", "l", &empty).is_err());
        store.db.execute_batch("CREATE TRIGGER fail_lesson BEFORE UPDATE ON records WHEN NEW.kind='lesson' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;").unwrap();
        let mut updated = material.clone();
        updated["fetched_at"] = serde_json::json!("2026-11-02T00:00:00Z");
        assert!(store.save_materials("a", "c", "l", &updated).is_err());
        assert_eq!(
            store.get("a", RecordKind::Material, "l").unwrap().unwrap(),
            saved
        );
        assert_eq!(
            store.get("a", RecordKind::Lesson, "l").unwrap().unwrap(),
            saved_lesson
        );
        assert_eq!(
            store.get("a", RecordKind::Analysis, "l").unwrap().unwrap()["evidence"],
            "old immutable snapshot"
        );
        drop(store);
        std::fs::remove_file(path).unwrap();
    }
    fn temp_db() -> std::path::PathBuf {
        std::env::temp_dir().join(format!(
            "buaa-native-test-{}-{}-{}.sqlite",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos(),
            TEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ))
    }
    #[test]
    fn windows_schema_upgrade_backs_up_wal_and_keeps_records() {
        let path = temp_db();
        let backup = temp_db();
        let old = Store::open(&path).unwrap();
        old.db.pragma_update(None, "wal_autocheckpoint", 0).unwrap();
        old.set_setting("profile", &serde_json::json!({"owner":"alice"}))
            .unwrap();
        let (store, created) = Store::open_versioned(&path, &backup).unwrap();
        assert!(created);
        assert_eq!(store.get_setting("profile").unwrap()["owner"], "alice");
        let saved = Connection::open_with_flags(&backup, OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap();
        let version: u32 = saved
            .pragma_query_value(None, "user_version", |r| r.get(0))
            .unwrap();
        assert_eq!(version, 0);
        let value: String = saved
            .query_row("SELECT value FROM settings WHERE key='profile'", [], |r| {
                r.get(0)
            })
            .unwrap();
        assert!(value.contains("alice"));
        drop(saved);
        drop(old);
        drop(store);
        let unused_backup = temp_db();
        let (again, created) = Store::open_versioned(&path, &unused_backup).unwrap();
        assert!(!created && !unused_backup.exists());
        drop(again);
        std::fs::remove_file(path).unwrap();
        std::fs::remove_file(backup).unwrap();
    }

    #[test]
    fn windows_schema_rejects_future_or_foreign_database_without_changes() {
        for (version, app) in [(2, Store::WINDOWS_APPLICATION_ID), (0, 1234)] {
            let path = temp_db();
            let backup = temp_db();
            let db = Connection::open(&path).unwrap();
            db.execute_batch(
                "CREATE TABLE protected(value TEXT); INSERT INTO protected VALUES('keep');",
            )
            .unwrap();
            db.pragma_update(None, "user_version", version).unwrap();
            db.pragma_update(None, "application_id", app).unwrap();
            drop(db);
            let before = std::fs::read(&path).unwrap();
            assert!(matches!(
                Store::open_versioned(&path, &backup),
                Err(StoreError::UnsupportedSchema)
            ));
            assert_eq!(std::fs::read(&path).unwrap(), before);
            assert!(!backup.exists());
            std::fs::remove_file(path).unwrap();
        }
        let path = temp_db();
        let backup = temp_db();
        let db = Connection::open(&path).unwrap();
        db.execute_batch("CREATE TABLE unrelated(value TEXT)")
            .unwrap();
        drop(db);
        assert!(matches!(
            Store::open_versioned(&path, &backup),
            Err(StoreError::UnsupportedSchema)
        ));
        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn windows_backup_failure_does_not_stamp_version_and_batch_rolls_back() {
        let path = temp_db();
        let store = Store::open(&path).unwrap();
        let backup = temp_db().join("missing-folder.sqlite3");
        assert!(Store::open_versioned(&path, &backup).is_err());
        let version: u32 = store
            .db
            .pragma_query_value(None, "user_version", |r| r.get(0))
            .unwrap();
        assert_eq!(version, 0);
        drop(store);
        let mut store = Store::open(&path).unwrap();
        let rows = serde_json::json!([
            {"kind":"course","id":"c","body":{"value":1}},
            {"kind":"unknown","id":"bad","body":{}}
        ]);
        assert!(store.put_batch("alice", rows.as_array().unwrap()).is_err());
        assert!(store
            .get("alice", RecordKind::Course, "c")
            .unwrap()
            .is_none());
        store
            .put_batch("alice", &rows.as_array().unwrap()[..1])
            .unwrap();
        assert!(store.get("bob", RecordKind::Course, "c").unwrap().is_none());
        drop(store);
        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn records_are_scoped_by_account_and_kind_and_survive_reopen() {
        let path = temp_db();
        {
            let mut store = Store::open(&path).unwrap();
            store
                .put(
                    "alice",
                    RecordKind::Course,
                    "same",
                    &serde_json::json!({"name":"Alice"}),
                )
                .unwrap();
            store
                .put(
                    "bob",
                    RecordKind::Course,
                    "same",
                    &serde_json::json!({"name":"Bob"}),
                )
                .unwrap();
            assert_eq!(
                store
                    .get("alice", RecordKind::Course, "same")
                    .unwrap()
                    .unwrap()["name"],
                "Alice"
            );
            assert!(store
                .get("alice", RecordKind::Lesson, "same")
                .unwrap()
                .is_none());
            assert!(matches!(
                store.put("", RecordKind::Course, "x", &Value::Null),
                Err(StoreError::InvalidIdentifier)
            ));
        }
        {
            let store = Store::open(&path).unwrap();
            assert_eq!(store.list("bob", RecordKind::Course).unwrap().len(), 1);
            assert_eq!(
                store
                    .get("bob", RecordKind::Course, "same")
                    .unwrap()
                    .unwrap()["name"],
                "Bob"
            );
        }
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn legacy_import_is_read_only_atomic_and_refuses_overwrite() {
        let old_path = temp_db();
        let new_path = temp_db();
        {
            let mut old = Store::open(&old_path).unwrap();
            old.put(
                "alice",
                RecordKind::Course,
                "id",
                &serde_json::json!({"name":"课程"}),
            )
            .unwrap();
            old.db
                .execute(
                    "INSERT INTO settings VALUES ('profile', '{\"owner\":\"alice\"}')",
                    [],
                )
                .unwrap();
        }
        let mut store = Store::open(&new_path).unwrap();
        assert!(matches!(
            store.import_legacy(&new_path),
            Err(StoreError::UnsafeImport)
        ));
        assert_eq!(store.import_legacy(&old_path).unwrap(), 1);
        assert_eq!(
            store
                .get("alice", RecordKind::Course, "id")
                .unwrap()
                .unwrap()["name"],
            "课程"
        );
        assert!(matches!(
            store.import_legacy(&old_path),
            Err(StoreError::UnsafeImport)
        ));
        drop(store);
        let old = Store::open(&old_path).unwrap();
        assert_eq!(old.list("alice", RecordKind::Course).unwrap().len(), 1);
        drop(old);
        std::fs::remove_file(old_path).unwrap();
        std::fs::remove_file(new_path).unwrap();
    }
    #[test]
    fn legacy_merge_preserves_existing_records_and_rolls_back_conflicts() {
        let old_path = temp_db();
        let new_path = temp_db();
        {
            let mut old = Store::open(&old_path).unwrap();
            old.put(
                "alice",
                RecordKind::Course,
                "shared",
                &serde_json::json!({"name":"same"}),
            )
            .unwrap();
            old.put(
                "alice",
                RecordKind::Lesson,
                "old",
                &serde_json::json!({"title":"legacy"}),
            )
            .unwrap();
            old.db
                .execute(
                    "INSERT INTO settings VALUES ('profile', '{\"owner\":\"alice\"}')",
                    [],
                )
                .unwrap();
        }
        let mut target = Store::open(&new_path).unwrap();
        target
            .put(
                "alice",
                RecordKind::Course,
                "shared",
                &serde_json::json!({"name":"same"}),
            )
            .unwrap();
        target
            .put(
                "bob",
                RecordKind::Course,
                "current",
                &serde_json::json!({"name":"new"}),
            )
            .unwrap();
        let report = target.merge_legacy(&old_path).unwrap();
        assert_eq!(report.records_inserted, 1);
        assert_eq!(report.records_unchanged, 1);
        assert_eq!(report.settings_inserted, 1);
        let again = target.merge_legacy(&old_path).unwrap();
        assert_eq!(again.records_inserted, 0);
        assert_eq!(again.records_unchanged, 2);
        assert_eq!(again.settings_unchanged, 1);
        {
            let mut old = Store::open(&old_path).unwrap();
            old.put(
                "alice",
                RecordKind::Course,
                "shared",
                &serde_json::json!({"name":"changed"}),
            )
            .unwrap();
            old.put(
                "alice",
                RecordKind::Lesson,
                "another",
                &serde_json::json!({"title":"not imported"}),
            )
            .unwrap();
        }
        assert!(matches!(
            target.merge_legacy(&old_path),
            Err(StoreError::MergeConflict)
        ));
        assert!(target
            .get("alice", RecordKind::Lesson, "another")
            .unwrap()
            .is_none());
        assert_eq!(
            target
                .get("alice", RecordKind::Course, "shared")
                .unwrap()
                .unwrap()["name"],
            "same"
        );
        assert_eq!(
            target
                .get("bob", RecordKind::Course, "current")
                .unwrap()
                .unwrap()["name"],
            "new"
        );
        drop(target);
        std::fs::remove_file(old_path).unwrap();
        std::fs::remove_file(new_path).unwrap();
    }
    #[test]
    fn legacy_preview_maps_hashed_owner_and_reports_conflicts_without_writes() {
        let old_path = temp_db();
        let new_path = temp_db();
        let owner = "student-id";
        let digest = Sha256::digest(owner.as_bytes());
        let legacy: String = digest[..12].iter().map(|b| format!("{b:02x}")).collect();
        {
            let mut old = Store::open(&old_path).unwrap();
            old.put(
                &legacy,
                RecordKind::Course,
                "same",
                &serde_json::json!({"name":"same"}),
            )
            .unwrap();
            old.put(
                &legacy,
                RecordKind::Course,
                "changed",
                &serde_json::json!({"name":"old"}),
            )
            .unwrap();
            old.put(
                &legacy,
                RecordKind::Lesson,
                "new",
                &serde_json::json!({"title":"legacy"}),
            )
            .unwrap();
            old.put(
                "someone-else",
                RecordKind::Course,
                "other",
                &serde_json::json!({}),
            )
            .unwrap();
        }
        let mut target = Store::open(&new_path).unwrap();
        target
            .put(
                owner,
                RecordKind::Course,
                "same",
                &serde_json::json!({"name":"same"}),
            )
            .unwrap();
        target
            .put(
                owner,
                RecordKind::Course,
                "changed",
                &serde_json::json!({"name":"current"}),
            )
            .unwrap();
        let preview = target.preview_legacy_account(&old_path, owner).unwrap();
        assert_eq!(
            (
                preview.matching_records,
                preview.new_records,
                preview.identical_records,
                preview.conflicting_records,
                preview.other_account_records
            ),
            (3, 1, 1, 1, 1)
        );
        assert_eq!(preview.conflicts_by_kind.get("course"), Some(&1));
        assert!(target
            .get(owner, RecordKind::Lesson, "new")
            .unwrap()
            .is_none());
        assert!(matches!(
            target.preview_legacy_account(&new_path, owner),
            Err(StoreError::UnsafeImport)
        ));
        drop(target);
        std::fs::remove_file(old_path).unwrap();
        std::fs::remove_file(new_path).unwrap();
    }
    #[test]
    fn invalid_legacy_record_rolls_back_everything() {
        let old_path = temp_db();
        let new_path = temp_db();
        {
            let old = Store::open(&old_path).unwrap();
            old.db
                .execute(
                    "INSERT INTO records VALUES ('alice','course','a','{}','now')",
                    [],
                )
                .unwrap();
            old.db
                .execute(
                    "INSERT INTO records VALUES ('alice','course','b','invalid','now')",
                    [],
                )
                .unwrap();
        }
        let mut store = Store::open(&new_path).unwrap();
        assert!(matches!(
            store.import_legacy(&old_path),
            Err(StoreError::InvalidJson(_))
        ));
        assert!(store.list("alice", RecordKind::Course).unwrap().is_empty());
        drop(store);
        std::fs::remove_file(old_path).unwrap();
        std::fs::remove_file(new_path).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn private_creation_and_hardlink_to_source_are_checked() {
        use std::os::unix::fs::MetadataExt;
        let path = temp_db();
        let link = temp_db();
        let mut store = Store::open(&path).unwrap();
        assert_eq!(path.metadata().unwrap().mode() & 0o077, 0);
        std::fs::hard_link(&path, &link).unwrap();
        assert!(matches!(
            store.import_legacy(&link),
            Err(StoreError::UnsafeImport)
        ));
        drop(store);
        std::fs::remove_file(link).unwrap();
        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn analysis_save_rejects_changed_material_in_same_transaction() {
        let mut store = Store::open(Path::new(":memory:")).unwrap();
        store
            .put(
                "alice",
                RecordKind::Course,
                "c",
                &serde_json::json!({"id":"c"}),
            )
            .unwrap();
        store
            .put(
                "alice",
                RecordKind::Lesson,
                "l",
                &serde_json::json!({"id":"l","course_id":"c"}),
            )
            .unwrap();
        store
            .put(
                "alice",
                RecordKind::Material,
                "l",
                &serde_json::json!({"digest":"new"}),
            )
            .unwrap();
        let mut analysis = serde_json::json!({"lesson_id":"l","course_id":"c","course_name":"课","lesson_title":"课次","created_at":"now","digest":"old","sections":[],"evidence":{},"assignments":[]});
        assert!(matches!(
            store.save_analysis_checked("alice", &analysis, Some("old")),
            Err(StoreError::MaterialsChanged)
        ));
        assert!(store
            .get("alice", RecordKind::Analysis, "l")
            .unwrap()
            .is_none());
        analysis["digest"] = serde_json::json!("new");
        store
            .save_analysis_checked("alice", &analysis, Some("new"))
            .unwrap();
        assert!(store
            .get("alice", RecordKind::Analysis, "l")
            .unwrap()
            .is_some());
    }
    #[test]
    fn saving_analysis_atomically_reconciles_tasks_and_preserves_completion() {
        let path = temp_db();
        {
            let mut store = Store::open(&path).unwrap();
            store
                .put(
                    "alice",
                    RecordKind::Course,
                    "c",
                    &serde_json::json!({"id":"c"}),
                )
                .unwrap();
            store
                .put(
                    "alice",
                    RecordKind::Lesson,
                    "l",
                    &serde_json::json!({"id":"l","course_id":"c"}),
                )
                .unwrap();
            let mut analysis = serde_json::json!({"lesson_id":"l","course_id":"c","course_name":"课","lesson_title":"第一课","created_at":"2026-10-28T00:00:00Z",
                "sections":[],"evidence":{"s0":{"text":"作业"}},"assignments":[{"text":"保留","evidence_ids":["s0"]},{"text":"移除","evidence_ids":["s0"]}]});
            let tasks = store.save_analysis("alice", &analysis).unwrap();
            let kept_id = tasks[0]["id"].as_str().unwrap().to_owned();
            let removed_id = tasks[1]["id"].as_str().unwrap().to_owned();
            let mut done = tasks[0].clone();
            done["done"] = Value::Bool(true);
            store
                .put("alice", RecordKind::Task, &kept_id, &done)
                .unwrap();
            analysis["assignments"] = serde_json::json!([{"text":"保留","evidence_ids":["s0"]}]);
            store.save_analysis("alice", &analysis).unwrap();
            assert_eq!(
                store
                    .get("alice", RecordKind::Task, &kept_id)
                    .unwrap()
                    .unwrap()["done"],
                true
            );
            assert_eq!(
                store
                    .get("alice", RecordKind::Task, &removed_id)
                    .unwrap()
                    .unwrap()["superseded"],
                true
            );
            let mut bad = analysis.clone();
            bad["assignments"] = serde_json::json!([{"text":"虚构","evidence_ids":["s9"]}]);
            assert!(matches!(
                store.save_analysis("alice", &bad),
                Err(StoreError::InvalidAnalysis)
            ));
            assert_eq!(
                store
                    .get("alice", RecordKind::Analysis, "l")
                    .unwrap()
                    .unwrap(),
                analysis
            );
            assert!(matches!(
                store.save_analysis("bob", &analysis),
                Err(StoreError::InvalidAnalysis)
            ));
        }
        std::fs::remove_file(path).unwrap();
    }
}
