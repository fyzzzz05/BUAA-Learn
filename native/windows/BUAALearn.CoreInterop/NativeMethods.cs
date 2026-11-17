using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace BUAALearn.CoreInterop;

internal static class NativeMethods
{
    private const string Library = "buaa_core";
    private const CallingConvention C = CallingConvention.Cdecl;
    [UnmanagedFunctionPointer(C)] internal delegate nint Send(nint context, nint request);
    [UnmanagedFunctionPointer(C)] internal delegate void Progress(nint context, nint message);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_ask(nint context, Send send, Release release, [MarshalAs(UnmanagedType.LPUTF8Str)] string input);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_analyze_lesson(nint context, Send send, Release release, Progress progress, [MarshalAs(UnmanagedType.LPUTF8Str)] string input);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_list_notice_groups(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_set_task_done(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, uint done);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_save_analysis_checked(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string analysis, [MarshalAs(UnmanagedType.LPUTF8Str)] string digest);
    [UnmanagedFunctionPointer(C)] internal delegate void Release(nint context, nint response);
    [DllImport(Library, CallingConvention = C)] internal static extern uint buaa_core_abi_version();
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_legacy_snapshot([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint requireIdle);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_legacy_rows([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_plan_automation([MarshalAs(UnmanagedType.LPUTF8Str)] string rule, [MarshalAs(UnmanagedType.LPUTF8Str)] string rows, [MarshalAs(UnmanagedType.LPUTF8Str)] string now);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_run_automation(nint context, Send send, Release release, [MarshalAs(UnmanagedType.LPUTF8Str)] string course, [MarshalAs(UnmanagedType.LPUTF8Str)] string rows, [MarshalAs(UnmanagedType.LPUTF8Str)] string now);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_pdf_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string snapshot);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_pdf_add(PdfHandle handle, [In] byte[] bytes, uint length, ulong seconds);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_pdf_finish(PdfHandle handle);
    [DllImport(Library, CallingConvention = C)] internal static extern void buaa_pdf_close(nint handle);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_inspect_slide_image([In] byte[] bytes, uint length);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_model_request([MarshalAs(UnmanagedType.LPUTF8Str)] string provider, [MarshalAs(UnmanagedType.LPUTF8Str)] string model, [MarshalAs(UnmanagedType.LPUTF8Str)] string system, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_model_parse([MarshalAs(UnmanagedType.LPUTF8Str)] string provider, [MarshalAs(UnmanagedType.LPUTF8Str)] string body);
    [DllImport(Library, CallingConvention = C)] internal static extern void buaa_core_string_free(nint value);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_core_parse_semesters([MarshalAs(UnmanagedType.LPUTF8Str)] string body);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library, CallingConvention = C)] internal static extern void buaa_store_close(nint handle);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_open_versioned([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string backup, out nint handle);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_list(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string kind);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_setting_get(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_setting_put(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string body);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_put_batch(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string rows);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_merge_school(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string rows);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_save_materials(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string course, [MarshalAs(UnmanagedType.LPUTF8Str)] string lesson, [MarshalAs(UnmanagedType.LPUTF8Str)] string body);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_put(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string kind, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string body);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_store_get(StoreHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string kind, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_open(nint context, Send send, Release release, [MarshalAs(UnmanagedType.LPUTF8Str)] string token, [MarshalAs(UnmanagedType.LPUTF8Str)] string refresh, [MarshalAs(UnmanagedType.LPUTF8Str)] string role);
    [DllImport(Library, CallingConvention = C)] internal static extern void buaa_school_close(nint handle);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_semesters(SchoolHandle handle);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_courses(SchoolHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string semester);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_schedule(SchoolHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string start, [MarshalAs(UnmanagedType.LPUTF8Str)] string end);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_recording_signature(SchoolHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string course, [MarshalAs(UnmanagedType.LPUTF8Str)] string task);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_set_recording(SchoolHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string token);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_catalogue(SchoolHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string course);
    [DllImport(Library, CallingConvention = C)] internal static extern nint buaa_school_materials_with_duration(SchoolHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string course, [MarshalAs(UnmanagedType.LPUTF8Str)] string lesson, double duration);
}

internal sealed class StoreHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal StoreHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { NativeMethods.buaa_store_close(handle); return true; }
}

internal sealed class SchoolHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GCHandle context;
    internal SchoolHandle(nint value, GCHandle context) : base(true)
    { SetHandle(value); this.context = context; }
    protected override bool ReleaseHandle()
    {
        // Rust must release its transport before the rooted managed callback context.
        NativeMethods.buaa_school_close(handle);
        if (context.IsAllocated) context.Free();
        return true;
    }
}
