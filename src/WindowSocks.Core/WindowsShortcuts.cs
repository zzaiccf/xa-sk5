using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowSocks.Core;

public static class WindowsShortcuts
{
    public const string AppId = "XIAOMAO.xa-sk5";
    private const string Preferences = @"Software\WindowSocks\Shell";
    public static string DesktopForUser(RegistryKey user)
    {
        using var folders = user.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
        string path = folders?.GetValue("Desktop") as string ?? "";
        if (!Path.IsPathFullyQualified(path) || path.Contains('%')) throw new IOException("無法讀取使用者的桌面路徑。");
        return path;
    }
    public static void Initialize(RegistryKey user, string executable, string? desktop = null, string? programs = null)
    {
        using var settings = user.CreateSubKey(Preferences);
        string link = Path.Combine(desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "xa sk5.lnk");
        // A manually removed shortcut stays removed on subsequent launches/upgrades.
        if (settings.GetValue("DesktopInitialized") is null || File.Exists(link)) Create(link, executable);
        Create(Path.Combine(programs ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), "xa sk5.lnk"), executable);
        settings.SetValue("DesktopInitialized", 1, RegistryValueKind.DWord);
    }
    public static void CreateDesktop(string executable) => Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "xa sk5.lnk"), executable);
    public static void RemoveDesktopForDirectory(RegistryKey user, string directory)
    {
        string path = Path.Combine(DesktopForUser(user), "xa sk5.lnk");
        if (File.Exists(path) && Path.GetDirectoryName(Target(path))?.Equals(Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase) == true) File.Delete(path);
    }
    public static string Target(string shortcut)
    {
        object? shell = null, link = null;
        try {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            link = ((dynamic)shell!).CreateShortcut(shortcut);
            return (string)((dynamic)link).TargetPath;
        } finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }
    public static void Create(string shortcut, string executable)
    {
        _ = StartupConfiguration.Command(executable); // Validate the product executable and fully qualified path.
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 xa sk5 主程式。", executable);
        if (File.Exists(shortcut) && Target(shortcut).Equals(executable, StringComparison.OrdinalIgnoreCase)) { SetAppId(shortcut); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        object? shell = null, link = null;
        try {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            link = ((dynamic)shell!).CreateShortcut(shortcut);
            dynamic value = link; value.TargetPath = executable; value.WorkingDirectory = Path.GetDirectoryName(executable);
            value.Description = "xa sk5"; value.IconLocation = executable + ",0"; value.Save();
        } finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        SetAppId(shortcut);
    }
    public static void SetCurrentAppId() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
    private static void SetAppId(string shortcut)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(shortcut, IntPtr.Zero, 2, ref iid, out var store));
        var key = new PropertyKey { Format = new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), Id = 5 }; // PKEY_AppUserModel_ID
        var value = new PropertyValue { Type = 31, Text = Marshal.StringToCoTaskMemUni(AppId) };
        try { store.SetValue(ref key, ref value); store.Commit(); }
        finally { Marshal.FreeCoTaskMem(value.Text); Marshal.FinalReleaseComObject(store); }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out PropertyValue value);
        void SetValue(ref PropertyKey key, ref PropertyValue value); void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr context, int flags, ref Guid iid, out IPropertyStore store);
}
