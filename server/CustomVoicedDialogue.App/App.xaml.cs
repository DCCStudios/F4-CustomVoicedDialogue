using System.Net.Http;
using System.Threading;
using System.Windows;
using CustomVoicedDialogue.Server;
using CustomVoicedDialogue.Server.Api;
using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Providers;

namespace CustomVoicedDialogue.App;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = null!;
    public static ProviderRegistry Providers { get; private set; } = null!;
    public static SynthesisService Synthesis { get; private set; } = null!;
    public static ServerHost Server { get; private set; } = null!;

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showRequest;

    private const string InstanceMutexName = @"Local\CustomVoicedDialogue.App";
    private const string ShowRequestName = @"Local\CustomVoicedDialogue.App.Show";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only: extra copies cannot bind the port anyway and
        // just pile up as zombie processes.  Launching the app again is how
        // people expect to get the window back, though, so the second copy
        // asks the first to show itself and then leaves quietly — a message
        // box here just opened behind whatever had focus and read as the app
        // failing to start.
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowRequestName, out var running))
                {
                    running.Set();
                    running.Dispose();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The first instance is still starting up; nothing to signal.
            }
            Shutdown();
            return;
        }

        StartShowRequestListener();

        Config = AppConfig.Load();
        Providers = new ProviderRegistry(new HttpClient());
        Synthesis = new SynthesisService(Config, Providers);
        Server = new ServerHost(Config, Synthesis);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "CustomVoicedDialogue error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    /// <summary>Waits for a later launch to ask for the window, and brings it
    /// back — including from the tray or a minimized state.</summary>
    private void StartShowRequestListener()
    {
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showRequest.WaitOne())
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;  // shutting down
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not { } window)
                    {
                        return;
                    }
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                    // A window behind a full-screen game will not come
                    // forward on Activate alone; a topmost flick does it
                    // without leaving the window pinned above everything.
                    window.Topmost = true;
                    window.Topmost = false;
                });
            }
        })
        {
            IsBackground = true,
            Name = "ShowRequestListener",
        };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the server off the UI thread with a bounded wait — a hung
        // graceful shutdown must never keep the window's X from working.
        try
        {
            var server = Server;
            if (server is not null)
            {
                Task.Run(server.StopAsync).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception)
        {
        }

        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (Exception)
        {
        }

        // Belt and braces: if anything (audio device thread, stuck request)
        // still keeps the process alive a few seconds after the last window
        // closed, end it hard.  The thread is background, so it never delays
        // a normal exit.
        new Thread(() =>
        {
            Thread.Sleep(3000);
            Environment.Exit(0);
        })
        {
            IsBackground = true,
        }.Start();

        base.OnExit(e);
    }
}
