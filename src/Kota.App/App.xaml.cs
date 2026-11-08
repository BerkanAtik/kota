using System;
using System.IO;
using System.Threading;
using System.Windows;
using Kota.Core;

namespace Kota.App;

public partial class App : Application
{
    Mutex? _singleInstance;
    bool _ownsMutex;
    volatile bool _exiting;
    EventWaitHandle? _showSignal;
    TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\Kota.SingleInstance", out _ownsMutex);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Kota.ShowFlyout");
        if (!_ownsMutex)
        {
            // Already running: launching Kota again (e.g. from the Start menu) opens the flyout.
            _showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(() => _tray?.ShowFlyout());
            }
        }) { IsBackground = true, Name = "Kota show listener" };
        listener.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true; // a tray app should keep running
        };

        var settings = Settings.Load();
        L.Apply(settings.Language);
        Theme.Apply(Resources);
        StartupRegistration.RepairPathIfEnabled();

        _tray = new TrayController(settings);
        _tray.Start();
        if (Array.Exists(e.Args, a => a.Equals("--show", StringComparison.OrdinalIgnoreCase))) _tray.ShowFlyout();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        if (_ownsMutex) _showSignal?.Set();
        _tray?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(KnownPaths.KotaDataDir);
            var path = Path.Combine(KnownPaths.KotaDataDir, "kota.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:u}] {ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
