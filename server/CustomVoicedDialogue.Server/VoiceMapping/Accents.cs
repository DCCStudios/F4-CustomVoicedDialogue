namespace CustomVoicedDialogue.Server.VoiceMapping;

/// <summary>One selectable accent.  <see cref="Guidance"/> is written as
/// direction to a voice actor and is handed to the tagging model, which
/// respells the line phonetically so the synthesizer actually produces the
/// sound — naming an accent alone tends to give a caricature, or nothing at
/// all.</summary>
public sealed record Accent(string Id, string DisplayName, string Guidance)
{
    /// <summary>True for the neutral default, which adds no direction and
    /// leaves the line exactly as written.</summary>
    public bool IsNeutral => Id == Accents.Default;
}

/// <summary>
/// The accent catalogue.  Each entry describes how the accent sits in the
/// mouth (rhoticity, vowels, rhythm) plus a couple of concrete respellings,
/// because concrete examples steer a small model far better than adjectives.
/// </summary>
public static class Accents
{
    public const string Default = "american";

    /// <summary>Rules about how to WRITE a respelling, as opposed to which
    /// sounds to aim for.  The synthesizer derives pronunciation from the
    /// letters, so a spelling English never uses ("aawy" for away) comes out
    /// mangled, a spelled r in a non-rhotic word still gets pronounced, and
    /// a final n split off with a hyphen lands far harder than any speaker
    /// would say it.  The reliable technique is to make each respelled
    /// syllable look like a real English word.</summary>
    public const string RespellingCraft =
        "HOW TO WRITE THE RESPELLING — this decides whether the synthesizer can read it at all. " +
        "The one reliable technique: respell a syllable so it looks like a real English word, or like " +
        "a spelling pattern English already uses. That is why day → die, face → fice, name → nime, " +
        "time → toime, house → hahs and down → dahn all work — every one is readable English. A " +
        "spelling English never uses is read letter by letter and comes out mangled, so never invent " +
        "clusters, never write doubled vowels like aa, ii, uu or yy, and never leave a bare y standing " +
        "for a vowel (away → aawy is broken and unreadable). Build only from patterns that already " +
        "occur in English words — ah, aw, ay, ee, eh, er, ih, oh, oo, ou, ow, oy, uh — and prefer the " +
        "spellings dialect writing has used for centuries: Ah'm (never Ai'm), ya, yer, ye, wot, woz, " +
        "wuz, o', an', th', da, gonna, outta, 'ave, 'ouse, dahn, aboot, awa'. " +
        "If one word needs several sounds changed, split it at the SYLLABLE boundary with a hyphen so " +
        "that each piece is itself readable English (man → may-un, settlement → settle-ment); never " +
        "hyphenate a lone consonant off the end, which is what makes an n land like a hammer " +
        "(daa-n and wor-n are wrong; daan and wawn are right). A final n or m stays one plain letter " +
        "attached to its vowel — never doubled, never split off. Where the accent carries the nasal in " +
        "the vowel instead of punching the consonant (down, town, worn, gone), broaden the vowel and " +
        "leave a single soft n after it. " +
        "In a non-rhotic accent DELETE the letter r wherever it is not followed by a vowel (worn → " +
        "wawn, hard → hahd, better → bettuh); leaving it in makes the synthesizer pronounce it and the " +
        "accent collapses. In a rhotic accent every r stays exactly where it is. " +
        "Respelling changes spelling only, never the words: every word of the original must still be " +
        "there, including small ones like was, is, the, and, to, and every grammatical ending must " +
        "survive (goes → goez, never go; burned → buhnd, never burn). Two words may merge only where " +
        "the accent genuinely does that (going to → gonna); dropping a word is never allowed. " +
        "Above all: if a word has no natural readable respelling in this accent, LEAVE IT SPELLED " +
        "NORMALLY. An ordinary word read correctly always beats an invented one the synthesizer " +
        "stumbles over — the accent only needs to show on the words that carry it.";

    public static IReadOnlyList<Accent> All { get; } =
    [
        new(Default, "American (neutral)", ""),

        new("southern", "American — Southern",
            "a warm US Southern drawl. The long i flattens to a single held ah (time → tahm, my → " +
            "mah, I → Ah). Short vowels break into two (bed → bay-ed, yes → yay-es). E before a nasal " +
            "rises to i (pen → pin, ten → tin). -ing always drops to -in'. Everything is nasal: before " +
            "n the vowel carries the nasality and the n itself stays soft and short (down → daown, " +
            "gone → gawn), never punched. Unhurried, with the stressed vowel held."),

        new("deep-south", "American — Deep South",
            "a heavy Deep South drawl — everything in the Southern accent, slower and thicker. The " +
            "long i is fully flat (right → raht, nine → nahn), oi flattens too (oil → awl), and short " +
            "vowels break further (man → may-un). -ing is always -in', 'you all' runs together as " +
            "y'all, final consonants soften almost away, and the nasal in down or town lives in the " +
            "vowel (daown, taown) with barely any n at the end. Words lean into each other."),

        new("boston", "American — Boston / New England",
            "a Boston accent, strongly non-rhotic: delete every r that is not followed by a vowel and " +
            "broaden the vowel in its place (car → cah, park → pahk, hard → hahd, worn → wawn, corner " +
            "→ cawnah). START takes a broad ah (bath → bahth is wrong here — that a stays flat). An r " +
            "appears between a word ending in a vowel and the next word (idea → idear). THOUGHT is " +
            "rounded (talk → tawk). Final n stays light after the broadened vowel."),

        new("new-york", "American — New York",
            "a New York City accent, non-rhotic: delete r after a vowel (water → wawtah, thirty → " +
            "thuhty, worn → wawn). THOUGHT is high and rounded with an off-glide (coffee → cawfee, " +
            "talk → tawk, dog → dawg). A before a nasal rises and breaks (man → meh-an, can't → " +
            "keh-ant). Th goes dental toward d and t in casual speech (this → dis, thing → ting). " +
            "Fast and clipped, with the stressed syllable hit hard."),

        new("mid-atlantic", "American — Mid-Atlantic (1940s radio)",
            "the clipped Transatlantic diction of 1940s newsreels and radio announcers: non-rhotic, " +
            "crisp fully-pronounced t's, precise theatrical enunciation, slightly British-leaning " +
            "vowels, no vowel reduction, brisk and formal. Endings stay crisp and complete — never " +
            "drop the g on -ing and never slur words together."),

        new("british-rp", "British — Received Pronunciation (posh)",
            "crisp British Received Pronunciation, non-rhotic: delete r after a vowel (car → cah, " +
            "better → bettuh, worn → wawn, hard → hahd). BATH takes the long broad a (bath → bahth, " +
            "can't → cahn't, past → pahst). LOT is short and rounded (not → not, never naht). Every t " +
            "is fully released, never glottal. It is precise, never folksy: keep every -ing ending " +
            "complete, never write Ah for I, and let final n stay neat and light rather than heavy."),

        new("british-cockney", "British — Cockney",
            "a London Cockney accent, non-rhotic: delete r after a vowel (worn → wawn, better → " +
            "bettah). MOUTH is the signature — ou becomes a long flat ah (house → hahs, down → dahn, " +
            "out → aht, about → abaht), with the n after it soft and swallowed, never punched. PRICE " +
            "widens (like → loike, time → toime) and FACE opens (face → fice, day → die, away → " +
            "awye). Th fronts " +
            "to f or v (think → fink, brother → bruvver). H drops (house → 'ouse, have → 'ave). T " +
            "between vowels becomes a glottal catch (bottle → bo'le, water → wo'er). L at the end " +
            "goes to w (milk → miwk, ball → baw)."),

        new("british-north", "British — Northern England",
            "a Northern English accent (Yorkshire/Manchester), non-rhotic. The STRUT vowel sits like " +
            "book, not like cup (love → luv, up → oop, blood → blud). There is no long broad a: bath, " +
            "past and laugh keep the short flat a (bahth is southern and wrong here). FACE and GOAT " +
            "stay pure single sounds (face → fehs, go → goh, no glide). The definite article shortens " +
            "to t' before a noun (in the pub → in t'pub). Blunt, even, and unshowy."),

        new("scottish", "Scottish",
            "a Scottish accent, strongly rhotic — KEEP every r and tap it (worn stays worn, hard " +
            "stays hard, never wawn or hahd). MOUTH is a pure oo (house → hoose, about → aboot, down " +
            "→ doon, now → noo) with a clean light n after it. Foot and goose are the same vowel " +
            "(good → guid). Not contracts to no' and -ing drops to -in'. T between vowels is a " +
            "glottal catch (water → wa'er). Brisk, with the pitch moving a lot."),

        new("welsh", "Welsh",
            "a Welsh accent: the melody is the accent — pitch rises and falls across every phrase and " +
            "lifts at the end, in a clear singsong. Vowels are pure and unglided (go → goh, face → " +
            "fehs). Consonants between vowels are held long (butter → butter with a leaned-on t). R " +
            "is lightly tapped and -ing endings are sounded fully, never dropped. Vowels stay bright " +
            "and forward, and the rhythm is even rather than clipped."),

        new("irish", "Irish",
            "an Irish accent, rhotic — keep the r and make it soft (worn stays worn). Th becomes t or " +
            "d (thing → ting, the → de, that → dat, three → tree). PRICE starts further back (time → " +
            "toime, my → moi). MOUTH fronts (down → deown, town → teown) with a light n after it. " +
            "-ing drops to -in'. Vowels are bright and forward and the melody rises and falls " +
            "musically inside each phrase."),

        new("spanish-mexican", "Spanish — Mexican",
            "English with a Mexican Spanish accent. Vowels collapse to the five pure Spanish ones " +
            "with no glide and no reduction — every syllable keeps its full vowel (about → abaut, " +
            "not uhbout). Th hardens to d or t (this → dis, think → tink, the → de). V becomes b " +
            "(very → bery). An e slips in before an initial s-cluster (school → eschool, Spanish → " +
            "Espanish). R is tapped. -ing softens to -een (going → goeen). Final consonants weaken, " +
            "so the n in down or worn is light. The rhythm is even and syllable-timed, every " +
            "syllable given the same length."),

        new("australian", "Australian",
            "an Australian accent, non-rhotic: delete r after a vowel (worn → wawn, better → bettah, " +
            "hard → hahd). FACE lowers toward price (day → die, mate → moite, name → noime) and " +
            "PRICE lowers further (time → toime, night → noight). MOUTH fronts (down → daown, town → " +
            "taown) with a light n. Unstressed endings relax to uh (better → bettah). Statements " +
            "lift at the end as if part question."),

        new("russian", "Russian",
            "English with a Russian accent. No vowel reduction — every unstressed vowel keeps its " +
            "full value. Th becomes z or s (this → zis, think → sink, the → ze). W turns to v (very " +
            "→ very said as vary, was → vas, we → ve). Final consonants devoice (bad → bat, is → iss, " +
            "have → haf). R is tapped hard and l is dark and heavy. Consonants are strong and the " +
            "stress lands heavily, with little pitch movement."),

        new("french", "French",
            "English with a French accent. H at the start of a word goes silent (hello → 'ello, have " +
            "→ 'ave, house → 'ouse). Th becomes z or s (the → ze, think → sink, that → zat). R is " +
            "soft and made in the throat. Vowels are pure with no glide (day → deh, no → noh). P, t " +
            "and k lose their puff of air. The stress falls on the LAST syllable of each phrase, and " +
            "a nasal vowel absorbs the n after it (down → dawn said through the nose, worn → wawn)."),

        new("german", "German",
            "English with a German accent. Th becomes z or s (the → ze, think → sink, this → zis). W " +
            "turns to v (we → ve, want → vant) and v turns to f (very → fery, have → haf). Final " +
            "consonants devoice (had → hat, bed → bet, is → iss). Vowels are short and exact with no " +
            "glide, and every consonant is fully and precisely articulated — clipped and deliberate, " +
            "with strong even stress."),

        new("italian", "Italian",
            "English with an Italian accent. Vowels are pure, open and never reduced, and a light " +
            "extra vowel tends to follow a word ending in a consonant (what's → what's-a, come → " +
            "come-a). Th becomes t or d (this → dis, thing → ting, three → tree). R is rolled. H is " +
            "often dropped ('ello). Double consonants are held long. The melody is expressive with " +
            "big rise and fall, and the rhythm is syllable-timed."),
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
        var roll = VoiceMapper.Fnv1a("slip " + lineKey) % 100;
        return roll < level * 60 / 100;
    }
}
