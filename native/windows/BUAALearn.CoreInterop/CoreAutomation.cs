using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BUAALearn.CoreInterop;

public static class CoreAutomation
{
    private sealed class Context(Func<JsonElement, object?> callback, CancellationToken cancellation)
    {
        public readonly Func<JsonElement, object?> Callback = callback;
        public readonly CancellationToken Cancellation = cancellation;
        public ExceptionDispatchInfo? Failure;
    }

    private static readonly NativeMethods.Send Send = Invoke;
    private static readonly NativeMethods.Release Release = (_, pointer) => Marshal.FreeCoTaskMem(pointer);

    public static JsonElement Plan(JsonElement rule, JsonElement rows, DateTimeOffset now)
    {
        CoreBridge.CheckAbi();
        return CoreBridge.Decode(NativeMethods.buaa_core_plan_automation(rule.GetRawText(), rows.GetRawText(), now.ToString("O")));
    }

    public static JsonElement Run(string course, JsonElement rows, DateTimeOffset now,
        Func<JsonElement, object?> callback, CancellationToken cancellation)
    {
        CoreBridge.CheckAbi();
        var context = new Context(callback, cancellation);
        var root = GCHandle.Alloc(context);
        nint result = 0;
        try
        {
            result = NativeMethods.buaa_core_run_automation(GCHandle.ToIntPtr(root), Send, Release,
                CoreBridge.Text(course), rows.GetRawText(), now.ToString("O"));
            cancellation.ThrowIfCancellationRequested();
            context.Failure?.Throw();
            var owned = result;
            result = 0;
            return CoreBridge.Decode(owned);
        }
        finally
        {
            if (result != 0) NativeMethods.buaa_core_string_free(result);
            root.Free();
        }
    }

    private static nint Invoke(nint pointer, nint request)
    {
        var context = (Context)GCHandle.FromIntPtr(pointer).Target!;
        try
        {
            context.Cancellation.ThrowIfCancellationRequested();
            using var input = JsonDocument.Parse(Marshal.PtrToStringUTF8(request) ?? throw new JsonException());
            var result = context.Callback(input.RootElement);
            context.Cancellation.ThrowIfCancellationRequested();
            return Marshal.StringToCoTaskMemUTF8(JsonSerializer.Serialize(result));
        }
        catch (Exception error)
        {
            context.Failure ??= ExceptionDispatchInfo.Capture(error);
            return 0;
        }
    }
}
