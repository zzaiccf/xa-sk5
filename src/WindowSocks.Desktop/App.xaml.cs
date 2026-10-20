using System;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using WindowSocks.Core;
namespace WindowSocks.Desktop;
public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        string name = @"Local\WindowSocks.Desktop." + WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(false, name);
        try { ownsInstance = instance.WaitOne(0); } catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance) {
            try { using var existing = EventWaitHandle.OpenExisting(name + ".Activate"); existing.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            instance.Dispose(); instance = null; Shutdown(); return;
        }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(new Action(() => {
            if (MainWindow is { } window) { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }
        })), null, Timeout.Infinite, false);
        // A control panel has no GPU-intensive content; software rendering also works over remote desktops.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        WindowsShortcuts.SetCurrentAppId();
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null); activation?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex(); instance?.Dispose(); base.OnExit(e);
    }
}
