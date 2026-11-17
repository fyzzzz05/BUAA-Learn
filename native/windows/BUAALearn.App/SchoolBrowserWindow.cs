using System.Text.Json;
using BUAALearn.CoreInterop;
using BUAALearn.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace BUAALearn.App;

/// No host objects or web message bridge is ever enabled on school content.
/// Remains hidden after portal login, and reopens only for a recording challenge.
internal sealed class SchoolBrowserWindow : Window, IRecordingAuthorizer, IDisposable
{
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { Text = "请完成学校登录。", Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
    private readonly BrowserProfileLease profile;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? initialization;
    private CancellationTokenSource? active;
    private bool disposed;
    private bool closeAllowed;
    private int disposalRequested;
    private string? owner;
    private string? recordingId;
    private string? expectedRecordingEntry;
    private bool recordingHandshake;
    public SchoolBrowserWindow(WorkspacePaths paths)
    {
        Title = "BUAA Learn · 连接学校"; AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 760));
        profile = BrowserProfileLease.Create(paths, "school");
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(status); Grid.SetRow(browser, 1); root.Children.Add(browser); Content = root;
        root.Loaded += (_, _) => loaded.TrySetResult();
        AppWindow.Closing += (_, e) =>
        {
            if (closeAllowed) return;
            e.Cancel = true; active?.Cancel(); AppWindow.Hide();
        };
    }
    private async Task InitializeAsync()
    {
        Activate(); await loaded.Task;
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile.Path, new CoreWebView2EnvironmentOptions());
        var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
        await browser.EnsureCoreWebView2Async(environment, options);
        lifetime.Token.ThrowIfCancellationRequested();
        var web = browser.CoreWebView2;
        web.Settings.IsWebMessageEnabled = false; web.Settings.AreHostObjectsAllowed = false; web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsPasswordAutosaveEnabled = false; web.Settings.IsGeneralAutofillEnabled = false;
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        web.DownloadStarting += (_, e) => e.Cancel = true;
        web.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
            if (LoginProtocol.UpgradeRecording(uri) is { } secure) { e.Cancel = true; DispatcherQueue.TryEnqueue(() => { if (!disposed) web.Navigate(secure.AbsoluteUri); }); return; }
            if (!LoginProtocol.IsAllowed(uri)) { e.Cancel = true; status.Text = "学校跳转地址不受支持，请关闭窗口后重试。"; return; }
            if (uri.AbsoluteUri == expectedRecordingEntry) recordingHandshake = true;
            if (recordingHandshake) recordingId = LoginProtocol.CourseId(uri) ?? recordingId;
        };
        web.FrameNavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !LoginProtocol.IsAllowed(uri)) e.Cancel = true; };
        web.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && LoginProtocol.IsAllowed(uri)) web.Navigate(uri.AbsoluteUri); };
    }
    // All WinUI/WebView calls run on this window's dispatcher. No native callback
    // waits synchronously for the UI; the queue awaits this Task asynchronously.
    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () => { try { result.TrySetResult(await action()); } catch (OperationCanceledException) { result.TrySetCanceled(); } catch (Exception e) { result.TrySetException(e); } }))
            result.TrySetException(new InvalidOperationException("学校窗口已关闭。"));
        return result.Task;
    }
    public Task<SchoolConnection> SignInAsync(CancellationToken cancellation) => OnUiAsync(async () =>
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token); active = operation;
        try
        {
            initialization ??= InitializeAsync(); await initialization.WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            browser.CoreWebView2.Navigate("https://spoc.buaa.edu.cn/spocnewht/cas");
            while (true)
            {
                await Task.Delay(600, operation.Token);
                var web = browser.CoreWebView2;
                if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var origin) || !LoginProtocol.CanCapture(origin)) continue;
                var raw = await web.ExecuteScriptAsync(LoginProtocol.CaptureScript);
                if (!Uri.TryCreate(web.Source, UriKind.Absolute, out origin) || !LoginProtocol.CanCapture(origin)) continue;
                using var document = JsonDocument.Parse(raw); var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object) continue;
                if (data.GetProperty("role").GetString() != "01") throw new CoreException("请使用学校学生角色。");
                operation.Token.ThrowIfCancellationRequested();
                owner = data.GetProperty("owner").GetString()!;
                var connection = new SchoolConnection(owner, data.GetProperty("token").GetString()!, data.GetProperty("refresh").GetString()!, "01", this, lifetime.Token, sessionResources: this);
                AppWindow.Hide(); return connection;
            }
        }
        finally { active = null; if (!disposed) AppWindow.Hide(); }
    });
    public Task<RecordingAuthorization> AuthorizeAsync(RecordingChallenge challenge, CancellationToken cancellation) => OnUiAsync(async () =>
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token); operation.CancelAfter(TimeSpan.FromSeconds(90)); active = operation;
        try
        {
            if (owner != challenge.Owner || disposed) throw new CoreException("学校账号状态已改变。");
            operation.Token.ThrowIfCancellationRequested(); recordingId = null; recordingHandshake = false;
            browser.CoreWebView2.Stop(); expectedRecordingEntry = challenge.EntryUri.AbsoluteUri;
            // Do not reuse an earlier course's bearer when observing this handshake.
            foreach (var cookie in await browser.CoreWebView2.CookieManager.GetCookiesAsync(null))
                if (LoginProtocol.RecordingBearer(cookie.Name, cookie.Domain, cookie.Value) != null) browser.CoreWebView2.CookieManager.DeleteCookie(cookie);
            status.Text = "正在等待录播授权。"; Activate(); browser.CoreWebView2.Navigate(challenge.EntryUri.AbsoluteUri);
            while (true)
            {
                await Task.Delay(500, operation.Token);
                string? bearer = null;
                foreach (var cookie in await browser.CoreWebView2.CookieManager.GetCookiesAsync(null)) bearer ??= LoginProtocol.RecordingBearer(cookie.Name, cookie.Domain, cookie.Value);
                if (bearer != null && recordingId != null) return new RecordingAuthorization(recordingId, bearer);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new RecordingAuthorizationCancelledException(); }
        finally { active = null; expectedRecordingEntry = null; recordingHandshake = false; if (!disposed) AppWindow.Hide(); }
    });
    public void Dispose()
    {
        // Connection disposal may happen on a worker; cancellation is immediate,
        // native browser destruction and profile cleanup stay on the UI thread.
        if (Interlocked.Exchange(ref disposalRequested, 1) != 0) return;
        try { lifetime.Cancel(); } catch (AggregateException) { }
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (disposed) return; disposed = true;
            if (initialization != null) try { await initialization; } catch { }
            browser.Close(); closeAllowed = true; Close(); profile.Dispose(); lifetime.Dispose();
        });
    }
}
