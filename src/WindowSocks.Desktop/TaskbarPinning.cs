using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WindowSocks.Desktop;

// TaskbarManager's documented WinRT ABI, used directly to retain Windows 10 support
// without requiring a newer Windows SDK runtime on every machine.
// https://learn.microsoft.com/windows/apps/develop/windows-integration/pin-to-taskbar
public static class TaskbarPinning
{
    public sealed record State(bool Supported, bool Pinned, bool Allowed);
    public static async Task<State> ReadAsync()
    {
        if (RequiresToken()) return new(false, false, false);
        using var factory = Factory();
        if (factory is null) return new(false, false, false);
        using var manager = Manager(factory);
        if (!ReadBoolean(manager, 6)) return new(false, false, false);
        bool pinned = await ReadOperation(manager, 8);
        return new(true, pinned, ReadBoolean(manager, 7));
    }
    // Call only from a foreground UI click. Windows owns the user's confirmation.
    public static async Task<bool> RequestAsync()
    {
        using var factory = Factory() ?? throw new NotSupportedException("此 Windows 版本請在工作列圖示按右鍵，選擇「釘選到工作列」。");
        using var manager = Manager(factory);
        return await ReadOperation(manager, 10);
    }
    private static bool RequiresToken()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\LimitedAccessFeatures\com.microsoft.windows.taskbar.pin");
        return key?.GetValue("4096B239A7295B635C090E647E867B5707DA6AB6CB78340B01FE4E0C8F4953D4") is int value && value != 0;
    }
    private static ComPointer? Factory()
    {
        string name = "Windows.UI.Shell.TaskbarManager";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var text));
        try {
            var iid = new Guid("db32ab74-de52-4fe6-b7b6-95ff9f8395df");
            int result = RoGetActivationFactory(text, ref iid, out var pointer);
            if (result < 0) return null;
            var factory = new ComPointer(pointer);
            var marker = new Guid("cdfefd63-e879-4134-b9a7-8283f05f9480");
            if (Marshal.QueryInterface(pointer, in marker, out var support) < 0) { factory.Dispose(); return null; }
            Marshal.Release(support); return factory;
        } finally { WindowsDeleteString(text); }
    }
    private static ComPointer Manager(ComPointer factory)
    {
        Marshal.ThrowExceptionForHR(factory.Method<PointerResult>(6)(factory.Value, out var manager));
        return new(manager);
    }
    private static bool ReadBoolean(ComPointer value, int slot)
    {
        Marshal.ThrowExceptionForHR(value.Method<BooleanResult>(slot)(value.Value, out byte result)); return result != 0;
    }
    private static async Task<bool> ReadOperation(ComPointer manager, int slot)
    {
        Marshal.ThrowExceptionForHR(manager.Method<PointerResult>(slot)(manager.Value, out var pointer));
        using var operation = new ComPointer(pointer);
        var iid = new Guid("00000036-0000-0000-c000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pointer, in iid, out var infoPointer));
        using var info = new ComPointer(infoPointer);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true) {
            Marshal.ThrowExceptionForHR(info.Method<IntegerResult>(7)(info.Value, out int status));
            if (status != 0) {
                // GetResults propagates the operation's real cancellation/error HRESULT.
                return ReadBoolean(operation, 8);
            }
            if (DateTime.UtcNow > deadline) {
                Marshal.ThrowExceptionForHR(info.Method<SimpleResult>(9)(info.Value));
                throw new TimeoutException("工作列釘選未完成，請稍後重試。");
            }
            await Task.Delay(100);
        }
    }
    private sealed class ComPointer(IntPtr value) : IDisposable
    {
        public IntPtr Value { get; private set; } = value;
        public T Method<T>(int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Value), slot * IntPtr.Size));
        public void Dispose() { if (Value != IntPtr.Zero) { Marshal.Release(Value); Value = IntPtr.Zero; } }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PointerResult(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BooleanResult(IntPtr self, out byte result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntegerResult(IntPtr self, out int result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleResult(IntPtr self);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
}
