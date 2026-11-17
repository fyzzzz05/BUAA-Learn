using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BUAALearn.CoreInterop;

namespace BUAALearn.Platform;

/// Narrow protocol for the packaged workspace only. The WinUI adapter must also
/// block remote navigation, frames, popups and downloads; never attach this to SSO.
public sealed class WorkspaceBridge(NativeWorkspace workspace, SchoolSyncRuntime runtime, SchoolLoginController? login = null, Action<Uri>? openReplay = null, SlideImageCache? images = null, ModelSettingsService? models = null, Action<string>? openExportFolder = null, Action<string>? openExportFile = null, Func<string, CancellationToken, Task<string?>>? chooseExport = null)
{
    public const string DocumentUrl = "https://workspace.buaa.invalid/index.html";
    public const int MaxRequestBytes = 65536;
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly object questionGate = new();
    private (string? Owner, string Id, CancellationTokenSource Scope)? activeQuestion;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false) }
    };
    public static bool IsWorkspaceDocument(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "workspace.buaa.invalid" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/index.html" && uri.Query.Length == 0;
    private sealed class InvalidRequest : Exception;
    private sealed class StaleAccount : Exception;
    private static string Text(JsonElement args, string name, int max = 256)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidRequest();
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > max || text.Any(char.IsControl)) throw new InvalidRequest();
        return text;
    }
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidRequest();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidRequest();
    }
    private static ReaderTab Tab(JsonElement args)
    {
        var name = Text(args, "tab", 32);
        return name switch { "timeline" => ReaderTab.Timeline, "transcript" => ReaderTab.Transcript, "slides" => ReaderTab.Slides, "materials" => ReaderTab.Materials, _ => throw new InvalidRequest() };
    }
    private static string Reply(string? id, bool ok, object? result, string? code, string? owner) =>
        JsonSerializer.Serialize(new { v = 1, id, ok, owner, result, error = code }, Json);
    public async Task<string?> DispatchAsync(string source, string message, CancellationToken cancellation = default)
    {
        if (!IsWorkspaceDocument(source)) return null;
        string? id = null;
        try
        {
            if (Encoding.UTF8.GetByteCount(message) > MaxRequestBytes) throw new InvalidRequest();
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
            var request = document.RootElement;
            Fields(request, "v", "id", "method", "owner", "args");
            id = Text(request, "id", 64);
            if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new InvalidRequest();
            if (!request.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 ||
                !request.TryGetProperty("owner", out var owner) || owner.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                !request.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) throw new InvalidRequest();
            var method = Text(request, "method", 64);
            if (method is "ask_course" or "cancel_question")
            {
                var expected = owner.GetString();
                var result = await QuestionAsync(method, expected, args, cancellation).ConfigureAwait(false);
                return Reply(id, true, result, null, expected);
            }
            if (method is "save_pdf_as" or "save_markdown_as")
            {
                Fields(args, "lesson");
                if (chooseExport == null) throw new InvalidRequest();
                var lesson = Text(args, "lesson");
                var expected = owner.GetString();
                var account = await runtime.CaptureAccountAsync(expected, cancellation).ConfigureAwait(false);
                using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation, account.Cancellation);
                var store = account.Store.BoundTo(scope.Token);
                var path = method == "save_pdf_as" ? SlidePdfExport.ExistingFile(workspace.Paths, store, lesson)
                    : new MarkdownExport(workspace.Paths, store, lesson).Save().Path;
                var suggested = method == "save_pdf_as" ? "去重课件.pdf" : Path.GetFileName(path);
                var destination = await chooseExport(suggested, scope.Token).ConfigureAwait(false);
                if (destination != null)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    await CopyExportAsync(path, destination, scope.Token).ConfigureAwait(false);
                }
                return Reply(id, true, new { saved = destination != null }, null, expected);
            }
            if (method == "slide")
            {
                Fields(args, "lesson", "slide", "download");
                if (images == null || !args.TryGetProperty("download", out var download) || download.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidRequest();
                var expected = owner.ValueKind == JsonValueKind.Null ? null : owner.GetString();
                var account = await runtime.CaptureAccountAsync(expected, cancellation).ConfigureAwait(false);
                using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation, account.Cancellation);
                var result = await images.GetAsync(account.Store, Text(args, "lesson"), Text(args, "slide"), download.GetBoolean(), scope.Token).ConfigureAwait(false);
                scope.Token.ThrowIfCancellationRequested(); return Reply(id, true, result, null, expected);
            }
            await dispatch.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var expected = owner.ValueKind == JsonValueKind.Null ? null : owner.GetString();
                var boundOwner = runtime.Jobs.CurrentOwner;
                if (method != "workspace" && expected != boundOwner) throw new StaleAccount();
                object? result;
                if (method == "connect")
                {
                    Fields(args); if (login == null) throw new InvalidRequest();
                    result = await login.StartAsync(expected, cancellation).ConfigureAwait(false);
                }
                else if (method == "cancel_login")
                {
                    Fields(args); if (login == null) throw new InvalidRequest();
                    await login.CancelAsync().ConfigureAwait(false); result = null;
                }
                else if (method == "select_account")
                {
                    Fields(args, "account"); var account = Text(args, "account");
                    if (login != null) await login.CancelAsync().ConfigureAwait(false);
                    await runtime.SelectAccountFromAsync(account, expected).ConfigureAwait(false); result = null;
                }
                else if (method == "disconnect")
                {
                    Fields(args); if (login != null) await login.CancelAsync().ConfigureAwait(false);
                    await runtime.DisconnectFromAsync(expected).ConfigureAwait(false); result = null;
                }
                else result = await runtime.UseAccountAsync(boundOwner, () => Execute(method, args), cancellation).ConfigureAwait(false);
                // A concurrent native account transition must never relabel an
                // old account's payload as belonging to the new current account.
                return Reply(id, true, result, null, method == "select_account" ? Text(args, "account") : boundOwner);
            }
            finally { dispatch.Release(); }
        }
        catch (StaleAccount) { return Reply(id, false, null, "stale_account", runtime.Jobs.CurrentOwner); }
        catch (AccountContextChangedException) { return Reply(id, false, null, "stale_account", runtime.Jobs.CurrentOwner); }
        catch (ModelCredentialException) { return Reply(id, false, null, "credential_unavailable", runtime.Jobs.CurrentOwner); }
        catch (CoreException error) when (error.Code is "qa_scope" or "qa_no_material" or "qa_busy" or "qa_configuration" or "automation_model")
        { return Reply(id, false, null, error.Code, runtime.Jobs.CurrentOwner); }
        catch (CoreModelException error)
        {
            var code = error.Code is "authentication" or "quota" or "timeout" or "network" ? "qa_" + error.Code : "qa_model";
            return Reply(id, false, null, code, runtime.Jobs.CurrentOwner);
        }
        catch (BUAALearn.CoreInterop.CoreException error) when (error.Code == "model_configuration") { return Reply(id, false, null, "model_configuration", runtime.Jobs.CurrentOwner); }
        catch (Exception error) when (error is InvalidRequest or JsonException or ArgumentException) { return Reply(id, false, null, "invalid_request", runtime.Jobs.CurrentOwner); }
        catch (OperationCanceledException) { return Reply(id, false, null, "cancelled", runtime.Jobs.CurrentOwner); }
        catch { return Reply(id, false, null, "operation_failed", runtime.Jobs.CurrentOwner); }
    }

    private static async Task CopyExportAsync(string source, string destination, CancellationToken cancellation)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new InvalidRequest();
        WorkspacePaths.RejectLink(source);
        WorkspacePaths.RejectLink(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".buaa.tmp");
        try
        {
            await using (var reader = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            await using (var writer = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await reader.CopyToAsync(writer, cancellation).ConfigureAwait(false);
                await writer.FlushAsync(cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<object?> QuestionAsync(string method, string? owner, JsonElement args, CancellationToken cancellation)
    {
        var requestId = Text(args, "request", 64);
        if (method == "cancel_question")
        {
            Fields(args, "request");
            lock (questionGate)
                if (activeQuestion is { } active && active.Owner == owner && active.Id == requestId)
                    active.Scope.Cancel();
            return null;
        }

        Fields(args, "request", "course", "lesson", "question", "acknowledge_cost");
        if (runtime.Learning is not { } learning || !args.TryGetProperty("acknowledge_cost", out var consent) || consent.ValueKind != JsonValueKind.True)
            throw new InvalidRequest();
        var course = Text(args, "course");
        var lesson = args.TryGetProperty("lesson", out var selected) && selected.ValueKind != JsonValueKind.Null ? Text(args, "lesson") : null;
        var question = Text(args, "question", 2000).Trim();
        if (question.Length == 0) throw new InvalidRequest();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        scope.CancelAfter(TimeSpan.FromMinutes(3));
        lock (questionGate)
        {
            if (activeQuestion != null) throw new CoreException("已有问答请求正在运行。", "qa_busy");
            activeQuestion = (owner, requestId, scope);
        }
        try
        {
            var account = await runtime.CaptureAccountAsync(owner, scope.Token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, account.Cancellation);
            return await Task.Run(() => learning.Ask(account.Store.BoundTo(linked.Token), course, lesson, question, linked.Token), linked.Token).ConfigureAwait(false);
        }
        finally { lock (questionGate) activeQuestion = null; }
    }
    private object? Execute(string method, JsonElement args)
    {
        if (method == "workspace")
        {
            Fields(args);
            var owner = runtime.Jobs.CurrentOwner;
            var active = owner == null ? null : workspace.OpenAccount(owner).List("job").Select(JobService.Decode)
                .Where(x => x.Status is JobStatus.Queued or JobStatus.Running)
                .OrderBy(x => x.Status == JobStatus.Running ? 0 : 1).ThenBy(x => x.CreatedAt).FirstOrDefault();
            return new { accounts = workspace.Accounts, owner, account_revision = runtime.AccountRevision, connected = runtime.Jobs.IsSchoolConnected, draining = runtime.Jobs.IsDraining, login_available = login != null, login = login?.State, active_job = active };
        }
        var reader = runtime.Reader ?? throw new InvalidRequest();
        switch (method)
        {
            case "courses": Fields(args); return new { semesters = reader.Semesters(), semester = reader.SelectedSemester, courses = reader.Courses(), schedule = reader.Schedule() };
            case "lessons": Fields(args, "course"); return reader.Lessons(Text(args, "course"));
            case "course_chat": Fields(args, "course"); return LearningService.History(workspace.OpenAccount(reader.Owner), Text(args, "course"));
            case "automation_settings":
                Fields(args); return runtime.Automation.Read(workspace.OpenAccount(reader.Owner));
            case "save_automation":
                Fields(args, "changes");
                if (!args.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array ||
                    changes.GetArrayLength() is 0 or > 300) throw new InvalidRequest();
                var ruleChanges = new List<(string Id, bool Enabled, bool Analyze)>();
                var seenCourses = new HashSet<string>(StringComparer.Ordinal);
                foreach (var change in changes.EnumerateArray())
                {
                    Fields(change, "course", "enabled", "auto_analyze");
                    var course = Text(change, "course");
                    if (!seenCourses.Add(course) || !change.TryGetProperty("enabled", out var enabled) ||
                        enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        !change.TryGetProperty("auto_analyze", out var analyze) ||
                        analyze.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidRequest();
                    ruleChanges.Add((course, enabled.GetBoolean(), analyze.GetBoolean()));
                }
                return runtime.Automation.Save(workspace.OpenAccount(reader.Owner), ruleChanges);
            case "lesson":
                Fields(args, "lesson"); var id = Text(args, "lesson");
                var lesson = new LessonReader(workspace.OpenAccount(reader.Owner), id);
                return new { lesson = reader.Lesson(id), materials = lesson.Materials, analysis = lesson.Analysis, last_tab = lesson.LastTab };
            case "work_items": Fields(args); return new TaskReader(workspace.OpenAccount(reader.Owner)).List();
            case "set_task_done":
                Fields(args, "task", "done");
                if (!args.TryGetProperty("done", out var done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidRequest();
                new TaskReader(workspace.OpenAccount(reader.Owner)).SetDone(Text(args, "task"), done.GetBoolean()); return null;
            case "work_source":
                Fields(args, "kind", "id", "lesson"); return new TaskReader(workspace.OpenAccount(reader.Owner)).Source(Text(args, "kind", 16), Text(args, "id"), Text(args, "lesson"));
            case "jobs": Fields(args); return workspace.OpenAccount(reader.Owner).List("job");
            case "model_settings":
                Fields(args); if (models == null) throw new InvalidRequest(); return models.Get(workspace.OpenAccount(reader.Owner));
            case "model_key_status":
                Fields(args, "base_url"); if (models == null) throw new InvalidRequest(); return models.InspectEndpoint(workspace.OpenAccount(reader.Owner), Text(args, "base_url", 2048));
            case "save_model_settings":
                Fields(args, "base_url", "model", "key_action", "key"); if (models == null) throw new InvalidRequest();
                var keyAction = Text(args, "key_action", 16); string? key = null;
                if (args.TryGetProperty("key", out var secret)) { if (secret.ValueKind != JsonValueKind.String) throw new InvalidRequest(); key = secret.GetString(); }
                return models.Save(workspace.OpenAccount(reader.Owner), Text(args, "base_url", 2048), Text(args, "model"), keyAction, key);
            case "cleanup_model_keys":
                Fields(args); if (models == null) throw new InvalidRequest(); models.Cleanup(workspace.OpenAccount(reader.Owner)); return models.Get(workspace.OpenAccount(reader.Owner));
            case "remove_model_key":
                Fields(args, "base_url"); if (models == null) throw new InvalidRequest(); return models.RemoveKey(workspace.OpenAccount(reader.Owner), Text(args, "base_url", 2048));
            case "open_replay":
                Fields(args, "lesson"); if (openReplay == null) throw new InvalidRequest();
                openReplay(ReplayLink.ForLesson(reader.Lesson(Text(args, "lesson")))); return null;
            case "export_markdown":
                Fields(args, "lesson");
                return new MarkdownExport(workspace.Paths, workspace.OpenAccount(reader.Owner), Text(args, "lesson")).Save();
            case "open_markdown_folder":
                Fields(args, "lesson"); if (openExportFolder == null) throw new InvalidRequest();
                openExportFolder(new MarkdownExport(workspace.Paths, workspace.OpenAccount(reader.Owner), Text(args, "lesson")).ExistingDirectory()); return null;
            case "open_data_folder":
                Fields(args); if (openExportFolder == null) throw new InvalidRequest();
                WorkspacePaths.RejectLink(workspace.Paths.Root); openExportFolder(workspace.Paths.Root); return null;
            case "pdf_status":
                Fields(args, "lesson");
                return SlidePdfExport.Status(workspace.Paths, workspace.OpenAccount(reader.Owner), Text(args, "lesson"));
            case "export_pdf":
                Fields(args, "lesson"); if (images == null) throw new InvalidRequest();
                var pdfLesson = Text(args, "lesson");
                SlidePdfExport.PathFor(workspace.Paths, workspace.OpenAccount(reader.Owner), pdfLesson);
                return runtime.Jobs.Enqueue("slides-pdf", pdfLesson);
            case "open_pdf":
                Fields(args, "lesson"); if (openExportFile == null) throw new InvalidRequest();
                var pdf = SlidePdfExport.ExistingFile(workspace.Paths, workspace.OpenAccount(reader.Owner), Text(args, "lesson"));
                openExportFile(pdf); return null;
            case "select_semester":
                Fields(args, "semester"); var term = Text(args, "semester");
                if (!reader.Semesters().Any(x => x.Id == term && x.Cached)) throw new InvalidRequest();
                workspace.OpenAccount(reader.Owner).MergeSchool([new("meta", "semester", new { id = term })]); return null;
            case "reader":
                Fields(args, "lesson", "tab"); var reading = new LessonReader(workspace.OpenAccount(reader.Owner), Text(args, "lesson"));
                var tab = Tab(args); return new { position = reading.RestorePosition(tab), rows = tab == ReaderTab.Timeline ? reading.Evidence : reading.Transcript };
            case "source":
                Fields(args, "lesson", "start", "end"); return new LessonReader(workspace.OpenAccount(reader.Owner), Text(args, "lesson")).SourceRange(Text(args, "start"), Text(args, "end"));
            case "save_reading":
                Fields(args, "lesson", "tab", "search", "anchor", "offset");
                if (!args.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.String ||
                    !args.TryGetProperty("anchor", out var anchor) || anchor.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                    !args.TryGetProperty("offset", out var offset) || offset.ValueKind != JsonValueKind.Number || !offset.TryGetDouble(out var scroll)) throw new InvalidRequest();
                new LessonReader(workspace.OpenAccount(reader.Owner), Text(args, "lesson")).SavePosition(Tab(args), search.GetString()!, anchor.ValueKind == JsonValueKind.String ? anchor.GetString() : null, scroll); return null;
            case "refresh_semesters": Fields(args); return runtime.RefreshSemesters();
            case "sync_semester": Fields(args, "semester"); return runtime.SyncSemester(Text(args, "semester"));
            case "sync_schedule": Fields(args); return runtime.SyncSchedule();
            case "sync_course": Fields(args, "course"); return runtime.SyncCourse(Text(args, "course"));
            case "analyze_lesson":
                Fields(args, "lesson", "acknowledge_cost");
                if (models == null || !args.TryGetProperty("acknowledge_cost", out var consent) || consent.ValueKind != JsonValueKind.True) throw new InvalidRequest();
                if (!models.Get(workspace.OpenAccount(reader.Owner)).Ready) throw new InvalidRequest();
                return runtime.AnalyzeLesson(Text(args, "lesson"));
            case "fetch_materials": Fields(args, "lesson"); return runtime.FetchMaterials(Text(args, "lesson"));
            case "retry_job": Fields(args, "job"); return runtime.Jobs.Retry(Text(args, "job"));
            default: throw new InvalidRequest();
        }
    }
}
