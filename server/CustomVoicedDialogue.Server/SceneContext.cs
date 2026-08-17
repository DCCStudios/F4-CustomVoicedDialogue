namespace CustomVoicedDialogue.Server;

/// <summary>One situation a line can be spoken in, as the app offers it for
/// auditioning.  <see cref="Value"/> is the literal string sent to the
/// tagger.</summary>
public sealed record SceneOption(string DisplayName, string Value)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The scenes the plugin reports with a line.
///
/// These strings must stay identical to the ones GameContext::Describe
/// builds on the plugin side — auditioning a delivery here is only
/// meaningful if it is the same text the game will send.  Both sides
/// compose the same three clauses, in this order, joined with "; ".
/// </summary>
public static class SceneContexts
{
    public const string InCombat = "in combat";
    public const string Sneaking = "sneaking, staying quiet";
    public const string Hostile = "the listener is hostile to them";

    /// <summary>Builds a scene the same way the plugin does: only the parts
    /// that are true, in a fixed order, joined with "; ".</summary>
    public static string Compose(bool inCombat, bool sneaking, bool listenerHostile)
    {
        var parts = new List<string>(3);
        if (inCombat)
        {
            parts.Add(InCombat);
        }
        if (sneaking)
        {
            parts.Add(Sneaking);
        }
        if (listenerHostile)
        {
            parts.Add(Hostile);
        }
        return string.Join("; ", parts);
    }

    /// <summary>Every situation the plugin can report, for the preview
    /// dropdowns.  Ordered so the single signals come first and the
    /// combinations after.</summary>
    public static IReadOnlyList<SceneOption> All { get; } =
    [
        new("Ordinary conversation", Compose(false, false, false)),
        new("In combat", Compose(true, false, false)),
        new("Sneaking", Compose(false, true, false)),
        new("Listener is hostile", Compose(false, false, true)),
        new("Sneaking, listener hostile", Compose(false, true, true)),
        new("In combat, listener hostile", Compose(true, false, true)),
        new("In combat, sneaking", Compose(true, true, false)),
        new("In combat, sneaking, listener hostile", Compose(true, true, true)),
    ];

    public static SceneOption Default => All[0];
}
