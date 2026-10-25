# Architecture

## Scope

Local, single-user Web application. FastAPI serves an HTML/CSS/JavaScript frontend and a read-only school data adapter. SQLite stores durable learning state. The application has no dependency on Codex, Ego, or another agent desktop product.

## Modules

| Module | Responsibility |
|---|---|
| `auth.py` | Launch an isolated ordinary browser for user-controlled SSO and recording-platform authentication; hold credentials only in memory |
| `portal.py` | Authenticated portal requests, refresh attempts, allowlisted recording redirects, recording API requests |
| `materials.py` | Normalize lesson catalogues, ASR fragments and PPT screenshots; preserve timestamps and report gaps |
| `learning.py` | Whole-transcript extraction, citation validation, deadline evidence, task identities and bilingual lexical retrieval |
| `service.py` | Serial job worker, per-account data access, persisted jobs, timetable-driven per-course replay watches |
| `replay_schedule.py` | Shanghai day boundaries, per-occurrence watch planning and time-window matching |
| `slide_export.py` | Allowlisted image fetching, conservative global screenshot deduplication, atomic lossless image PDF export |
| `store.py` | SQLite records partitioned by owner, kind and identifier; private local settings |
| `app.py` | Local-only API boundary and static frontend |
| `static/` | Course browsing, lesson study, transcript search, PPT viewing, tasks, model settings and automations |

## School data flow

1. Ordinary browser visits the teaching portal's `/spocnewht/cas` and the school's official SSO page.
2. Portal callback establishes its own application token; user role and account are read only after successful login.
3. Portal APIs provide semesters, the signed-in student's courses and calendar rows. The server derives student identity from the token.
4. A portal course ID resolves through `/yun/getHxyTimestampAndSignature2` to the school's recording bridge. The program does not create its own signature.
5. The bridge returns a recording course ID. The recording platform has a separate CAS/token session.
6. Recording catalogue entries provide lesson IDs. Materials are fetched for selected recorded lessons only.

Known read endpoints include `/component/queryXnxq`, `/jxkj/queryKclb`, `/jxkj/queryRlData`, the recording catalogue, lesson information, `search-trans-result`, and `search-ppt`. Portal auth headers are not forwarded to recording hosts. Signed redirect query strings and credentials are never logged or stored in learning records.

## Evidence and tasks

ASR segments get deterministic IDs within a fetched snapshot (`s0`, `s1`, ...). Analysis records retain the complete evidence snapshot and its material digest; refreshing materials does not silently retarget old citations. Results are drafts, not verified transcripts.

Tasks have a deterministic identity based on lesson ID and extracted description. Reanalysis preserves completion state for unchanged tasks and marks removed or reworded tasks superseded. Cross-lesson changes to the same assignment are not automatically merged in v0.1; users must compare the cited instructions.

Deadlines require a verbatim source quote containing the same deadline text. Missing evidence is represented as unknown. This check prevents fabricated sources and unsupported dates; it cannot prove a model correctly understood a real quotation.

## Scheduling semantics

The in-process scheduler checks stored rules every ten seconds. Each timetable occurrence starts watching at 00:00 Asia/Shanghai on the following day, retries every two hours until requested processing succeeds, and then stops. First enable tracks yesterday onward, avoiding historical bulk analysis. Jobs are serialized and coalesced by owner/type/target. Before external requests, the next retry time is persisted so authentication and provider failures cannot create rapid polling loops. Empty materials remain pending. Pausing or editing a running rule is preserved.

Real calendar rows are refreshed daily while connected, covering the previous seven and next twenty-one days. Weekly display is derived from the same response. Future dates are never extrapolated from weekdays. Persisted pending watches survive rolling-calendar replacement and server restarts. A missing timetable leaves the rule waiting for calendar data. Rules do not run with the server stopped. Timetable updates can be triggered through POST `/api/schedule/sync`.

On restart, previously queued/running jobs are marked interrupted rather than replayed under an unknown session. New logins select a separate local account namespace.

## Extension points

- Separate OCR/vision evidence from ASR evidence while retaining timestamps and slide identity.
- Re-transcribe only uncertain audio windows and compare against site ASR.
- Replace lexical retrieval with a pluggable embedding backend and evaluate recall.
- Add an approved hosting/authentication model before multi-user deployment.
- Add system-managed service startup and explicit notification channels.
- Export course materials into DeepTutor rather than couple ingestion to its entire runtime.

## References

- [FastAPI](https://fastapi.tiangolo.com/)
- [Playwright authentication](https://playwright.dev/python/docs/auth)
- [HTTPX clients](https://www.python-httpx.org/advanced/clients/)
- [DeepTutor knowledge center](https://docs.deeptutor.info/explore/knowledge/)

## Slide export

POST `/api/lessons/{id}/slides/export` queues a `slides-pdf` job. The per-account artifact is available through GET `/api/lessons/{id}/slides/pdf` only after completion. GET lesson includes counts, snapshot digest and whether the export matches current materials. Downloads accept only HTTPS school-resource URLs, never forward school or model credentials, reject redirects, and enforce byte/pixel limits. Failed exports leave the previous complete PDF untouched. Original screenshot records remain unchanged.
