using Microsoft.Win32.SafeHandles;

namespace BUAALearn.CoreInterop;

public sealed class CoreSlidePdf : IDisposable
{
    private readonly PdfHandle handle;
    public CoreSlidePdf(string path, string snapshot)
    {
        CoreBridge.CheckAbi();
        handle = new(NativeMethods.buaa_pdf_create(CoreBridge.Text(path), CoreBridge.Text(snapshot)));
        if (handle.IsInvalid) { handle.Dispose(); throw new CoreException("无法创建 PPT PDF 临时文件。"); }
    }
    public void Add(byte[] bytes, ulong seconds) =>
        CoreBridge.Decode(NativeMethods.buaa_pdf_add(handle, bytes, (uint)bytes.Length, seconds));
    public int Finish() => CoreBridge.Decode(NativeMethods.buaa_pdf_finish(handle)).GetInt32();
    public void Dispose() => handle.Dispose();
}

internal sealed class PdfHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal PdfHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { NativeMethods.buaa_pdf_close(handle); return true; }
}
