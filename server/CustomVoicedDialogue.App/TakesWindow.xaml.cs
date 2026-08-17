using System.IO;
using System.Windows;
using System.Windows.Controls;
using CustomVoicedDialogue.Server;

namespace CustomVoicedDialogue.App;

/// <summary>Lists every take of one line so the user can audition an earlier
/// take and restore it, or delete them all.  Playback and the actual
/// selection/deletion are done through callbacks the Lines tab supplies, so
/// this window stays a thin view.</summary>
public partial class TakesWindow : Window
{
    private readonly string _voicePath;
    private readonly Action<string> _play;
    private readonly Func<string, bool> _selectTake;   // cacheKey -> succeeded
    private readonly Action _deleteAll;
    private readonly Action _deleteOthers;

    public sealed class TakeRow
    {
        public int Variant { get; init; }
        public string TaggedText { get; init; } = "";
        public string CacheKey { get; init; } = "";
        public string WavPath { get; init; } = "";
        public bool Available { get; init; }
        public bool IsActive { get; init; }
        public string ActiveMark => IsActive ? "●" : "";
        public string AvailabilityText => Available ? "on disk" : "gone";
    }

    public TakesWindow(
        string voicePath,
        string lineText,
        IReadOnlyList<SynthesisService.TakeInfo> takes,
        Action<string> play,
        Func<string, bool> selectTake,
        Action deleteAll,
        Action deleteOthers)
    {
        InitializeComponent();
        _voicePath = voicePath;
        _play = play;
        _selectTake = selectTake;
        _deleteAll = deleteAll;
        _deleteOthers = deleteOthers;
        LineText.Text = lineText;
        Load(takes);
    }

    private void Load(IReadOnlyList<SynthesisService.TakeInfo> takes)
    {
        TakesGrid.ItemsSource = takes
            .Select(t => new TakeRow
            {
                Variant = t.Variant,
                TaggedText = t.TaggedText,
                CacheKey = t.CacheKey,
                WavPath = t.WavPath,
                Available = t.Available,
                IsActive = t.IsActive,
            })
            .ToList();
        StatusText.Text = $"{takes.Count} take(s)";
    }

    private void OnPlayTake(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TakeRow row && row.Available && File.Exists(row.WavPath))
        {
            _play(row.WavPath);
            return;
        }
        StatusText.Text = "that take's audio is no longer on disk";
    }

    private void OnSelectTake(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TakeRow row)
        {
            return;
        }
        if (!row.Available)
        {
            StatusText.Text = "can't set active — that take's audio is gone";
            return;
        }
        if (_selectTake(row.CacheKey))
        {
            StatusText.Text = $"take {row.Variant} is now active — the game picks it up on its next encounter";
            // Reflect the new active mark without reopening.
            if (TakesGrid.ItemsSource is IEnumerable<TakeRow> rows)
            {
                TakesGrid.ItemsSource = rows
                    .Select(r => new TakeRow
                    {
                        Variant = r.Variant,
                        TaggedText = r.TaggedText,
                        CacheKey = r.CacheKey,
                        WavPath = r.WavPath,
                        Available = r.Available,
                        IsActive = string.Equals(r.CacheKey, row.CacheKey, StringComparison.OrdinalIgnoreCase),
                    })
                    .ToList();
            }
            if (row.Available && File.Exists(row.WavPath))
            {
                _play(row.WavPath);
            }
        }
        else
        {
            StatusText.Text = "could not set that take active";
        }
    }

    private void OnDeleteOthers(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Delete every take except the active one? The line keeps its current take; the rest are removed. This cannot be undone.",
            "Delete other takes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }
        _deleteOthers();
        DialogResult = true;   // signal the Lines tab to refresh
        Close();
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Delete every take of this line and its audio? The game will regenerate the line fresh. This cannot be undone.",
            "Delete all takes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }
        _deleteAll();
        DialogResult = true;   // signal the Lines tab to refresh
        Close();
    }
}
