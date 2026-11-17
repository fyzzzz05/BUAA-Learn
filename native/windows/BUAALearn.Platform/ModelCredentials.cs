using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace BUAALearn.Platform;

public interface IModelCredentialStore
{
    // Caller owns and clears the returned buffer. Never return a vault's backing array.
    byte[]? Read(string target);
    void Write(string target, ReadOnlySpan<byte> value);
    void Delete(string target);
}
public sealed class ModelCredentialException : Exception
{
    public ModelCredentialException() : base("无法访问 Windows 凭据管理器，请检查权限后重试。") { }
}
public sealed class WindowsModelCredentialStore : IModelCredentialStore
{
    private const uint Generic = 1, LocalMachine = 2;
    public const int MaxBytes = 2560;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)] private static extern void CredFree(nint credential);
    private static void Check(string target)
    {
        if (!OperatingSystem.IsWindows()) throw new ModelCredentialException();
        if (!target.StartsWith("BUAALearn/model/", StringComparison.Ordinal) || target.Length > 256) throw new ArgumentException("模型凭据标识无效。");
    }
    public byte[]? Read(string target)
    {
        Check(target);
        if (!CredReadW(target, Generic, 0, out var pointer))
        { if (Marshal.GetLastWin32Error() == 1168) return null; throw new ModelCredentialException(); }
        nint blob = 0; var length = 0;
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            if (value.Type != Generic || value.CredentialBlobSize is 0 or > MaxBytes || value.CredentialBlob == 0) throw new ModelCredentialException();
            blob = value.CredentialBlob; length = (int)value.CredentialBlobSize;
            var bytes = new byte[value.CredentialBlobSize]; Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length); return bytes;
        }
        finally { for (var i = 0; i < length; i++) Marshal.WriteByte(blob, i, 0); CredFree(pointer); }
    }
    public void Write(string target, ReadOnlySpan<byte> value)
    {
        Check(target); if (value.Length is 0 or > MaxBytes) throw new ArgumentException("模型密钥大小无效。");
        var bytes = value.ToArray(); var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new Credential { Type = Generic, TargetName = target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = pin.AddrOfPinnedObject(), Persist = LocalMachine, UserName = "BUAA Learn" };
            if (!CredWriteW(ref credential, 0)) throw new ModelCredentialException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); pin.Free(); }
    }
    public void Delete(string target)
    {
        Check(target); if (!CredDeleteW(target, Generic, 0) && Marshal.GetLastWin32Error() != 1168) throw new ModelCredentialException();
    }
}
internal sealed class ModelSecret(byte[] value) : IDisposable
{
    private byte[]? bytes = value;
    public string Bearer => bytes == null ? throw new ObjectDisposedException(nameof(ModelSecret)) : Encoding.UTF8.GetString(bytes);
    public void Dispose() { var old = Interlocked.Exchange(ref bytes, null); if (old != null) CryptographicOperations.ZeroMemory(old); }
    public override string ToString() => "[模型密钥]";
}
