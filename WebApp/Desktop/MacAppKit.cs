using System.Runtime.InteropServices;
using Photino.NET;
namespace PereneTts.Desktop;

/// <summary>The few AppKit calls Photino.NET 4.0.16 / Photino.Native 4.0.22 do not provide on macOS:
/// a save panel with a suggested file name, and close/quit confirmation. Photino calls its closing handler from
/// windowWillClose: (too late to cancel) and its Quit menu calls terminate: directly, so the hooks below add
/// windowShouldClose: and applicationShouldTerminate: to Photino's own delegate classes before they are used.
/// Every call must run on the main thread.</summary>
internal static class MacAppKit
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long ModalResponseOk = 1;
    private const nuint TerminateCancel = 0, TerminateNow = 1;

    [DllImport(ObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument, [MarshalAs(UnmanagedType.U1)] bool flag);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFlag(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool flag);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendKeyword(IntPtr receiver, IntPtr selector, uint keyword);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern uint SendCode(IntPtr receiver, IntPtr selector);

    private delegate byte ShouldCloseImplementation(IntPtr self, IntPtr selector, IntPtr sender);
    private delegate nuint ShouldTerminateImplementation(IntPtr self, IntPtr selector, IntPtr sender);
    // Native code keeps these function pointers, so the delegates must live as long as the process.
    private static ShouldCloseImplementation? shouldClose;
    private static ShouldTerminateImplementation? shouldTerminate;

    /// <summary>Modal NSSavePanel starting in directory with suggestedName filled in; null when cancelled.</summary>
    public static string? ShowSavePanel(string title, string directory, string suggestedName)
    {
        var panel = Send(objc_getClass("NSSavePanel"), Selector("savePanel"));
        Send(panel, Selector("setTitle:"), NSString(title));
        Send(panel, Selector("setNameFieldStringValue:"), NSString(suggestedName));
        Send(panel, Selector("setDirectoryURL:"), Send(objc_getClass("NSURL"), Selector("fileURLWithPath:isDirectory:"), NSString(directory), true));
        SendFlag(panel, Selector("setCanCreateDirectories:"), true);
        if (SendLong(panel, Selector("runModal")) != ModalResponseOk) return null;
        var url = Send(panel, Selector("URL"));
        var path = url == IntPtr.Zero ? IntPtr.Zero : Send(url, Selector("path"));
        return path == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(path, Selector("UTF8String")));
    }

    /// <summary>Call before PhotinoWindow.WaitForClose creates the app delegate and window.
    /// windowShouldClose returns whether the window may close now; applicationShouldTerminate receives whether
    /// macOS itself is quitting the app (log out, restart, shut down) and returns whether it may quit now.
    /// Returns false when the hooks could not be added; closing then quits without confirmation.</summary>
    public static bool InstallCloseHooks(Func<bool> windowShouldClose, Func<bool, bool> applicationShouldTerminate)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (!NativeLibrary.TryLoad("Photino.Native", typeof(PhotinoWindow).Assembly, null, out _)) return false;
        var windowDelegate = objc_getClass("WindowDelegate");
        var appDelegate = objc_getClass("AppDelegate");
        if (windowDelegate == IntPtr.Zero || appDelegate == IntPtr.Zero) return false;
        shouldClose = (_, _, _) => Safely(windowShouldClose) ? (byte)1 : (byte)0;
        shouldTerminate = (_, _, _) => Safely(() => applicationShouldTerminate(IsSystemQuit())) ? TerminateNow : TerminateCancel;
        var closeAdded = class_addMethod(windowDelegate, Selector("windowShouldClose:"), Marshal.GetFunctionPointerForDelegate(shouldClose), "B@:@");
        var quitAdded = class_addMethod(appDelegate, Selector("applicationShouldTerminate:"), Marshal.GetFunctionPointerForDelegate(shouldTerminate), "Q@:@");
        return closeAdded && quitAdded;
    }

    // An exception must never cross back into AppKit; allowing the close is the safe default.
    private static bool Safely(Func<bool> decide)
    {
        try { return decide(); }
        catch (Exception) { return true; }
    }

    /// <summary>A quit Apple event with a reason ('why?') comes from log out, restart or shut down, where no dialog can be shown.
    /// ⌘Q has no current Apple event, and Dock → Quit has no reason.</summary>
    private static bool IsSystemQuit()
    {
        var manager = Send(objc_getClass("NSAppleEventManager"), Selector("sharedAppleEventManager"));
        var appleEvent = manager == IntPtr.Zero ? IntPtr.Zero : Send(manager, Selector("currentAppleEvent"));
        if (appleEvent == IntPtr.Zero) return false;
        var reason = SendKeyword(appleEvent, Selector("attributeDescriptorForKeyword:"), FourCharCode("why?"));
        return reason != IntPtr.Zero && SendCode(reason, Selector("enumCodeValue")) != 0;
    }

    private static uint FourCharCode(string code) => (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);
    private static IntPtr Selector(string name) => sel_registerName(name);
    private static IntPtr NSString(string value) => Send(objc_getClass("NSString"), Selector("stringWithUTF8String:"), value);
}
