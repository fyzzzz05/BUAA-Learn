using System.Text.Json;

namespace BUAALearn.CoreInterop;

public static class CoreLegacy
{
    public static void Snapshot(string source, string target, bool requireIdle) =>
        CoreBridge.Decode(NativeMethods.buaa_legacy_snapshot(CoreBridge.Text(source), CoreBridge.Text(target), requireIdle ? 1u : 0u));
    public static JsonElement Rows(string source, string account) =>
        CoreBridge.Decode(NativeMethods.buaa_legacy_rows(CoreBridge.Text(source), CoreBridge.Text(account)));
}
