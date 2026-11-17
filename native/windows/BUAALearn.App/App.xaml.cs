using Microsoft.UI.Xaml;
using BUAALearn.Platform;
using System.Text.Json;

namespace BUAALearn.App;

public partial class App : Application
{
    private Window? window;
    private Mutex? installerLease;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments is [_, "--legacy-migration", var source, var account, var mode, var result])
        {
            if (mode is not ("preview" or "apply")) { Environment.ExitCode = 2; Exit(); return; }
            try
            {
                using var workspace = NativeWorkspace.Open();
                var report = LegacyMigration.Run(workspace, source, account, mode == "apply");
                WriteMigrationResult(result, report);
                Environment.ExitCode = report.Conflicts.Any(x => x.Blocking) ? 2 : 0;
            }
            catch (Exception error)
            {
                WriteMigrationResult(result, new { error = error is ArgumentException or BUAALearn.CoreInterop.CoreException
                    ? error.Message : "迁移未完成，请检查数据库、目录和磁盘空间。" });
                Environment.ExitCode = 1;
            }
            Exit(); return;
        }
        installerLease = new Mutex(false, @"Local\BUAALearnNativeRunning");
        var seededIndex = Array.IndexOf(arguments, "--workspace-seeded-smoke-report");
        var smokeIndex = seededIndex >= 0 ? seededIndex : Array.IndexOf(arguments, "--workspace-smoke-report");
        if (smokeIndex >= 0 && smokeIndex + 1 < arguments.Length)
            window = new WorkspaceWindow(Path.GetFullPath(arguments[smokeIndex + 1]), seededIndex >= 0);
        else window = arguments.Contains("--probe", StringComparer.Ordinal) || arguments.Contains("--smoke-report", StringComparer.Ordinal)
            ? new ProbeWindow() : new WorkspaceWindow();
        window.Activate();
    }

    private static void WriteMigrationResult(string path, object result)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, result, new JsonSerializerOptions { WriteIndented = true });
    }
}
