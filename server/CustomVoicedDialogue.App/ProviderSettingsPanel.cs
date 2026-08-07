using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Providers;

namespace CustomVoicedDialogue.App;

/// <summary>
/// Renders a provider's option schema into editable controls and collects
/// the values back.  Every provider gets its settings UI from this one
/// class — adding a provider never means new UI code.
/// </summary>
public sealed class ProviderSettingsPanel
{
    private readonly Panel _host;
    private readonly Panel? _defaultVoiceHost;
    private readonly Dictionary<string, Func<string>> _readers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When <paramref name="defaultVoiceHost"/> is given, the
    /// provider's own voice option is rendered there (relabeled as the
    /// default/test voice) instead of among the regular settings — in the
    /// main window it lives next to the test-synthesis box it actually
    /// drives, since in-game voices come from the Player/NPC Voices tabs.</summary>
    public ProviderSettingsPanel(Panel host, Panel? defaultVoiceHost = null)
    {
        _host = host;
        _defaultVoiceHost = defaultVoiceHost;
    }

    private static bool IsDefaultVoiceOption(string key) =>
        key.ToLowerInvariant() is "voice" or "voiceid" or "voice_id" or "voice_name";

    public void Show(ITtsProvider provider, AppConfig config)
    {
        _host.Children.Clear();
        _defaultVoiceHost?.Children.Clear();
        _readers.Clear();

        if (provider.HelpUrl is not null)
        {
            var link = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
            var hyperlink = new Hyperlink(new Run($"{provider.DisplayName} setup / API keys"))
            {
                NavigateUri = new Uri(provider.HelpUrl),
            };
            hyperlink.RequestNavigate += (_, args) =>
                Process.Start(new ProcessStartInfo(args.Uri.ToString()) { UseShellExecute = true });
            link.Inlines.Add(hyperlink);
            _host.Children.Add(link);
        }

        if (!string.IsNullOrEmpty(provider.UsageNotes))
        {
            _host.Children.Add(new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x50, 0x78, 0xA0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = provider.UsageNotes,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.9,
                    FontSize = 12,
                },
            });
        }

        var current = config.SettingsFor(provider);
        foreach (var option in provider.Options)
        {
            var isDefaultVoice = _defaultVoiceHost is not null && IsDefaultVoiceOption(option.Key);
            var target = isDefaultVoice ? _defaultVoiceHost! : _host;
            var labelText = isDefaultVoice ? "Default / test voice" : option.Label;
            var description = isDefaultVoice
                ? $"{option.Description} Used by this test box; in-game it is only a fallback — " +
                  "player lines use the Player Voice tab, NPC lines the NPC Voices tab."
                : option.Description;

            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var label = new TextBlock
            {
                Text = labelText,
                Width = 190,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = description,
            };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            var value = current.Get(option.Key, option.DefaultValue);
            switch (option.Kind)
            {
                case OptionKind.Secret:
                {
                    var box = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Password = value };
                    _readers[option.Key] = () => box.Password;
                    row.Children.Add(box);
                    break;
                }
                case OptionKind.Toggle:
                {
                    var box = new CheckBox
                    {
                        IsChecked = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1",
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    _readers[option.Key] = () => box.IsChecked == true ? "true" : "false";
                    row.Children.Add(box);
                    break;
                }
                case OptionKind.Choice:
                {
                    var combo = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, IsEditable = true, Text = value };
                    foreach (var choice in option.Choices ?? [])
                    {
                        combo.Items.Add(choice);
                    }
                    _readers[option.Key] = () => combo.Text;
                    row.Children.Add(combo);
                    break;
                }
                default:
                {
                    var box = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Text = value, ToolTip = description };
                    _readers[option.Key] = () => box.Text;
                    row.Children.Add(box);
                    break;
                }
            }
            target.Children.Add(row);

            if (!string.IsNullOrEmpty(description))
            {
                target.Children.Add(new TextBlock
                {
                    Text = description,
                    Margin = new Thickness(190, 0, 0, 4),
                    Opacity = 0.6,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
    }

    public Dictionary<string, string> Collect() =>
        _readers.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.OrdinalIgnoreCase);
}
