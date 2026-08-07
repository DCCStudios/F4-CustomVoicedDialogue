using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CustomVoicedDialogue.Server.Providers;

namespace CustomVoicedDialogue.App;

/// <summary>
/// First-run wizard: service pick (with local auto-detection) → settings
/// with a test-gated advance → player voice with preview → done.
/// </summary>
public partial class WizardWindow : Window
{
    private enum Step
    {
        Provider,
        Configure,
        Voice,
        Done,
    }

    private readonly AudioPreview _preview = new();
    private readonly ProviderSettingsPanel _settingsEditor;
    private Step _step = Step.Provider;
    private ITtsProvider? _selected;
    private bool _testPassed;

    public WizardWindow()
    {
        InitializeComponent();
        _settingsEditor = new ProviderSettingsPanel(ConfigurePanel);
        Loaded += async (_, _) => await BuildProviderListAsync();
        Closed += (_, _) => _preview.Dispose();
        ShowStep(Step.Provider);
    }

    private async Task BuildProviderListAsync()
    {
        StepTitle.Text = "1 · Choose a voice service";
        var detected = await DetectLocalServicesAsync();

        ProviderList.Items.Clear();
        foreach (var group in App.Providers.All.GroupBy(p => p.IsCloud).OrderBy(g => g.Key))
        {
            ProviderList.Items.Add(new TextBlock
            {
                Text = group.Key ? "Cloud services (API key, best quality)" : "Local services (free, run on your PC)",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 8, 0, 4),
            });
            foreach (var provider in group.OrderBy(p => p.DisplayName))
            {
                var isDetected = provider.DefaultLocalPort is int port && detected.Contains(port);
                var button = new RadioButton
                {
                    GroupName = "provider",
                    Margin = new Thickness(8, 2, 0, 2),
                    Foreground = Brushes.White,
                    Tag = provider,
                    Content = new TextBlock
                    {
                        Text = provider.DisplayName + (isDetected ? "   ● running — ready to use" : ""),
                        Foreground = isDetected ? Brushes.LimeGreen : Brushes.White,
                    },
                };
                button.Checked += (_, _) =>
                {
                    _selected = provider;
                    NextButton.IsEnabled = true;
                };
                ProviderList.Items.Add(button);
            }
        }
    }

    /// <summary>Probes the known default ports of local services.</summary>
    private static async Task<HashSet<int>> DetectLocalServicesAsync()
    {
        var ports = App.Providers.All
            .Where(p => p.DefaultLocalPort is not null)
            .Select(p => p.DefaultLocalPort!.Value)
            .Distinct();
        var detected = new HashSet<int>();
        var probes = ports.Select(async port =>
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync("127.0.0.1", port);
                if (await Task.WhenAny(connect, Task.Delay(400)) == connect && client.Connected)
                {
                    lock (detected)
                    {
                        detected.Add(port);
                    }
                }
            }
            catch (Exception)
            {
            }
        });
        await Task.WhenAll(probes);
        return detected;
    }

    private void ShowStep(Step step)
    {
        _step = step;
        StepProvider.Visibility = step == Step.Provider ? Visibility.Visible : Visibility.Collapsed;
        StepConfigure.Visibility = step == Step.Configure ? Visibility.Visible : Visibility.Collapsed;
        StepVoice.Visibility = step == Step.Voice ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step == Step.Provider ? Visibility.Collapsed : Visibility.Visible;

        switch (step)
        {
            case Step.Provider:
                StepTitle.Text = "1 · Choose a voice service";
                NextButton.Content = "Next";
                NextButton.IsEnabled = _selected is not null;
                break;
            case Step.Configure:
                StepTitle.Text = $"2 · Set up {_selected!.DisplayName}";
                _settingsEditor.Show(_selected, App.Config);
                NextButton.Content = "Next";
                NextButton.IsEnabled = _testPassed;
                break;
            case Step.Voice:
                StepTitle.Text = "3 · Pick your character's voice";
                NextButton.Content = "Next";
                NextButton.IsEnabled = true;
                _ = PopulateVoicesAsync();
                break;
            case Step.Done:
                StepTitle.Text = "4 · Ready to play";
                NextButton.Content = "Finish";
                NextButton.IsEnabled = true;
                break;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        var previous = _step switch
        {
            Step.Configure => Step.Provider,
            Step.Voice => Step.Configure,
            Step.Done => Step.Voice,
            _ => Step.Provider,
        };
        ShowStep(previous);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Provider:
                _testPassed = false;
                ShowStep(Step.Configure);
                break;
            case Step.Configure:
                CommitSettings();
                ShowStep(Step.Voice);
                break;
            case Step.Voice:
                App.Config.PlayerVoice = WizardVoiceCombo.Text.Trim();
                App.Config.Save();
                ShowStep(Step.Done);
                break;
            case Step.Done:
                App.Config.FirstRunCompleted = true;
                App.Config.StartServerOnLaunch = true;
                App.Config.Save();
                Close();
                break;
        }
    }

    private void CommitSettings()
    {
        if (_selected is null)
        {
            return;
        }
        App.Config.StoreSettings(_selected, _settingsEditor.Collect());
        App.Config.Provider = _selected.Id;
        App.Config.Save();
        App.Synthesis.InvalidateVoiceCache();
    }

    private async void OnWizardTest(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }
        WizardTestButton.IsEnabled = false;
        WizardTestResult.Text = "synthesizing…";
        try
        {
            CommitSettings();
            var settings = App.Config.SettingsFor(_selected);
            var raw = await Task.Run(() => _selected.SynthesizeAsync(
                "If you can hear this, your voice service is working.", "", settings, CancellationToken.None));
            var wav = Server.Audio.AudioPipeline.NormalizeToGameWav(raw);
            var validation = Server.Audio.AudioValidator.Validate(wav, "If you can hear this, your voice service is working.");
            if (!validation.Ok)
            {
                WizardTestResult.Text = $"✗ {validation.Failure}";
                WizardTestResult.Foreground = Brushes.OrangeRed;
                return;
            }
            _preview.Play(wav);
            _testPassed = true;
            NextButton.IsEnabled = true;
            WizardTestResult.Text = "✓ working — you should hear the test line now";
            WizardTestResult.Foreground = Brushes.LimeGreen;
        }
        catch (Exception exception)
        {
            WizardTestResult.Text = $"✗ {exception.Message}";
            WizardTestResult.Foreground = Brushes.OrangeRed;
        }
        finally
        {
            WizardTestButton.IsEnabled = true;
        }
    }

    private async Task PopulateVoicesAsync()
    {
        if (_selected is null)
        {
            return;
        }
        try
        {
            var voices = await _selected.ListVoicesAsync(App.Config.SettingsFor(_selected), CancellationToken.None);
            WizardVoiceCombo.Items.Clear();
            foreach (var voice in voices)
            {
                WizardVoiceCombo.Items.Add(voice.Id);
            }
            if (WizardVoiceCombo.Items.Count > 0)
            {
                WizardVoiceCombo.SelectedIndex = 0;
            }
        }
        catch (Exception)
        {
            // Editable combo still lets the user type a voice id.
        }
    }

    private async void OnWizardPreview(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }
        WizardVoiceResult.Text = "synthesizing…";
        try
        {
            var settings = App.Config.SettingsFor(_selected);
            var voice = WizardVoiceCombo.Text;
            var raw = await Task.Run(() => _selected.SynthesizeAsync(
                "I've been looking for this vault for weeks.", voice, settings, CancellationToken.None));
            _preview.Play(Server.Audio.AudioPipeline.NormalizeToGameWav(raw));
            WizardVoiceResult.Text = $"playing '{voice}'";
        }
        catch (Exception exception)
        {
            WizardVoiceResult.Text = $"✗ {exception.Message}";
        }
    }
}
