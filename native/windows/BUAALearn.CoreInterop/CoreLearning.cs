using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BUAALearn.CoreInterop;

public interface ICoreModel
{
    JsonElement Complete(JsonElement request, CancellationToken cancellation);
}

public sealed class CoreModelException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// Synchronous worker-only ABI. No callback, borrowed string or model snapshot
/// may escape this invocation; exceptions never cross the native boundary.
public static class CoreLearning
{
    private sealed class Context(ICoreModel model, CancellationToken cancellation, Action<string>? progress)
    {
        public readonly ICoreModel Model = model;
        public readonly CancellationToken Cancellation = cancellation;
        public readonly Action<string>? Progress = progress;
        public ExceptionDispatchInfo? Fatal;
    }
    private static readonly NativeMethods.Send Send = Complete;
    private static readonly NativeMethods.Release Release = (_, pointer) => Marshal.FreeCoTaskMem(pointer);
    private static readonly NativeMethods.Progress Progress = Report;
    private static Context Get(nint pointer) => (Context)GCHandle.FromIntPtr(pointer).Target!;
    private static nint Complete(nint pointer, nint request)
    {
        var state = Get(pointer);
        try
        {
            state.Cancellation.ThrowIfCancellationRequested(); state.Fatal?.Throw();
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(request) ?? throw new JsonException());
            var result = state.Model.Complete(document.RootElement, state.Cancellation);
            state.Cancellation.ThrowIfCancellationRequested();
            return Marshal.StringToCoTaskMemUTF8(JsonSerializer.Serialize(new { ok = true, data = result }));
        }
        catch (Exception error)
        {
            var code = error is CoreModelException model ? model.Code : "failed";
            // Only malformed model output may use the core's bounded repair
            // loop. A transport failure must also abort optional summarization.
            if (code is not ("invalid_json" or "invalid_response")) state.Fatal ??= ExceptionDispatchInfo.Capture(error);
            return Marshal.StringToCoTaskMemUTF8(JsonSerializer.Serialize(new { ok = false, code }));
        }
    }
    private static void Report(nint pointer, nint message)
    {
        var state = Get(pointer);
        try { state.Cancellation.ThrowIfCancellationRequested(); state.Fatal?.Throw(); state.Progress?.Invoke(Marshal.PtrToStringUTF8(message) ?? ""); }
        catch (Exception error) { state.Fatal ??= ExceptionDispatchInfo.Capture(error); }
    }
    public static JsonElement Analyze(ICoreModel model, JsonElement input, CancellationToken cancellation = default, Action<string>? progress = null)
        => Invoke(model, input, cancellation, progress, analyze: true);
    public static JsonElement Ask(ICoreModel model, JsonElement input, CancellationToken cancellation = default)
        => Invoke(model, input, cancellation, null, analyze: false);

    private static JsonElement Invoke(ICoreModel model, JsonElement input, CancellationToken cancellation, Action<string>? progress, bool analyze)
    {
        ArgumentNullException.ThrowIfNull(model); cancellation.ThrowIfCancellationRequested(); CoreBridge.CheckAbi();
        var state = new Context(model, cancellation, progress);
        var handle = GCHandle.Alloc(state);
        nint result = 0;
        try
        {
            var pointer = GCHandle.ToIntPtr(handle);
            var payload = input.GetRawText();
            result = analyze
                ? NativeMethods.buaa_core_analyze_lesson(pointer, Send, Release, Progress, payload)
                : NativeMethods.buaa_core_ask(pointer, Send, Release, payload);
            cancellation.ThrowIfCancellationRequested(); state.Fatal?.Throw();
            var owned = result; result = 0; return CoreBridge.Decode(owned);
        }
        finally { if (result != 0) NativeMethods.buaa_core_string_free(result); handle.Free(); }
    }
}
