namespace CustomVoicedDialogue.Server.VoiceMapping;

/// <summary>One selectable accent.  Pronunciation comes from the curated
/// IPA lexicon (<see cref="AccentLexicon"/>), applied in code after
/// tagging; <see cref="Guidance"/> is a short character note the tagging
/// model may weave into its steering instruction so the rhythm and melody
/// match the vowels.</summary>
public sealed record Accent(string Id, string DisplayName, string Guidance)
{
    /// <summary>True for the neutral default, which changes nothing.</summary>
    public bool IsNeutral => Id == Accents.Default;
}

/// <summary>The accent catalogue.</summary>
public static class Accents
{
    public const string Default = "american";

    public static IReadOnlyList<Accent> All { get; } =
    [
        new(Default, "American (neutral)", ""),

        new("southern", "American — Southern",
            "a warm, easy Southern drawl: friendly and easy-going, with the " +
            "stressed vowels gently held."),

        new("deep-south", "American — Deep South",
            "a thick Deep South drawl: heavy, unhurried, vowels leaned on " +
            "hard and words flowing into each other."),

        new("boston", "American — Boston / New England",
            "a blunt, quick, working-class New England edge."),

        new("new-york", "American — New York",
            "fast, clipped, streetwise New York attitude, hitting the " +
            "stressed syllables hard."),

        new("mid-atlantic", "American — Mid-Atlantic (1940s radio)",
            "the clipped, theatrical diction of 1940s radio: crisp, formal, " +
            "brisk, every ending pronounced completely."),

        new("british-rp", "British — Received Pronunciation (posh)",
            "precise, composed, understated British poshness — crisp " +
            "consonants, nothing folksy."),

        new("british-cockney", "British — Cockney",
            "cheeky, quick East-End London patter."),

        new("british-north", "British — Northern England",
            "a blunt, even, unshowy Northern English delivery."),

        new("scottish", "Scottish",
            "brisk and musical, with the pitch moving a lot and the r's " +
            "tapped."),

        new("glaswegian", "Scottish — Glaswegian",
            "fast, punchy Glasgow patter: clipped vowels, hard glottal " +
            "catches on the t's, and the melody swinging wide inside " +
            "every phrase."),

        new("welsh", "Welsh",
            "a lilting Welsh singsong — the melody rises and falls across " +
            "every phrase and lifts at the end."),

        new("irish", "Irish",
            "a bright, musical Irish lilt, quick and warm."),

        new("spanish-mexican", "Spanish — Mexican",
            "English spoken with a Mexican Spanish accent: even, " +
            "syllable-timed rhythm, every syllable given its full weight."),

        new("australian", "Australian",
            "a laid-back, dry Australian delivery, statements lifting at " +
            "the end as if half a question."),

        new("russian", "Russian",
            "English spoken with a Russian accent: heavy, even stress, " +
            "little pitch movement, consonants landing hard."),

        new("french", "French",
            "English spoken with a French accent: smooth and legato, the " +
            "stress drifting to the ends of phrases."),

        new("german", "German",
            "English spoken with a German accent: clipped, precise and " +
            "deliberate, with strong even stress."),

        new("italian", "Italian",
            "English spoken with an Italian accent: expressive and musical, " +
            "with big rises and falls."),
    ];

    public static Accent Get(string? id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    /// <summary>Whether a line lands on an accent "slip", derived from the
    /// line's own hash so a given line always performs identically (the audio
    /// cache depends on that) while slips scatter naturally across a
    /// conversation.  <paramref name="imperfection"/> is 0–100 and maps to at
    /// most a 60 % slip rate, so even at maximum the accent wobbles rather
    /// than disappears.</summary>
    public static bool LineSlips(string lineKey, int imperfection)
    {
        var level = Math.Clamp(imperfection, 0, 100);
        if (level == 0)
        {
            return false;
        }
        // A different seed from the take-number hash, so slips do not
        // correlate with the emotion take.
        var roll = VoiceMapper.Fnv1a("slip " + lineKey) % 100;
        return roll < level * 60 / 100;
    }
}
