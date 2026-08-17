using System.Windows;

namespace CustomVoicedDialogue.App;

/// <summary>Asks for direction on one specific line, so a take can be aimed
/// at what the user wants instead of re-rolled blindly.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string line, string existingDirection)
    {
        InitializeComponent();
        LineText.Text = line;
        DirectionBox.Text = existingDirection;
        // Whatever was asked for last time is the natural starting point for
        // the next attempt, so it comes back selected and ready to replace.
        Loaded += (_, _) =>
        {
            DirectionBox.Focus();
            DirectionBox.SelectAll();
        };
    }

    public string Direction => DirectionBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Direction.Length == 0)
        {
            return;  // nothing to act on; leave the dialog open
        }
        DialogResult = true;
    }
}
