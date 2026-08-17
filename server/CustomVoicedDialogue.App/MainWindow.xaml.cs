using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CustomVoicedDialogue.Server;
using CustomVoicedDialogue.Server.Lines;
using CustomVoicedDialogue.Server.Providers;
using CustomVoicedDialogue.Server.VoiceMapping;

namespace CustomVoicedDialogue.App;

public partial class MainWindow : Window
{
    private readonly AudioPreview _preview = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly ProviderSettingsPanel _settingsEditor;
    private IReadOnlyList<TtsVoice> _voices = [];

    public sealed class NpcRow
    {
        public string VoiceType { get; set; } = "";
        public string AutoVoice { get; set; } = "";
        public string Override { get; set; } = "";
        public string Accent { get; set; } = Accents.Default;
    }

    public sealed class HistoryRow
    {
        public string Time { get; init; } = "";
        public string Text { get; init; } = "";
        public string Voice { get; init; } = "";
        public string Milliseconds { get; init; } = "";
        public string ResultText { get; init; } = "";
        public string? WavPath { get; init; }
    }

    public sealed class LineRow
    {
        public string VoicePath { get; init; } = "";
        public string Text { get; init; } = "";
        public string TaggedText { get; init; } = "";
        public string Voice { get; init; } = "";
        public int Variant { get; init; }
        public string Scene { get; init; } = "";
        public string CustomPrompt { get; init; } = "";
        public string HealthText { get; init; } = "";
        public string? WavPath { get; init; }
    }

    public MainWindow()
    {
        // Second-instance startup shuts the application down before the
        // services exist; don't touch them from a window that never shows.
        if (App.Server is null)
        {
            InitializeComponent();
            _settingsEditor = new ProviderSettingsPanel(SettingsPanel, TestVoicePanel);
            Loaded += (_, _) => Close();
            return;
        }

        InitializeComponent();
        _settingsEditor = new ProviderSettingsPanel(SettingsPanel, TestVoicePanel);

        PortBox.Text = App.Config.Port.ToString();
        AutoStartCheck.IsChecked = App.Config.StartServerOnLaunch;
        ShoutInCombatCheck.IsChecked = App.Config.ShoutInCombat;
        UpdateCheckBox.IsChecked = App.Config.CheckForUpdates;
        CurrentVersionText.Text = $"CustomVoicedDialogue {UpdateChecker.CurrentVersion}";

        foreach (var provider in App.Providers.All.OrderBy(p => p.IsCloud).ThenBy(p => p.DisplayName))
        {
            ProviderCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{provider.DisplayName}  ({(provider.IsCloud ? "cloud" : "local")})",
                Tag = provider.Id,
            });
        }
        SelectProvider(App.Config.Provider);

        App.Synthesis.Synthesized += entry => Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"{entry.Timestamp:HH:mm:ss}  {(entry.Success ? "ok " : "FAIL")}  {Truncate(entry.Text, 60)}");
            RefreshHistory();
        });

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        // The catalogue re-validates itself on a background heartbeat, so a
        // wav deleted while the app is open shows up without a manual refresh.
        App.Synthesis.Lines.Changed += () => Dispatcher.BeginInvoke(RefreshLines);
        RefreshLines();

        Loaded += async (_, _) =>
        {
            if (!App.Config.FirstRunCompleted)
            {
                var wizard = new WizardWindow { Owner = this };
                wizard.ShowDialog();
                SelectProvider(App.Config.Provider);
            }
            if (App.Config.StartServerOnLaunch)
            {
                await StartServerAsync();
            }
            if (App.Config.CheckForUpdates)
            {
                _ = CheckUpdatesAsync(silent: true);
            }
            RefreshStatus();
            LoadAccentControls();
            _ = RefreshVoicesAsync();
        };
        Closed += (_, _) => _preview.Dispose();
    }

    // ---- status ----------------------------------------------------------

    private void RefreshStatus()
    {
        SetLight(ServerLight, ServerLightText,
            App.Server.Running,
            App.Server.Running ? $"Server listening on 127.0.0.1:{App.Config.Port}" : "Server stopped");
        StartStopButton.Content = App.Server.Running ? "Stop server" : "Start server";

        var provider = App.Providers.Get(App.Config.Provider);
        SetLight(ProviderLight, ProviderLightText,
            provider is not null,
            provider is not null ? $"Provider: {provider.DisplayName}" : "Provider not configured");

        var contact = App.Server.LastGameContact;
        var gameRecent = contact is not null && DateTimeOffset.Now - contact < TimeSpan.FromMinutes(2);
        SetLight(GameLight, GameLightText,
            gameRecent,
            contact is null ? "Game not connected yet" : $"Game last seen {contact:HH:mm:ss}");

        var (files, bytes) = App.Synthesis.Cache.Stats();
        CacheStatsText.Text = $"Audio cache: {files} file(s), {bytes / 1024.0 / 1024.0:F1} MB at {App.Synthesis.Cache.CacheDirectory}";
    }

    private static void SetLight(System.Windows.Shapes.Ellipse light, TextBlock text, bool ok, string message)
    {
        light.Fill = ok ? Brushes.LimeGreen : Brushes.Gray;
        text.Text = message;
    }

    private void AppendLog(string line)
    {
        ServerLog.Items.Insert(0, line);
        while (ServerLog.Items.Count > 200)
        {
            ServerLog.Items.RemoveAt(ServerLog.Items.Count - 1);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    // ---- server tab ------------------------------------------------------

    private async Task StartServerAsync()
    {
        try
        {
            await App.Server.StartAsync();
            AppendLog($"{DateTime.Now:HH:mm:ss}  server started on port {App.Config.Port}");
        }
        catch (Exception exception)
        {
            AppendLog($"{DateTime.Now:HH:mm:ss}  server failed to start: {exception.Message}");
            MessageBox.Show($"The server could not start on port {App.Config.Port}:\n{exception.Message}", "CustomVoicedDialogue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshStatus();
    }

    private async void OnStartStopServer(object sender, RoutedEventArgs e)
    {
        if (App.Server.Running)
        {
            await App.Server.StopAsync();
            AppendLog($"{DateTime.Now:HH:mm:ss}  server stopped");
        }
        else
        {
            await StartServerAsync();
        }
        RefreshStatus();
    }

    private async void OnApplyPort(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Enter a port between 1 and 65535.", "CustomVoicedDialogue");
            return;
        }
        App.Config.Port = port;
        App.Config.Save();
        if (App.Server.Running)
        {
            await App.Server.StopAsync();
            await StartServerAsync();
        }
        MessageBox.Show(
            $"Port set to {port}. The game plugin must use the same port — update iPort in Data\\F4SE\\Plugins\\CustomVoicedDialogue.ini if you changed it from the default.",
            "CustomVoicedDialogue");
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        App.Config.StartServerOnLaunch = AutoStartCheck.IsChecked == true;
        App.Config.Save();
    }

    private void OnShoutInCombatChanged(object sender, RoutedEventArgs e)
    {
        // Deliberately does NOT invalidate existing audio: it is not part of
        // the voice fingerprint, so already-generated lines keep the take
        // they were performed with and only new lines follow the new setting.
        App.Config.ShoutInCombat = ShoutInCombatCheck.IsChecked == true;
        App.Config.Save();
    }

    private void OnOpenCache(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", App.Synthesis.Cache.CacheDirectory) { UseShellExecute = true });

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        var removed = App.Synthesis.Cache.Prune(TimeSpan.Zero);
        AppendLog($"{DateTime.Now:HH:mm:ss}  cleared {removed} cached file(s)");
        RefreshStatus();
    }

    // ---- provider tab ----------------------------------------------------

    private ITtsProvider? SelectedProvider =>
        ProviderCombo.SelectedItem is ComboBoxItem { Tag: string id } ? App.Providers.Get(id) : null;

    private void SelectProvider(string id)
    {
        foreach (ComboBoxItem item in ProviderCombo.Items)
        {
            if (item.Tag is string tag && tag.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                ProviderCombo.SelectedItem = item;
                return;
            }
        }
        if (ProviderCombo.Items.Count > 0 && ProviderCombo.SelectedItem is null)
        {
            ProviderCombo.SelectedIndex = 0;
        }
    }

    private void OnProviderSelected(object sender, SelectionChangedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }
        _settingsEditor.Show(provider, App.Config);
        TestConnectionResult.Text = "";
    }

    /// <summary>Persists the panel's values and makes them active.</summary>
    private ProviderSettings CommitProviderSettings(ITtsProvider provider)
    {
        var values = _settingsEditor.Collect();
        App.Config.StoreSettings(provider, values);
        App.Config.Provider = provider.Id;
        App.Config.Save();
        App.Synthesis.InvalidateVoiceCache();
        return App.Config.SettingsFor(provider);
    }

    private void OnSaveProviderSettings(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }
        // Until now these were only written as a side effect of testing or
        // synthesizing, so a change made and left alone was quietly lost.
        CommitProviderSettings(provider);
        TestConnectionResult.Text = $"✓ saved {provider.DisplayName} settings";
        TestConnectionResult.Foreground = Brushes.LimeGreen;
        RefreshStatus();
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }
        TestConnectionButton.IsEnabled = false;
        TestConnectionResult.Text = "testing…";
        try
        {
            var settings = CommitProviderSettings(provider);
            var result = await provider.TestConnectionAsync(settings, CancellationToken.None);
            TestConnectionResult.Text = $"{(result.Success ? "✓" : "✗")} {result.Message} ({result.Elapsed.TotalMilliseconds:F0} ms)";
            TestConnectionResult.Foreground = result.Success ? Brushes.LimeGreen : Brushes.OrangeRed;
            RefreshStatus();
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    // Emotionally varied sample lines (including written vocalizations and
    // asterisk actions) for exercising the auto-tagger.
    private static readonly string[] SampleTestLines =
    {
        "I've been looking for this vault for weeks.",
        "Get down! They're everywhere!",
        "I... I never wanted any of this to happen.",
        "Hm hmm.",
        "*Sighs* Fine. Have it your way.",
        "You did it! I can't believe you actually did it!",
        "Stay very quiet. Something's moving down there.",
        "Why do I have to pay for this stuff? I'm the Overboss, remember?",
        // These turn on the Situation dropdown rather than on their own
        // words — the same sentence should read very differently to a
        // friend and to someone who was just shooting at you.
        "Thanks for the help. I mean that.",
        "We need to move. Right now.",
        "Don't make me do this.",
        "Put that down before somebody gets hurt.",
        "Nice work back there. Thought we were finished for sure.",
        "I've got one round left, so make it count.",
        "You really thought I wouldn't find out?",
        "Everybody stay calm. Nobody has to get hurt today.",
        "That's close enough. Not another step.",
        "It's over. Just walk away and we forget this happened.",
    };
    private int _sampleTestLineIndex;
    // In-game a line's take is fixed (hashed from its voice path); the
    // test box rolls a fresh take per click so variety can be auditioned.
    private int _testTakeCounter;

    private void OnSampleTestLine(object sender, RoutedEventArgs e)
    {
        _sampleTestLineIndex = (_sampleTestLineIndex + 1) % SampleTestLines.Length;
        TestSynthesisText.Text = SampleTestLines[_sampleTestLineIndex];
        // Hand keyboard focus to the input with the sample selected —
        // otherwise the button keeps focus and typed replacement text goes
        // nowhere, so Synthesize processes the stale sample.
        TestSynthesisText.Focus();
        TestSynthesisText.SelectAll();
    }

    private async void OnTestSynthesis(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }
        TestSynthesisButton.IsEnabled = false;
        TestSynthesisResult.Text = "synthesizing…";
        try
        {
            var text = TestSynthesisText.Text;
            var inputText = text;
            var settings = CommitProviderSettings(provider);
            var stopwatch = Stopwatch.StartNew();

            // The checkbox forces tagging for this test regardless of the
            // saved auto_tag toggle, so steering can be auditioned before
            // committing to a full regeneration.
            string? taggedAs = null;
            string? tagError = null;
            if (TestAutoTagCheck.IsChecked == true && provider is Server.Providers.InworldProvider inworld)
            {
                TestSynthesisResult.Text = "tagging…";
                var tagSettings = new ProviderSettings(
                    new Dictionary<string, string>(settings.Values, StringComparer.OrdinalIgnoreCase) { ["auto_tag"] = "true" });
                var takeSeed = $"test\\take-{_testTakeCounter++}";
                var scene = (TestSceneCombo.SelectedItem as SceneOption ?? SceneContexts.Default).Value;
                var result = await Task.Run(() => inworld.AutoTagDetailedAsync(
                    text, "PlayerVoiceMale01", true, tagSettings, CancellationToken.None, takeSeed,
                    accent: null, accentImperfection: 0, retake: 0, scene: scene,
                    shoutInCombat: App.Config.ShoutInCombat));
                tagError = result.RouterError;
                if (!string.Equals(result.Text, text, StringComparison.Ordinal))
                {
                    taggedAs = result.Text;
                    text = result.Text;
                }
                TestSynthesisResult.Text = "synthesizing…";
            }

            var raw = await Task.Run(() => provider.SynthesizeAsync(text, "", settings, CancellationToken.None));
            var wav = Server.Audio.AudioPipeline.NormalizeToGameWav(raw);
            var validation = Server.Audio.AudioValidator.Validate(wav, text);
            if (!validation.Ok)
            {
                TestSynthesisResult.Text = $"✗ validation failed: {validation.Failure}";
                TestSynthesisResult.Foreground = Brushes.OrangeRed;
                return;
            }
            _preview.Play(wav);
            var tagNote = taggedAs is null ? "" : $"\ntagged as: {taggedAs}";
            if (tagError is not null)
            {
                tagNote += $"\n⚠ auto-tag fell back to rules — {tagError}";
            }
            else if (taggedAs is null && TestAutoTagCheck.IsChecked == true)
            {
                tagNote += "\n(no tags applied)";
            }
            TestSynthesisResult.Text =
                $"✓ \"{Truncate(inputText, 60)}\" — {validation.Duration.TotalSeconds:F1}s of audio in {stopwatch.Elapsed.TotalMilliseconds:F0} ms " +
                $"({Server.Audio.AudioPipeline.TargetSampleRate / 1000.0:0.#} kHz mono 16-bit{(validation.ClippingWarning ? ", clipping warning" : "")}) — playing{tagNote}";
            TestSynthesisResult.Foreground = Brushes.LimeGreen;
        }
        catch (Exception exception)
        {
            TestSynthesisResult.Text = $"✗ {exception.Message}";
            TestSynthesisResult.Foreground = Brushes.OrangeRed;
        }
        finally
        {
            TestSynthesisButton.IsEnabled = true;
        }
    }

    // ---- player voice tab ------------------------------------------------

    private async Task RefreshVoicesAsync()
    {
        var provider = App.Providers.Get(App.Config.Provider);
        if (provider is null)
        {
            return;
        }
        try
        {
            _voices = await provider.ListVoicesAsync(App.Config.SettingsFor(provider), CancellationToken.None);
        }
        catch (Exception)
        {
            _voices = [];
        }
        PlayerVoiceCombo.Items.Clear();
        foreach (var voice in _voices)
        {
            PlayerVoiceCombo.Items.Add(voice.Id);
        }
        PlayerVoiceCombo.Text = App.Config.PlayerVoice;
        RefreshNpcGrid();
    }

    private async void OnRefreshVoices(object sender, RoutedEventArgs e) => await RefreshVoicesAsync();

    private async void OnPreviewPlayerVoice(object sender, RoutedEventArgs e) =>
        await PreviewVoiceAsync(PlayerVoiceCombo.Text, PlayerVoiceResult);

    private async Task PreviewVoiceAsync(string voice, TextBlock output)
    {
        var provider = App.Providers.Get(App.Config.Provider);
        if (provider is null)
        {
            output.Text = "configure a provider first";
            return;
        }
        output.Text = "synthesizing preview…";
        try
        {
            var settings = App.Config.SettingsFor(provider);
            var raw = await Task.Run(() => provider.SynthesizeAsync("War. War never changes.", voice, settings, CancellationToken.None));
            _preview.Play(Server.Audio.AudioPipeline.NormalizeToGameWav(raw));
            output.Text = $"playing '{voice}'";
        }
        catch (Exception exception)
        {
            output.Text = $"✗ {exception.Message}";
        }
    }

    private void OnSavePlayerVoice(object sender, RoutedEventArgs e)
    {
        App.Config.PlayerVoice = PlayerVoiceCombo.Text.Trim();
        App.Config.Save();
        PlayerVoiceResult.Text = $"saved: {App.Config.PlayerVoice}";
    }

    // ---- accent ----------------------------------------------------------

    private void LoadAccentControls()
    {
        PlayerAccentCombo.ItemsSource = Accents.All;
        PlayerAccentCombo.SelectedItem = Accents.Get(App.Config.PlayerAccent);
        AccentImperfectionSlider.Value = Math.Clamp(App.Config.AccentImperfection, 0, 100);

        // Both previews offer the same situations the plugin reports in
        // play, so an audition here matches what the game will send.
        PlayerAccentSceneCombo.ItemsSource = SceneContexts.All;
        PlayerAccentSceneCombo.SelectedItem = SceneContexts.Default;
        TestSceneCombo.ItemsSource = SceneContexts.All;
        TestSceneCombo.SelectedItem = SceneContexts.Default;
    }

    private void OnSavePlayerAccent(object sender, RoutedEventArgs e)
    {
        var accent = PlayerAccentCombo.SelectedItem as Accent ?? Accents.Get(Accents.Default);
        App.Config.PlayerAccent = accent.Id;
        App.Config.AccentImperfection = (int)Math.Round(AccentImperfectionSlider.Value);
        App.Config.Save();
        PlayerAccentResult.Text =
            $"saved: {accent.DisplayName}, {App.Config.AccentImperfection}% imperfection — affected lines regenerate automatically";
    }

    /// <summary>Speaks a sample line exactly as the game would get it: run
    /// through the tagger with the selected accent, so what you hear is what
    /// the accent actually does rather than a description of it.</summary>
    private async void OnPreviewPlayerAccent(object sender, RoutedEventArgs e)
    {
        var provider = App.Providers.Get(App.Config.Provider);
        if (provider is null)
        {
            PlayerAccentResult.Text = "configure a provider first";
            return;
        }
        var accent = PlayerAccentCombo.SelectedItem as Accent ?? Accents.Get(Accents.Default);
        // Fallback when the preview text box is empty: each click cycles
        // to the next sample.  Together they touch every feature the
        // accents trade on: the PRICE/MOUTH/FACE vowel shifts, th and wh
        // consonant changes, h-dropping, broad BATH, LOT rounding, r
        // flavour (tap/trill words), yod words, and the function words
        // the lexicons target.
        // The later lines lean on the situation rather than the vowels:
        // each one is a sentence whose meaning genuinely turns on who is
        // listening and what is happening, so switching the Situation
        // dropdown makes an audible difference.
        string[] samples =
        [
            "I'm not going to ask you again. Put the gun down and walk away.",
            "My brother thinks the whole town saw something out there last night.",
            "Take the road south past the old house and don't stop for anything.",
            "There's nothing better than a hot meal and a good night's sleep.",
            "Sorry, friend — we haven't heard any news about the raiders around here.",
            "What was that? Stay right where you are and keep your voice down.",
            "Thanks for the help. I mean that.",
            "We need to move. Right now.",
            "Don't make me do this.",
            "You really thought I wouldn't find out about the water chip?",
            "Get behind me and don't look. I'll handle it.",
            "Nice work back there. Thought we were finished for sure.",
            "I've got one round left, so make it count.",
            "Put that down before somebody gets hurt.",
        ];
        // Read every control up front: the synthesis call runs on a worker
        // thread, and WPF controls may only be touched on the UI thread.
        var voice = PlayerVoiceCombo.Text;
        var imperfection = (int)Math.Round(AccentImperfectionSlider.Value);
        var customText = PlayerAccentPreviewText.Text.Trim();
        var sample = customText.Length > 0 ? customText : samples[_testTakeCounter % samples.Length];
        var scene = (PlayerAccentSceneCombo.SelectedItem as SceneOption ?? SceneContexts.Default).Value;

        PlayerAccentResult.Text = "synthesizing preview…";
        try
        {
            var settings = App.Config.SettingsFor(provider);
            var line = sample;
            if (provider is InworldProvider inworld)
            {
                var tagged = await inworld.AutoTagDetailedAsync(
                    sample, "PlayerVoiceMale01", true, settings, CancellationToken.None,
                    $"accent-preview/{accent.Id}/{_testTakeCounter++}",
                    accent, imperfection, retake: 0, scene: scene, shoutInCombat: App.Config.ShoutInCombat);
                // Preview-only: keep the sample takes at conversational
                // tempo so the accent is what you hear, not the pacing.
                line = InworldProvider.ScrubInstruction(tagged.Text);
                if (tagged.RouterError is not null)
                {
                    PlayerAccentResult.Text = $"✗ tagging failed: {tagged.RouterError}";
                    return;
                }
            }
            else
            {
                PlayerAccentResult.Text = "accents need the Inworld provider (inworld-tts-2)";
                return;
            }

            var raw = await Task.Run(() => provider.SynthesizeAsync(line, voice, settings, CancellationToken.None));
            _preview.Play(Server.Audio.AudioPipeline.NormalizeToGameWav(raw));
            PlayerAccentResult.Text = $"{accent.DisplayName} → {line}";
        }
        catch (Exception exception)
        {
            PlayerAccentResult.Text = $"✗ {exception.Message}";
        }
    }

    // ---- NPC voices tab --------------------------------------------------

    private void RefreshNpcGrid()
    {
        var mapper = new VoiceMapper(App.Config);
        var voiceTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in App.Config.NpcVoiceOverrides.Keys)
        {
            voiceTypes.Add(known);
        }
        foreach (var entry in App.Synthesis.History)
        {
            var segments = entry.VoicePath.Split('\\');
            if (segments.Length >= 2)
            {
                voiceTypes.Add(segments[^2]);
            }
        }
        // Common vanilla voice types, so the grid is useful before playing.
        foreach (var common in new[]
                 {
                     "MaleBoston", "FemaleBoston", "MaleEvenToned",
                     "FemaleEvenToned", "MaleRough", "FemaleRough", "MaleOldGrizzled", "FemaleOldGrizzled",
                 })
        {
            voiceTypes.Add(common);
        }
        // Player voice types are not NPCs — the Player Voice tab governs them.
        voiceTypes.RemoveWhere(voiceType => voiceType.StartsWith("PlayerVoice", StringComparison.OrdinalIgnoreCase));

        // The accent dropdown offers the same catalogue on every row.
        NpcAccentColumn.ItemsSource = Accents.All;

        NpcGrid.ItemsSource = voiceTypes.Select(voiceType => new NpcRow
        {
            VoiceType = voiceType,
            AutoVoice = _voices.Count > 0 ? mapper.ResolveVoice(false, voiceType, _voices) : "(refresh voices)",
            Override = App.Config.NpcVoiceOverrides.TryGetValue(voiceType, out var value) ? value : "",
            Accent = App.Config.NpcAccentOverrides.TryGetValue(voiceType, out var accent) ? accent : Accents.Default,
        }).ToList();
    }

    private void OnRefreshNpcGrid(object sender, RoutedEventArgs e) => RefreshNpcGrid();

    private void OnSaveNpcOverrides(object sender, RoutedEventArgs e)
    {
        if (NpcGrid.ItemsSource is not IEnumerable<NpcRow> rows)
        {
            return;
        }
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Override))
            {
                App.Config.NpcVoiceOverrides.Remove(row.VoiceType);
            }
            else
            {
                App.Config.NpcVoiceOverrides[row.VoiceType] = row.Override.Trim();
            }

            // Neutral is the default, so it is stored by absence.
            if (string.IsNullOrWhiteSpace(row.Accent) || row.Accent == Accents.Default)
            {
                App.Config.NpcAccentOverrides.Remove(row.VoiceType);
            }
            else
            {
                App.Config.NpcAccentOverrides[row.VoiceType] = row.Accent;
            }
        }
        App.Config.Save();
        RefreshNpcGrid();
    }

    private async void OnPreviewNpcVoice(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not NpcRow row)
        {
            return;
        }
        var voice = string.IsNullOrWhiteSpace(row.Override) ? row.AutoVoice : row.Override;
        await PreviewVoiceAsync(voice, PlayerVoiceResult);
    }

    // ---- diagnostics tab -------------------------------------------------

    private void RefreshHistory()
    {
        HistoryGrid.ItemsSource = App.Synthesis.History.Select(entry => new HistoryRow
        {
            Time = entry.Timestamp.ToString("HH:mm:ss"),
            Text = Truncate(entry.EnrichedText is null ? entry.Text : $"{entry.Text}  ⇒  {entry.EnrichedText}", 110),
            Voice = entry.Voice,
            Milliseconds = entry.Elapsed.TotalMilliseconds.ToString("F0"),
            ResultText = entry.Success
                ? entry.ClippingWarning ? "ok (clipping warning)" : "ok"
                : $"failed: {Truncate(entry.Failure ?? "", 80)}",
            WavPath = entry.WavPath,
        }).ToList();
    }

    private void OnReplayHistory(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is HistoryRow { WavPath: { } path } && File.Exists(path))
        {
            _preview.PlayFile(path);
        }
    }

    // ---- lines tab -------------------------------------------------------

    private void RefreshLines()
    {
        var filter = LineFilterBox?.Text?.Trim() ?? "";
        var records = App.Synthesis.Lines.Records;

        var rows = records
            .Where(record =>
                filter.Length == 0 ||
                record.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                record.VoicePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                record.Voice.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(record => new LineRow
            {
                VoicePath = record.VoicePath,
                Text = record.Text,
                TaggedText = record.TaggedText,
                Voice = record.Voice,
                Variant = record.Variant,
                Scene = record.Scene,
                CustomPrompt = record.CustomPrompt,
                HealthText = DescribeHealth(record.Health),
                WavPath = string.IsNullOrEmpty(record.CacheKey) ? null : App.Synthesis.Cache.PathFor(record.CacheKey),
            })
            .ToList();

        // Preserve the selected line across a refresh (regenerating one
        // rebuilds the grid, and losing the row under the cursor mid-audition
        // would be maddening).
        var selected = (LinesGrid.SelectedItem as LineRow)?.VoicePath;
        LinesGrid.ItemsSource = rows;
        if (selected is not null)
        {
            LinesGrid.SelectedItem = rows.FirstOrDefault(r => r.VoicePath == selected);
        }

        var missing = records.Count(r => r.Health is LineHealth.MissingInGame or LineHealth.MissingInCache or LineHealth.Missing);
        LineSummary.Text = missing == 0
            ? $"{records.Count} line(s)"
            : $"{records.Count} line(s), {missing} with missing audio";
    }

    private static string DescribeHealth(LineHealth health) => health switch
    {
        LineHealth.Ok => "ok",
        LineHealth.MissingInGame => "gone from game",
        LineHealth.MissingInCache => "gone from cache",
        LineHealth.Missing => "audio deleted",
        _ => "not checked",
    };

    private void OnLineFilterChanged(object sender, TextChangedEventArgs e) => RefreshLines();

    private void OnRefreshLines(object sender, RoutedEventArgs e)
    {
        App.Synthesis.ValidateLines();
        RefreshLines();
    }

    private void OnOpenLineLog(object sender, RoutedEventArgs e)
    {
        var path = App.Synthesis.Lines.Path;
        if (!File.Exists(path))
        {
            LineSummary.Text = "no lines have been generated yet";
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnLineSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LinesGrid.SelectedItem is not LineRow row)
        {
            LineTaggedText.Text = "";
            LineFileText.Text = "";
            return;
        }
        LineTaggedText.Text = row.TaggedText;
        LineFileText.Text = row.Scene.Length > 0
            ? $"{row.VoicePath}     (scene: {row.Scene})"
            : row.VoicePath;
    }

    private void OnOpenTakes(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not LineRow row)
        {
            return;
        }
        var takes = App.Synthesis.TakesFor(row.VoicePath);
        if (takes.Count == 0)
        {
            LineSummary.Text = "no takes on record for this line yet";
            return;
        }
        var dialog = new TakesWindow(
            row.VoicePath,
            row.Text,
            takes,
            play: path => _preview.PlayFile(path),
            selectTake: cacheKey => App.Synthesis.SelectTake(row.VoicePath, cacheKey),
            deleteAll: () => App.Synthesis.DeleteAllTakes(row.VoicePath),
            deleteOthers: () => App.Synthesis.DeleteOtherTakes(row.VoicePath))
        {
            Owner = this,
        };
        dialog.ShowDialog();
        RefreshLines();
    }

    private void OnPlayLine(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is LineRow { WavPath: { } path } && File.Exists(path))
        {
            _preview.PlayFile(path);
            return;
        }
        LineSummary.Text = "that take's audio is no longer on disk — regenerate it";
    }

    private async void OnRegenerateLine(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is LineRow row)
        {
            await RegenerateAndPlayAsync((Button)sender, row, direction: null);
        }
    }

    private async void OnDirectLine(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not LineRow row)
        {
            return;
        }
        var dialog = new PromptWindow(row.Text, row.CustomPrompt) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await RegenerateAndPlayAsync((Button)sender, row, dialog.Direction);
    }

    /// <summary>Generates a fresh take and plays it — the point of asking for
    /// one is hearing it, so it is never left sitting silently in the grid.</summary>
    private async Task RegenerateAndPlayAsync(Button button, LineRow row, string? direction)
    {
        button.IsEnabled = false;
        LineSummary.Text = direction is { Length: > 0 }
            ? "generating a directed take…"
            : "generating a new take…";
        try
        {
            var status = await App.Synthesis.RegenerateAsync(row.VoicePath, direction);
            RefreshLines();
            if (status is { State: JobState.Done, WavPath: { } path } && File.Exists(path))
            {
                LineSummary.Text = "new take ready — the game picks it up on its next encounter with this line";
                _preview.PlayFile(path);
            }
            else
            {
                LineSummary.Text = status?.Failure is { Length: > 0 } failure
                    ? $"regeneration failed: {Truncate(failure, 90)}"
                    : "regeneration did not finish";
            }
        }
        catch (Exception exception)
        {
            LineSummary.Text = $"regeneration failed: {Truncate(exception.Message, 90)}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnSimulateGameRequest(object sender, RoutedEventArgs e)
    {
        // Issues the identical HTTP sequence the F4SE plugin performs, so a
        // green result here proves the entire pipeline without the game.
        if (!App.Server.Running)
        {
            SimulateResult.Text = "start the server first";
            return;
        }
        SimulateResult.Text = "running…";
        try
        {
            using var client = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{App.Config.Port}") };
            var status = await client.GetAsync("/api/status");
            var voicePath = $@"Sound\Voice\Simulated.esp\PlayerVoiceFemale01\Sim_{DateTime.Now:HHmmss}_1.wav";
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                text = "This is a simulated dialogue line from the diagnostics page.",
                voicePath,
                voiceType = "PlayerVoiceFemale01",
                isPlayer = true,
            });
            var synth = await client.PostAsync("/api/synth", new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            var steps = $"status {(int)status.StatusCode}; synth {(int)synth.StatusCode}";
            byte[]? wav = null;
            if (synth.IsSuccessStatusCode && synth.Content.Headers.ContentType?.MediaType == "audio/wav")
            {
                wav = await synth.Content.ReadAsByteArrayAsync();
            }
            else
            {
                for (var attempt = 0; attempt < 120 && wav is null; attempt++)
                {
                    await Task.Delay(500);
                    var result = await client.GetAsync("/api/result?voicePath=" + Uri.EscapeDataString(voicePath));
                    if (result.IsSuccessStatusCode && result.Content.Headers.ContentType?.MediaType == "audio/wav")
                    {
                        wav = await result.Content.ReadAsByteArrayAsync();
                    }
                    else if ((int)result.StatusCode is 404 or 422)
                    {
                        var error = await result.Content.ReadAsStringAsync();
                        SimulateResult.Text = $"✗ {steps}; result {(int)result.StatusCode}: {Truncate(error, 200)}";
                        return;
                    }
                }
            }
            if (wav is null)
            {
                SimulateResult.Text = $"✗ {steps}; timed out waiting for audio";
                return;
            }
            _preview.Play(wav);
            SimulateResult.Text = $"✓ {steps}; received {wav.Length / 1024} KB of valid audio — playing";
        }
        catch (Exception exception)
        {
            SimulateResult.Text = $"✗ {exception.Message}";
        }
    }

    // ---- updates tab -----------------------------------------------------

    private string? _latestReleaseUrl;

    private async Task CheckUpdatesAsync(bool silent)
    {
        try
        {
            var latest = await UpdateChecker.CheckAsync();
            if (latest is null)
            {
                if (!silent)
                {
                    UpdateResult.Text = "you are on the latest version";
                }
                return;
            }
            _latestReleaseUrl = latest.Url;
            UpdateResult.Text = $"version {latest.Version} is available";
            OpenReleaseButton.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            if (!silent)
            {
                UpdateResult.Text = $"update check failed: {exception.Message}";
            }
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(silent: false);

    private void OnOpenRelease(object sender, RoutedEventArgs e)
    {
        if (_latestReleaseUrl is not null)
        {
            Process.Start(new ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
        }
    }

    private void OnUpdatePrefChanged(object sender, RoutedEventArgs e)
    {
        App.Config.CheckForUpdates = UpdateCheckBox.IsChecked == true;
        App.Config.Save();
    }
}
