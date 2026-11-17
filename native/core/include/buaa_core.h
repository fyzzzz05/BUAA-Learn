#ifndef BUAA_CORE_H
#define BUAA_CORE_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* UTF-8 JSON in; owned UTF-8 JSON out. Release each result with buaa_core_string_free. */
/* ABI major version; no allocation. Hosts must check before creating handles. */
uint32_t buaa_core_abi_version(void);
/* Original PNG/JPEG/WebP bytes; bounded to 12 MiB. Does not retain input. */
char *buaa_legacy_snapshot(const char *source, const char *destination, uint32_t require_idle);
char *buaa_legacy_rows(const char *source, const char *owner);
char *buaa_core_inspect_slide_image(const uint8_t *bytes, uint32_t length);
typedef struct BuaaSlidePdf BuaaSlidePdf;
BuaaSlidePdf *buaa_pdf_create(const char *path, const char *snapshot);
char *buaa_pdf_add(BuaaSlidePdf *handle, const uint8_t *bytes, uint32_t length, uint64_t seconds);
char *buaa_pdf_finish(BuaaSlidePdf *handle);
void buaa_pdf_close(BuaaSlidePdf *handle);
char *buaa_core_parse_semesters(const char *body);
char *buaa_core_parse_courses(const char *body, const char *semester);
char *buaa_core_parse_catalogue(const char *body);
char *buaa_core_parse_transcript(const char *body);
char *buaa_core_parse_slides(const char *body);
char *buaa_core_plan_automation(const char *rule, const char *rows, const char *current);
typedef char *(*BuaaAutomationCallback)(void *context, const char *request);
typedef void (*BuaaAutomationRelease)(void *context, char *response);
char *buaa_core_run_automation(void *context, BuaaAutomationCallback send, BuaaAutomationRelease release, const char *course, const char *rows, const char *current);
char *buaa_core_ground_points(const char *points, const char *evidence);
char *buaa_core_validate_answer(const char *answer, const char *cited, const char *available);
char *buaa_core_timeline(const char *plan, const char *segments, const char *findings, double duration);
char *buaa_core_model_request(const char *provider, const char *model, const char *system, const char *prompt, const char *schema);
char *buaa_core_model_parse(const char *provider, const char *body);
typedef struct CoreStore CoreStore;
CoreStore *buaa_store_open(const char *path);
/* On success stores a new handle in *result; otherwise sets it to NULL.
   The envelope reports schema_version and backup_created. Free envelope once. */
char *buaa_store_open_versioned(const char *path, const char *backup_path, CoreStore **result);
char *buaa_store_setting_get(CoreStore *store, const char *key);
char *buaa_store_setting_put(CoreStore *store, const char *key, const char *body);
char *buaa_store_put_batch(CoreStore *store, const char *owner, const char *records);
char *buaa_store_save_materials(CoreStore *store, const char *owner, const char *course, const char *lesson, const char *body);
char *buaa_store_put(CoreStore *store, const char *owner, const char *kind, const char *id, const char *body);
char *buaa_store_merge_school(CoreStore *store, const char *owner, const char *records);
char *buaa_store_get(CoreStore *store, const char *owner, const char *kind, const char *id);
char *buaa_store_list(CoreStore *store, const char *owner, const char *kind);
char *buaa_store_save_analysis(CoreStore *store, const char *owner, const char *analysis);
char *buaa_store_list_notice_groups(CoreStore *store, const char *owner);
char *buaa_store_set_task_done(CoreStore *store, const char *owner, const char *id, uint32_t done);
char *buaa_store_save_analysis_checked(CoreStore *store, const char *owner, const char *analysis, const char *digest);
void buaa_store_close(CoreStore *store);
typedef struct CoreSchool CoreSchool;
/* send receives transient JSON including credentials; return allocated JSON with
   status and body. release must free that response. Never follow redirects. */
typedef char *(*BUAASend)(void *context, const char *request);
typedef void (*BUAARelease)(void *context, char *response);
typedef void (*BUAAProgress)(void *context, const char *message);
/* The analysis callback receives temporary source text in memory. */
char *buaa_core_extract_chunk(void *context, BUAASend send, BUAARelease release, const char *prompt, const char *evidence);
char *buaa_core_analyze_lesson(void *context, BUAASend send, BUAARelease release, BUAAProgress progress, const char *input);
char *buaa_core_ask(void *context, BUAASend send, BUAARelease release, const char *input);
CoreSchool *buaa_school_open(void *context, BUAASend send, BUAARelease release, const char *token, const char *refresh, const char *role);
char *buaa_school_set_recording(CoreSchool *session, const char *token);
char *buaa_school_semesters(CoreSchool *session);
char *buaa_school_courses(CoreSchool *session, const char *semester);
char *buaa_school_recording_signature(CoreSchool *session, const char *course, const char *task);
char *buaa_school_schedule(CoreSchool *session, const char *start, const char *end);
char *buaa_school_catalogue(CoreSchool *session, const char *course);
char *buaa_school_materials(CoreSchool *session, const char *course, const char *lesson);
char *buaa_school_materials_with_duration(CoreSchool *session, const char *course, const char *lesson, double duration);
void buaa_school_close(CoreSchool *session);
void buaa_core_string_free(char *value);
#ifdef __cplusplus
}
#endif
#endif
