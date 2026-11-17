using System.Diagnostics;
using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace BUAALearn.App;

/// M0 verification window, not a production workspace. Lists use explicit action
/// buttons rather than introducing single-select popups before PRD decision D01.
internal sealed class ProbeWindow : Window
{
    private readonly TextBlock status = new() { Text = "正在检查业务内核…", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel choices = new() { Spacing = 8 };
    private readonly WebView2 browser = new();
    private readonly Button connect = new() { Content = "连接学校", IsEnabled = false };
    private readonly Button cancel = new() { Content = "停止并退出登录" };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private CoreWebView2Environment? environment;
    private CoreSchool? school;
    private SchoolTransport? transport;
    private string? recordingId;
    private bool browserReady;
    private bool closed;
    private bool busy;
    private readonly string sessionDirectory;
    private readonly FileStream sessionLease;

    public ProbeWindow()
    {
        Title = "BUAA Learn · Windows 连接验证";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        var root = new Grid { Padding = new Thickness(20), ColumnSpacing = 20 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rail = new StackPanel { Spacing = 16 };
        rail.Children.Add(new TextBlock { Text = "学校连接验证", FontSize = 24 });
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        rail.Children.Add(status);
        connect.Click += async (_, _) => await Run(LoginAsync);
        cancel.Click += async (_, _) =>
        {
            operation?.Cancel(); transport?.Dispose(); browser.CoreWebView2?.Stop();
            if (busy) { SetStatus("操作已停止；正在释放学校会话。"); return; }
            choices.Children.Clear();
            await Task.Run(() => school?.Dispose()); school = null; transport = null;
            try
            {
                if (browserReady && !closed && browser.CoreWebView2 is { } core) await core.Profile.ClearBrowsingDataAsync();
                SetStatus("已退出学校会话。");
            }
            catch { SetStatus("学校会话已释放，浏览器资料将在退出后重试清理。"); }
        };
        rail.Children.Add(connect); rail.Children.Add(cancel);
        rail.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 520 });
        root.Children.Add(rail);
        Grid.SetColumn(browser, 1); root.Children.Add(browser);
        Content = root;
        var sessionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BUAA Learn Native", "probe-sessions");
        Directory.CreateDirectory(sessionRoot);
        // A locked profile (another live probe) is skipped; only abandoned generated paths are removed.
        foreach (var directory in Directory.EnumerateDirectories(sessionRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                using (new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                TryDelete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        sessionDirectory = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        sessionLease = new FileStream(Path.Combine(sessionDirectory, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Closed += async (_, _) =>
        {
            closed = true; lifetime.Cancel(); operation?.Cancel(); transport?.Dispose();
            browser.Close();
            await Task.Run(() => school?.Dispose());
            // Browser processes may release the profile after the window closes. Also clean on next launch.
            sessionLease.Dispose(); TryDelete(sessionDirectory);
        };
        root.Loaded += async (_, _) => await InitializeAsync();
    }
    private static void TryDelete(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void SetStatus(string message) { if (!closed) status.Text = message; }
    private async Task InitializeAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var smokeIndex = Array.IndexOf(args, "--smoke-report");
        var smokePath = smokeIndex >= 0 && smokeIndex + 1 < args.Length ? args[smokeIndex + 1] : null;
        var elapsed = Stopwatch.StartNew();
        var success = false;
        try
        {
            CoreBridge.CheckAbi();
            CoreBridge.ParseSemesters("{\"code\":200,\"content\":[{\"xnxq\":\"synthetic\"}]}");
            if (closed) return;
            environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, sessionDirectory, new CoreWebView2EnvironmentOptions());
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await browser.EnsureCoreWebView2Async(environment, options);
            if (closed) return;
            var web = browser.CoreWebView2;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.DownloadStarting += (_, e) => e.Cancel = true;
            web.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
                if (LoginProtocol.UpgradeRecording(uri) is { } secure)
                {
                    e.Cancel = true;
                    DispatcherQueue.TryEnqueue(() => { if (!closed) web.Navigate(secure.AbsoluteUri); });
                    return;
                }
                if (!LoginProtocol.IsAllowed(uri)) { e.Cancel = true; SetStatus($"已阻止跳转到 {uri.Scheme}://{uri.IdnHost}。请核对学校跳转允许列表。"); return; }
                recordingId = LoginProtocol.CourseId(uri) ?? recordingId;
            };
            web.FrameNavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !LoginProtocol.IsAllowed(uri)) e.Cancel = true;
            };
            web.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && LoginProtocol.IsAllowed(uri)) web.Navigate(uri.AbsoluteUri);
            };
            browserReady = true; connect.IsEnabled = true;
            SetStatus("内核和登录浏览器已就绪。");
            success = true;
        }
        catch { SetStatus("无法初始化。请检查业务内核及 WebView2 Runtime，重新构建或安装后重试。"); }
        finally
        {
            if (smokePath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(smokePath))!);
                await File.WriteAllTextAsync(smokePath, JsonSerializer.Serialize(new { ok = success, abi = CoreBridge.ExpectedAbi, elapsed_ms = elapsed.ElapsedMilliseconds, school_verified = false }));
                Environment.ExitCode = success ? 0 : 1;
                Close(); Application.Current.Exit();
            }
        }
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy || closed || !browserReady) return;
        busy = true; connect.IsEnabled = false; choices.IsHitTestVisible = false;
        operation?.Dispose(); operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10));
        try { await action(operation.Token); }
        catch (OperationCanceledException) { SetStatus("操作已取消或等待超时，请重新连接学校。"); }
        catch (CoreException error) { SetStatus(error.Message); }
        catch { SetStatus("验证未完成。请检查学校网络、页面状态或重新连接学校。"); }
        finally
        {
            if (operation.IsCancellationRequested)
            {
                transport?.Dispose();
                await Task.Run(() => school?.Dispose()); school = null; transport = null;
                if (!closed) { choices.Children.Clear(); browser.CoreWebView2?.CookieManager.DeleteAllCookies(); }
            }
            busy = false;
            if (!closed) { connect.IsEnabled = true; choices.IsHitTestVisible = true; }
        }
    }
    private void AddChoice(string label, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += async (_, _) => await Run(action);
        choices.Children.Add(button);
    }
    private async Task LoginAsync(CancellationToken token)
    {
        transport?.Dispose();
        await Task.Run(() => school?.Dispose()); school = null; transport = null;
        choices.Children.Clear(); recordingId = null;
        await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
        browser.CoreWebView2.Navigate("https://spoc.buaa.edu.cn/spocnewht/cas");
        SetStatus("请在学校页面完成登录。");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(1000, token);
            if (!Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out var origin) || !LoginProtocol.CanCapture(origin)) continue;
            var raw = await browser.CoreWebView2.ExecuteScriptAsync(LoginProtocol.CaptureScript);
            // Recheck after evaluation; a navigation may have occurred while it was pending.
            if (!Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out origin) || !LoginProtocol.CanCapture(origin)) continue;
            using var result = JsonDocument.Parse(raw);
            var data = result.RootElement;
            if (data.ValueKind != JsonValueKind.Object) continue;
            if (data.GetProperty("role").GetString() != "01") throw new CoreException("请切换到学校学生角色后重新连接。");
            transport = new SchoolTransport(lifetime.Token);
            school = new CoreSchool(transport, data.GetProperty("token").GetString()!, data.GetProperty("refresh").GetString()!, "01");
            var terms = await Task.Run(() => school.Semesters(), token);
            token.ThrowIfCancellationRequested();
            choices.Children.Clear();
            foreach (var term in terms.EnumerateArray())
            {
                var value = term.GetString()!;
                AddChoice(value, t => CoursesAsync(value, t));
            }
            SetStatus(choices.Children.Count == 0 ? "学校未返回学期。" : "学校已连接，选择学期。");
            return;
        }
    }
    private async Task CoursesAsync(string term, CancellationToken token)
    {
        var rows = await Task.Run(() => school!.Courses(term), token);
        token.ThrowIfCancellationRequested(); choices.Children.Clear();
        foreach (var row in rows.EnumerateArray())
        {
            var course = row.Clone();
            AddChoice(course.GetProperty("name").GetString()!, t => CatalogueAsync(course, t));
        }
        SetStatus(choices.Children.Count == 0 ? "该学期没有课程。" : "选择一门课程验证录播授权。");
    }
    private async Task CatalogueAsync(JsonElement course, CancellationToken token)
    {
        recordingId = null;
        var signature = await Task.Run(() => school!.RecordingEntry(course.GetProperty("id").GetString()!, course.GetProperty("task_id").GetString()!), token);
        browser.CoreWebView2.Navigate(LoginProtocol.RecordingEntry(signature).AbsoluteUri);
        SetStatus("正在等待录播授权。");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, token);
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(null);
            string? bearer = null;
            foreach (var cookie in cookies)
                bearer ??= LoginProtocol.RecordingBearer(cookie.Name, cookie.Domain, cookie.Value);
            if (bearer == null || recordingId == null) continue;
            var id = recordingId;
            var rows = await Task.Run(() => { school!.SetRecording(bearer); return school.Catalogue(id); }, token);
            token.ThrowIfCancellationRequested(); choices.Children.Clear();
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.GetProperty("has_recording").GetBoolean()) continue;
                var lesson = row.Clone();
                AddChoice(lesson.GetProperty("title").GetString()!, t => MaterialsAsync(id, lesson, t));
            }
            SetStatus($"录播目录已验证，共 {rows.GetArrayLength()} 节；选择回放验证材料。");
            return;
        }
        throw new CoreException("录播授权未完成或跳转未返回课程编号，请重新连接。");
    }
    private async Task MaterialsAsync(string courseId, JsonElement lesson, CancellationToken token)
    {
        var id = lesson.GetProperty("id").GetString()!;
        var duration = lesson.GetProperty("duration_seconds").GetDouble();
        if (lesson.TryGetProperty("recording_course_id", out var recording) && !string.IsNullOrWhiteSpace(recording.GetString())) courseId = recording.GetString()!;
        var material = await Task.Run(() => school!.Materials(courseId, id, duration), token);
        token.ThrowIfCancellationRequested();
        var segments = material.GetProperty("segments").GetArrayLength();
        var slides = material.GetProperty("slides").GetArrayLength();
        SetStatus($"材料接口已验证：{segments} 段文本、{slides} 张截图。" + (segments == 0 && slides == 0 ? "材料尚未就绪，不能视为已取得课堂材料。" : "本原型不保存课堂内容。"));
    }
}
