using System.Text.Json;
using BUAALearn.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace BUAALearn.App;

/// Local business UI and native services. The school browser is a separate surface.
internal sealed class WorkspaceWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { Text = "正在打开学习空间…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32) };
    private readonly CancellationTokenSource lifetime = new();
    private NativeWorkspace? workspace;
    private SchoolSyncRuntime? runtime;
    private WorkspaceBridge? bridge;
    private SchoolLoginController? login;
    private SlideImageCache? images;
    private ModelSettingsService? models;
    private ModelTransport? modelTransport;
    private bool shuttingDown;
    private bool readyToClose;
    private Task? initialization;
    private BrowserProfileLease? profile;
    private readonly string? smokeReport;
    private bool smokeFinished;
    private readonly bool seededSmoke;
    public WorkspaceWindow(string? smokeReport = null, bool seededSmoke = false)
    {
        this.smokeReport = smokeReport;
        this.seededSmoke = seededSmoke;
        Title = "BUAA Learn";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        var root = new Grid(); root.Children.Add(browser); root.Children.Add(status); Content = root;
        AppWindow.Closing += (sender, args) =>
        {
            if (readyToClose) return;
            args.Cancel = true;
            if (!shuttingDown) _ = ShutdownAsync();
        };
        root.Loaded += async (_, _) => { initialization ??= InitializeAsync(); await initialization; };
    }
    private async Task InitializeAsync()
    {
        try
        {
            var smokePaths = smokeReport == null ? null :
                new WorkspacePaths(Path.Combine(Path.GetDirectoryName(smokeReport)!, "workspace-" + Guid.NewGuid().ToString("N")));
            workspace = await Task.Run(() =>
            {
                var opened = NativeWorkspace.Open(smokePaths);
                try { LegacyMigration.Recover(opened); return opened; }
                catch { opened.Dispose(); throw; }
            }, lifetime.Token);
            if (shuttingDown) return;
            models = new ModelSettingsService(new WindowsModelCredentialStore());
            modelTransport = new ModelTransport();
            images = new SlideImageCache(workspace.Paths);
            runtime = new SchoolSyncRuntime(workspace, models: models, modelTransport: modelTransport, images: images, scheduleAutomation: true);
            if (seededSmoke) await SeedSmokeAsync(workspace, runtime);
            login = new SchoolLoginController(runtime, OpenSchoolAsync); login.Changed += WorkspaceChanged;
            bridge = new WorkspaceBridge(workspace, runtime, login,
                uri => OpenShell(uri.AbsoluteUri), images, models, openExportFolder: OpenShell, openExportFile: OpenShell, chooseExport: ChooseExportAsync);
            profile = BrowserProfileLease.Create(workspace.Paths, "workspace");
            if (!HasWebViewRuntime())
            {
                status.Text = "缺少 Microsoft Edge WebView2 Runtime，请从 Microsoft 安装后重新打开应用。";
                if (smokeReport != null) await FinishSmokeAsync(null);
                return;
            }
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile.Path, new CoreWebView2EnvironmentOptions());
            var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
            await browser.EnsureCoreWebView2Async(environment, options);
            if (shuttingDown) return;
            var web = browser.CoreWebView2;
            web.Settings.IsWebMessageEnabled = true;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.DownloadStarting += (_, e) => e.Cancel = true;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.FrameNavigationStarting += (_, e) => e.Cancel = true;
            web.NavigationStarting += (_, e) => e.Cancel = !WorkspaceBridge.IsWorkspaceDocument(e.Uri);
            var assets = Path.Combine(AppContext.BaseDirectory, "Workspace");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException();
            web.SetVirtualHostNameToFolderMapping("workspace.buaa.invalid", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            // CSP blocks frames and network; this filter additionally denies every
            // resource except the three shipped entry assets. No file:// bridge.
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                if (e.ResourceContext == CoreWebView2WebResourceContext.Image && new[] { "data:image/png;base64,", "data:image/jpeg;base64,", "data:image/webp;base64," }.Any(prefix => e.Request.Uri.StartsWith(prefix, StringComparison.Ordinal))) return;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "workspace.buaa.invalid" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                    !new[] { "/index.html", "/app.js", "/app.css" }.Contains(uri.AbsolutePath, StringComparer.Ordinal))
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain");
            };
            web.WebMessageReceived += async (_, e) =>
            {
                var source = e.Source;
                if (shuttingDown || !WorkspaceBridge.IsWorkspaceDocument(source)) return;
                try
                {
                    var message = e.WebMessageAsJson;
                    var reply = await Task.Run(() => bridge.DispatchAsync(source, message, lifetime.Token), lifetime.Token);
                    if (!shuttingDown && reply != null && WorkspaceBridge.IsWorkspaceDocument(web.Source)) web.PostWebMessageAsJson(reply);
                }
                catch (OperationCanceledException) { }
                catch { if (!shuttingDown) { status.Text = "本地通信暂时不可用，请重新打开应用。"; status.Visibility = Visibility.Visible; } }
            };
            runtime.Jobs.Changed += JobChanged;
            web.NavigationCompleted += async (_, e) =>
            {
                status.Text = e.IsSuccess ? "" : "无法打开业务页面，请重新构建或安装后重试。";
                status.Visibility = e.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
                if (smokeReport != null) await FinishSmokeAsync(e.IsSuccess ? web : null);
            };
            web.Navigate(WorkspaceBridge.DocumentUrl);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!shuttingDown) status.Text = "无法打开学习空间，请检查应用文件、数据目录或 WebView2 Runtime。";
            if (smokeReport != null) await FinishSmokeAsync(null);
        }
    }

    private static bool HasWebViewRuntime()
    {
        try { return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (Exception error) when (error is FileNotFoundException or DllNotFoundException ||
            error.HResult == unchecked((int)0x80070002)) { return false; }
    }

    private Task<string?> ChooseExportAsync(string name, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                var extension = Path.GetExtension(name);
                picker.FileTypeChoices.Add(extension == ".pdf" ? "PDF" : "Markdown", new List<string> { extension });
                picker.SuggestedFileName = Path.GetFileNameWithoutExtension(name);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                completion.TrySetResult(file?.Path);
            }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new OperationCanceledException());
        return completion.Task.WaitAsync(cancellation);
    }

    private static void OpenShell(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    private static async Task SeedSmokeAsync(NativeWorkspace workspace, SchoolSyncRuntime runtime)
    {
        const string owner = "synthetic-smoke";
        const string term = "2026-20271";
        var store = workspace.RegisterAuthenticatedAccount(owner);
        var courses = Enumerable.Range(1, 100).Select(index =>
        {
            var id = $"smoke-{index:000}";
            return new RecordWrite("course", id, new { id, semester = term, code = id, name = $"合成课程 {index:000}" });
        });
        store.PutBatch([new("meta", "semester", new { id = term }), .. courses]);
        await runtime.SelectAccountAsync(owner);
    }

    private async Task FinishSmokeAsync(CoreWebView2? web)
    {
        if (smokeFinished) return;
        smokeFinished = true;
        var ok = false;
        double? readyElapsedMs = null;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var condition = seededSmoke
            ? "document.querySelectorAll('main .course-card').length === 100"
            : "document.querySelector('main h1')?.textContent === '本学期课程' && document.querySelector('main')?.textContent.includes('尚无本地课程。')";
        try
        {
            // The empty account heading and login action appear only after
            // React loads the package and receives a successful bridge reply.
            for (var i = 0; web != null && i < 40; i++)
            {
                var result = await web.ExecuteScriptAsync(condition);
                if (result == "true") { ok = true; readyElapsedMs = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds; break; }
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch { }
        try { File.WriteAllText(smokeReport!, JsonSerializer.Serialize(new { ok, workspace = true, seeded = seededSmoke, ready_elapsed_ms = readyElapsedMs })); }
        finally
        {
            // Initialization may still be running when it reports a failure.
            DispatcherQueue.TryEnqueue(() => _ = ShutdownAsync());
        }
    }
    private void JobChanged(JobRecord job) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!shuttingDown && browser.CoreWebView2 is { } web && WorkspaceBridge.IsWorkspaceDocument(web.Source))
            web.PostWebMessageAsJson(JsonSerializer.Serialize(new { v = 1, @event = "changed", owner = job.Owner, revision = job.Revision }));
    });
    private void WorkspaceChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!shuttingDown && browser.CoreWebView2 is { } web && WorkspaceBridge.IsWorkspaceDocument(web.Source))
            web.PostWebMessageAsJson("{\"v\":1,\"event\":\"workspace_changed\"}");
    });
    private Task<SchoolConnection> OpenSchoolAsync(CancellationToken cancellation)
    {
        var result = new TaskCompletionSource<SchoolConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            SchoolBrowserWindow? school = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (shuttingDown || workspace == null) throw new OperationCanceledException();
                school = new SchoolBrowserWindow(workspace.Paths);
                result.TrySetResult(await school.SignInAsync(cancellation));
            }
            catch (OperationCanceledException) { school?.Dispose(); result.TrySetCanceled(); }
            catch (Exception error) { school?.Dispose(); result.TrySetException(error); }
        })) result.TrySetException(new InvalidOperationException("应用窗口已关闭。"));
        return result.Task;
    }
    private async Task ShutdownAsync()
    {
        shuttingDown = true; browser.IsHitTestVisible = false; status.Visibility = Visibility.Visible; status.Text = "正在停止任务…";
        lifetime.Cancel();
        try
        {
            if (initialization != null) await initialization;
            if (login != null) { login.Changed -= WorkspaceChanged; await login.DisposeAsync(); }
            if (runtime != null)
            {
                runtime.Jobs.Changed -= JobChanged;
                if (!await runtime.Jobs.StopAsync()) { shuttingDown = false; browser.IsHitTestVisible = true; status.Text = "任务尚未退出，请稍后再次关闭窗口。"; return; }
                await runtime.DisposeAsync();
            }
            if (images != null) await images.DisposeAsync();
            modelTransport?.Dispose();
            browser.Close(); workspace?.Dispose();
            profile?.Dispose();
            readyToClose = true; Close();
        }
        catch { shuttingDown = false; status.Text = "仍有任务正在释放，请稍后再次关闭窗口。"; }
    }
}
