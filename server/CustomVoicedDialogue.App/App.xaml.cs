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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only: extra copies cannot bind the port anyway and
        // just pile up as zombie processes.
        _instanceMutex = new Mutex(true, @"Local\CustomVoicedDialogue.App", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "CustomVoicedDialogue is already running — check for its window or look at the existing process.",
                "CustomVoicedDialogue",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
