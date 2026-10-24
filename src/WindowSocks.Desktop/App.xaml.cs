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
    private EventWaitHandle? exitRequest;
    private RegisteredWaitHandle? exitWait;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        string name = @"Local\WindowSocks.Desktop." + WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(false, name);
        try { ownsInstance = instance.WaitOne(0); } catch (AbandonedMutexException) { ownsInstance = true; }
        var intent = DesktopLaunch.Resolve(e.Args, ownsInstance);
        if (intent is DesktopLaunchIntent.Activate or DesktopLaunchIntent.ExitExisting) {
            try { using var existing = EventWaitHandle.OpenExisting(name + (intent == DesktopLaunchIntent.ExitExisting ? ".Exit" : ".Activate")); existing.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            instance.Dispose(); instance = null; Shutdown(); return;
        }
        // A pinned taskbar Exit task must not start the service when no UI is running.
        if (intent == DesktopLaunchIntent.NothingToExit) { Shutdown(); return; }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(new Action(() => {
            if (MainWindow is { } window) { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }
        })), null, Timeout.Infinite, false);
        exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Exit");
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitRequest, (_, _) => Dispatcher.BeginInvoke(new Action(() => {
            if (MainWindow is MainWindow window) window.RequestExit();
        })), null, Timeout.Infinite, false);
        // A control panel has no GPU-intensive content; software rendering also works over remote desktops.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        WindowsShortcuts.SetCurrentAppId();
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null); activation?.Dispose(); exitWait?.Unregister(null); exitRequest?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex(); instance?.Dispose(); base.OnExit(e);
    }
}
