using System.Runtime.InteropServices;
using System.Text.Json;

namespace BUAALearn.CoreInterop;

/// Synchronous C ABI callback. The host must call school methods off the UI thread.
/// Request JSON contains credentials; implementations must never log it.
public interface ISchoolTransport
{
    string Send(string request);
}

/// Optional operation scope: cancellation stays in managed code, never in JSON.
public interface IScopedSchoolTransport : ISchoolTransport
{
    IDisposable UseCancellation(CancellationToken cancellation);
}

public sealed class CoreSchool : IDisposable
{
    // Static roots keep function pointers alive even through full collections.
    private static readonly NativeMethods.Send SendCallback = Send;
    private static readonly NativeMethods.Release ReleaseCallback = Release;
    private readonly SchoolHandle handle;
    private readonly object gate = new();
    private ISchoolTransport? transport;

    public CoreSchool(ISchoolTransport transport, string token, string refresh, string role)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.transport = transport;
        CoreBridge.CheckAbi();
        token = CoreBridge.Text(token); refresh = CoreBridge.Text(refresh); role = CoreBridge.Text(role);
        var context = GCHandle.Alloc(transport);
        try
        {
            var pointer = NativeMethods.buaa_school_open(GCHandle.ToIntPtr(context), SendCallback, ReleaseCallback, token, refresh, role);
            if (pointer == 0) throw new CoreException("学校登录信息无效。");
            handle = new SchoolHandle(pointer, context);
        }
        catch { context.Free(); throw; }
    }
    private static nint Send(nint context, nint request)
    {
        try
        {
            var transport = (ISchoolTransport)GCHandle.FromIntPtr(context).Target!;
            var response = transport.Send(Marshal.PtrToStringUTF8(request)!);
            return Marshal.StringToCoTaskMemUTF8(CoreBridge.Text(response));
        }
        catch { return 0; } // No managed exception may cross the native boundary.
    }
    private static void Release(nint context, nint response) => Marshal.FreeCoTaskMem(response);
    private JsonElement Call(Func<SchoolHandle, nint> operation, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            cancellation.ThrowIfCancellationRequested();
            using var scope = (transport as IScopedSchoolTransport)?.UseCancellation(cancellation);
            try
            {
                var result = CoreBridge.Decode(operation(handle));
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch (CoreException) when (cancellation.IsCancellationRequested)
            { throw new OperationCanceledException(cancellation); }
        }
    }
    public JsonElement Semesters(CancellationToken cancellation = default) => Call(NativeMethods.buaa_school_semesters, cancellation);
    public JsonElement Courses(string semester, CancellationToken cancellation = default) => Call(h => NativeMethods.buaa_school_courses(h, CoreBridge.Text(semester)), cancellation);
    public JsonElement Schedule(string start, string end, CancellationToken cancellation = default) => Call(h => NativeMethods.buaa_school_schedule(h, CoreBridge.Text(start), CoreBridge.Text(end)), cancellation);
    public JsonElement RecordingEntry(string course, string task, CancellationToken cancellation = default) => Call(h => NativeMethods.buaa_school_recording_signature(h, CoreBridge.Text(course), CoreBridge.Text(task)), cancellation);
    public void SetRecording(string token, CancellationToken cancellation = default) => Call(h => NativeMethods.buaa_school_set_recording(h, CoreBridge.Text(token)), cancellation);
    public JsonElement Catalogue(string course, CancellationToken cancellation = default) => Call(h => NativeMethods.buaa_school_catalogue(h, CoreBridge.Text(course)), cancellation);
    public JsonElement Materials(string course, string lesson, double duration, CancellationToken cancellation = default) => Call(h => NativeMethods.buaa_school_materials_with_duration(h, CoreBridge.Text(course), CoreBridge.Text(lesson), duration), cancellation);
    public void Dispose() { lock (gate) { handle.Dispose(); transport = null; } }
}
