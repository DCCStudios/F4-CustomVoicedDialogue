using System.Text.Json;

namespace CustomVoicedDialogue.Server.Providers;

/// <summary>Cartesia: POST /tts/bytes with X-API-Key, wav container out.</summary>
public sealed class CartesiaProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public CartesiaProvider(HttpClient http) => _http = http;

    public string Id => "cartesia";
    public string DisplayName => "Cartesia";
    public bool IsCloud => true;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => "https://play.cartesia.ai/keys";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("API_KEY", "API key", OptionKind.Secret, ""),
        new("voiceid", "Voice ID", OptionKind.Text, "", "A Cartesia voice id from your library."),
        new("model_id", "Model", OptionKind.Choice, "sonic-3", Choices: ["sonic-3", "sonic-2", "sonic-english", "sonic-multilingual"]),
        new("language", "Language", OptionKind.Text, "en"),
        new("speed", "Speed", OptionKind.Choice, "normal", Choices: ["slowest", "slow", "normal", "fast", "fastest"]),
        new("sample_rate", "Sample rate", OptionKind.Choice, "48000",
            "Requested PCM sample rate (Hz). 48000 is Cartesia's full quality; changing this regenerates cached audio.",
            Choices: ["48000", "44100", "24000", "22050"]),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["model_id"] = settings.Get("model_id", "sonic-3"),
            ["transcript"] = text,
            ["voice"] = new Dictionary<string, object>
            {
                ["mode"] = "id",
                ["id"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid") : voice,
            },
            ["language"] = settings.Get("language", "en"),
            ["output_format"] = new Dictionary<string, object>
            {
                ["container"] = "wav",
                ["encoding"] = "pcm_s16le",
                ["sample_rate"] = settings.GetInt("sample_rate", 48000),
            },
            ["speed"] = settings.Get("speed", "normal"),
        };
        return await ProviderHelpers.PostForAudioAsync(
            _http, "https://api.cartesia.ai/tts/bytes", body, cancellationToken,
            request =>
            {
                request.Headers.Add("X-API-Key", settings.Get("API_KEY"));
                request.Headers.Add("Cartesia-Version", "2024-11-13");
            });
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.cartesia.ai/voices?limit=100");
        request.Headers.Add("X-API-Key", settings.Get("API_KEY"));
        request.Headers.Add("Cartesia-Version", "2024-11-13");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var voices = new List<TtsVoice>();
        var root = json.RootElement;
        var list = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) ? data : default;
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var voice in list.EnumerateArray())
            {
                var id = voice.GetProperty("id").GetString() ?? "";
                var name = voice.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? id : id;
                var gender = voice.TryGetProperty("gender", out var genderElement)
                    ? genderElement.GetString()?.ToLowerInvariant() switch
                    {
                        "feminine" or "female" => VoiceGender.Female,
                        "masculine" or "male" => VoiceGender.Male,
                        _ => VoiceGender.Unknown,
                    }
                    : VoiceGender.Unknown;
                voices.Add(new TtsVoice(id, name, gender));
            }
        }
        return voices;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            var voices = await ListVoicesAsync(settings, cancellationToken);
            return $"authenticated; {voices.Count} voice(s)";
        });
}

/// <summary>Inworld: POST /tts/v1/voice with Basic auth; JSON response
/// carries base64 LINEAR16 PCM at the requested sample rate.</summary>
public sealed class InworldProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public InworldProvider(HttpClient http) => _http = http;

    public string Id => "inworld";
    public string DisplayName => "Inworld";
    public bool IsCloud => true;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => "https://platform.inworld.ai/";

    public string? UsageNotes =>
        "Voice steering (inworld-tts-2): the model reads natural-language stage directions " +
        "in square brackets placed BEFORE the line, like directing a voice actor:\n" +
        "    [speak as if barely holding back rage, through gritted teeth] I told you already.\n" +
        "A rich instruction layering emotion, pacing, volume, pitch, and vocal style performs " +
        "better than a bare tag like [sad]. Non-verbal sounds can be inserted inline where " +
        "they occur: [laugh] [sigh] [breathe] [clear throat] [cough] [yawn]. Steering must be " +
        "written in English; the brackets are performed, not read aloud.\n" +
        "Note that speed words (slowly, drawn-out, hesitant, measured) genuinely stretch the " +
        "delivery, so auto-tagging keeps ordinary lines at a conversational tempo and saves " +
        "them for moments that need the weight.\n" +
        "On tts-1.5 models use simple start-of-line markups instead ([happy], [whispering]) — " +
        "TTS-2 steering sentences would be read literally there.";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("API_KEY", "API credential (Base64)", OptionKind.Secret, "", "The Base64 credential from the Inworld portal (used as Basic auth)."),
        new("voiceid", "Voice", OptionKind.Text, "", "Inworld voice id."),
        new("model_id", "Model", OptionKind.Choice, "inworld-tts-2", Choices: ["inworld-tts-1.5", "inworld-tts-2"]),
        new("language", "Language", OptionKind.Choice, "en-US",
            Choices: ["en-US", "zh-CN", "ko-KR", "ja-JP", "ru-RU", "it-IT", "es-ES", "pt-BR", "de-DE", "fr-FR", "ar-SA", "pl-PL", "nl-NL", "hi-IN", "he-IL"]),
        new("delivery", "Delivery", OptionKind.Choice, "balanced",
            "Inworld Studio's Delivery slider (stable = consistent, creative = expressive). Used by inworld-tts-2.",
            Choices: ["stable", "balanced", "creative"]),
        new("temperature", "Temperature", OptionKind.Number, "1.1",
            "Sampling temperature (0-2). Used by inworld-tts-1.x models; inworld-tts-2 ignores it (use Delivery).", 0, 2),
        new("speed", "Speed", OptionKind.Number, "1.0", "Speaking rate (0.5-1.5).", 0.5, 1.5),
        new("sample_rate", "Sample rate", OptionKind.Choice, "48000",
            "Requested PCM sample rate (Hz). 48000 is Inworld's full quality; changing this regenerates cached audio.",
            Choices: ["48000", "44100", "24000", "22050"]),
        new("enhance", "Enhanced audio quality", OptionKind.Toggle, "true",
            "Inworld's post-synthesis denoising (the Studio \"Enhanced\" toggle) — reduces " +
            "background noise and artifacts. On by default; changing it regenerates cached audio."),
        new("auto_tag", "Emotion auto-tagging", OptionKind.Toggle, "false",
            "Uses Inworld's LLM Router (same API key, billed at provider rates) to add a short " +
            "steering instruction and non-verbal tags to each line before synthesis. " +
            "inworld-tts-2 only; each line is tagged once and cached. Toggling regenerates cached audio."),
        new("tag_model", "Tagging model", OptionKind.Choice, "groq/llama-3.1-8b-instant",
            "LLM Router model used for auto-tagging (fast, inexpensive models; all verified " +
            "available on Inworld's router). The default is the fastest model measured that " +
            "returns stable output for a repeated line — tagging is cached per line, so a model " +
            "that varies between identical calls costs extra synthesis. Any other provider/model " +
            "string the router supports also works, but some need plan credits — the Test " +
            "synthesis box reports availability errors.",
            Choices:
            [
                "groq/llama-3.1-8b-instant",
                "openai/gpt-4o-mini",
                "google/gemini-2.5-flash-lite",
                "google/gemini-3.1-flash-lite",
                "groq/llama-3.1-8b-instant",
                "mistral/ministral-3b-2512",
                "mistral/ministral-8b-2512",
                "mistral/mistral-small-latest",
                "openai/gpt-4.1-nano",
                "openai/gpt-5-nano",
                "deepseek/deepseek-v4-flash",
            ]),
    ];

    // ---- emotion auto-tagging ---------------------------------------------

    private const string TaggerSystemPrompt =
        "You add Inworld TTS-2 voice steering to lines of video game dialogue. " +
        "Reply with the line unchanged except for: (1) exactly one steering instruction " +
        "in square brackets placed before the line, written like direction to a voice actor " +
        "(emotion, attitude, volume, pitch, vocal style; under 15 words); " +
        "(2) optional non-verbal tags from [laugh] [breathe] [clear throat] [sigh] [cough] [yawn] " +
        "inserted inline where the sound occurs; " +
        "(3) optionally capitalizing the word or syllable the delivery stresses (NOT, aGAIN) — " +
        "one for most lines, two when the line has a genuine build or reversal (a rhetorical " +
        "question, a threat that lands harder on its last word, a correction), none for a flat " +
        "or purely functional line. " +
        "Capitalizing changes letter CASE only: every letter of the word must survive exactly " +
        "(alright → alRIGHT, never aRIGHT; because → beCAUSE, never bCAUSE). " +
        "(4) intensifying the line's own end punctuation specifically where the delivery is " +
        "LOUD or explosive — a shout, a furious outburst, a desperate cry. Used as needed, not " +
        "as a default: most lines, even strongly felt ones, keep their own punctuation — a " +
        "quiet threat, a grief-stricken line, or anything delivered hushed or controlled never " +
        "gains a mark it did not have. Where a line truly is shouted, mere \"?\" or \"!\" " +
        "undersells it: \"How many of you do I have to kill to save your lives?\" becomes " +
        "\"...lives?!\"; \"Get away from her!\" becomes \"Get away from her!!\"; a flat " +
        "statement screamed as an accusation can gain a \"!\" it did not have (\"We had a " +
        "deal.\" → \"We had a deal!\"). Never use an ellipsis or add a dash for a pause: that " +
        "stretches the delivery exactly like the banned words below. " +
        "Real dialogue must keep its spoken words exactly (punctuation is not a word — rule 4 " +
        "above is the one exception to \"exactly\"). The line may already contain non-verbal " +
        "tags such as [sigh]; keep every one of them where they are. " +
        "COMMIT TO A REAL EMOTION on every line — a performance with a point of view beats a " +
        "safe one. [neutral], [calm], [normal tone] and [steady] are wasted takes: work out what " +
        "the line is actually doing — a threat, a warning, a plea, a boast, a joke, suspicion, " +
        "relief, grief, disgust, affection, adrenaline after a fight — and name that feeling " +
        "with the attitude under it, layering two or three qualities that reinforce each other — " +
        "four for a line at the true extreme, and there layer in a PHYSICAL sensation of the " +
        "body producing the sound, not just an emotion word: breathing hard, teeth gritted, " +
        "throat tight, voice shaking, jaw clenched. [angry, threatening, heavily breathing, " +
        "full of hatred] is what an actual rage line calls for, not a single polite adjective. " +
        "Prefer a quality that MOVES over one that only sits there: where the line has a shift " +
        "in it, name the shift, not just the mood — pitch rising through a threat, volume " +
        "dropping to a hush on the last word, a laugh breaking through on the turn, a crack or " +
        "catch where it hurts, building rather than flat throughout. A static mood is the " +
        "fallback for a line that truly holds steady, not the default: " +
        "[cold, teeth-gritted, dangerously quiet], [warm, relief breaking into a short laugh], " +
        "[sharp, incredulous, pitch climbing], [gruff but gentle, softening on her name], " +
        "[building fury, volume rising to the last word], [voice cracking on the last word]. " +
        "A line that repeats a word or phrase for emphasis (\"not today, not tomorrow... " +
        "NOTHING is gonna change that — NOTHING\") may capitalize that word each time it lands, " +
        "not just once — the repetition is the point, and rule (3)'s cap is per distinct " +
        "emphasis, not per line. " +
        "Even a plain functional line has a mood behind it: someone giving directions is " +
        "impatient, wary, or glad to help, never blank. " +
        "Pacing matters: default to a natural conversational tempo, the way someone actually " +
        "talks mid-conversation. Emotion comes from feeling, attitude, volume and pitch, never " +
        "from speed — an intense line is intense at full tempo. " +
        "The words slow, slowly, drawn-out, deliberate, measured, halting, hesitant, weary, " +
        "resigned, solemn, sombre, trailing off, and any request for pauses all stretch the " +
        "delivery and break it into pauses the sentence never called for: never use them or " +
        "synonyms of them, on any line. Carry weight through feeling, attitude, or volume " +
        "instead — bitter, quiet, flat, hard, warm, shaken. When in doubt, choose the brisker " +
        "reading. " +
        "Written actions in *asterisks* or (parentheses) are stage directions, not spoken " +
        "words: replace them with the matching non-verbal tag; never leave asterisk or " +
        "parenthetical text in the line. " +
        "Exception: when the whole line is a written vocalization, sound, or action rather " +
        "than real dialogue (e.g. '*Hums*', 'Hm hmm.', 'Psst.', 'Ahem.', '*Whistles*'), " +
        "rewrite it as a performable version — a vivid instruction plus vocalization text. " +
        "Write that vocalization phonetically, so a voice reading it aloud produces the sound " +
        "itself: closed-mouth hums are m-heavy and melodic ('Mm-hm-mmm, hm-mm-hmmm…'), " +
        "whistles airy ('Fwee-hoo-whee!', 'Hwoo-hwee…'), scoffs clipped ('Tch.', 'Pfft.'), " +
        "groans drawn out ('Ughhh…'). Elongate with repeated letters, hyphens, and ellipses " +
        "so it is performed, not read as words — this elongation is for these vocalization " +
        "lines only, and is never a licence to slow spoken dialogue. " +
        "A take number is provided: vary the syllable pattern, length, and melodic shape " +
        "between takes so no two takes sound alike — never copy the examples verbatim. " +
        "When a line names several sounds or actions (e.g. humming and whistling), the " +
        "performance must include every one of them. " +
        "Reply with the finished line only, no explanations.";

    /// <summary>Scene direction appended to the tagger's system prompt, only
    /// when the game actually reported something noteworthy.  Measured: the
    /// extra tokens cost no detectable latency (the system prompt already
    /// dwarfs them), and the listener-hostility signal alone flips a line's
    /// whole meaning — "Thanks for the help" is sincere to an ally and
    /// sarcastic to someone who was just shooting at the speaker.</summary>
    internal static string ScenePrompt(bool shoutInCombat) =>
        " SCENE: the line is spoken in the situation described after \"Context:\". Let it decide " +
        "what the line is doing and how it comes out — a line said under gunfire is not the line " +
        "said across a quiet room, and a line said to someone hostile is not the line said to a " +
        "friend. " +
        "VOLUME follows the situation. " +
        (shoutInCombat
            ? "In combat with a hostile listener the speaker is shouting over gunfire at someone " +
              "trying to kill them: reach for loud, shouting, roaring, raised to a yell, and let " +
              "a genuinely shouted line intensify its end punctuation under rule (4). In combat " +
              "with nobody hostile listening the voice is only raised to carry over the noise — " +
              "urgent and carrying, called out to someone on the same side, never enraged or " +
              "roaring at them. "
            : "Combat never raises the volume here: a line under fire is urgent and hard, but it " +
              "is not shouted, roared or yelled, and it does not gain punctuation for volume. " +
              "Carry the pressure through attitude, breath and pitch instead. ") +
        "A hostile listener with no fighting going on is the opposite again: that is where a " +
        "cold, quiet, controlled menace lands hardest, so keep those hard and low rather than " +
        "loud. " +
        "Sneaking always wins: a line delivered while sneaking stays at a whisper no matter who " +
        "is listening or what is happening. " +
        "Worked examples, because the difference is easy to lose: " +
        (shoutInCombat
            ? "in combat, listener hostile — \"Get away from her.\" becomes " +
              "[furious, shouting over the gunfire, hoarse with rage] Get away from her!!; " +
              "in combat, nobody hostile — \"Are you hurt?\" becomes " +
              "[urgent, raised to carry over the noise, afraid for them] Are you hurt?! — never " +
              "[angry, full of hatred], because that listener is on the speaker's side; "
            : "in combat, listener hostile — \"Get away from her.\" becomes " +
              "[hard, teeth gritted, breathing fast] Get away from her.; " +
              "in combat, nobody hostile — \"Are you hurt?\" becomes " +
              "[urgent, tight with worry] Are you hurt?; ") +
        "listener hostile, no combat — \"Drop it. Now.\" becomes " +
        "[cold, quiet, dangerously controlled] Drop it. Now. — low and hard, not shouted; " +
        "sneaking — \"Stay behind me.\" becomes " +
        "[barely above a whisper, tense] Stay behind me. " +
        "Never mention the situation, and never add words to the line because of it.";

    /// <summary>Accent direction appended to the tagger's system prompt.
    /// The model only colours the steering instruction — pronunciation is
    /// applied afterwards in code from the curated IPA lexicon, which needs
    /// the standard spellings intact to match, so the words-unchanged rule
    /// stays fully in force.</summary>
    internal static string AccentPrompt(VoiceMapping.Accent accent, bool slips) =>
        $" ACCENT: the speaker talks in {accent.DisplayName} — {accent.Guidance} " +
        "Let your steering instruction carry that accent's attitude, rhythm and melody (a short " +
        "clause naming the accent is enough). The spoken words themselves must stay spelled exactly " +
        "as written — never respell them and never use dialect spellings; the pronunciation is " +
        "applied after you." +
        (slips
            ? " On this line the speaker's accent eases toward standard speech, so keep the accent " +
              "clause in your instruction mild."
            : "");

    /// <summary>Result of auto-tagging: the (possibly) enriched line, plus
    /// the router failure that forced a rule-based fallback, if any — so
    /// the GUI can say WHY nothing was tagged instead of guessing.</summary>
    public sealed record AutoTagResult(string Text, string? RouterError);

    /// <summary>Adds TTS-2 steering to a line through the Inworld LLM
    /// Router (same API key), falling back to cheap rule-based tags when
    /// the router is unavailable or rewrites the spoken words.  Runs at
    /// temperature 0 so a line tags the same way every time and the audio
    /// cache stays stable.
    ///
    /// <paramref name="retake"/> is the exception: when the user asks the
    /// app for another reading of a line, an identical answer is useless, so
    /// a retake deliberately loosens sampling and asks for a different
    /// interpretation.  Retakes are never the game's own path.</summary>
    private enum DirectiveKind { Normal, ExplicitBracket, ExactText, NonVerbal, PhoneticSound }

    /// <summary>What a typed "Direct this line" instruction resolves to.
    /// For <see cref="DirectiveKind.ExactText"/>, Bracket is the verbatim
    /// bracket and Spoken is whatever the user typed outside it.  For
    /// <see cref="DirectiveKind.NonVerbal"/>, Bracket is the remaining
    /// steering and Spoken is the *action* that replaces the line.  For
    /// Normal, Bracket is the steering direction.  <paramref name="Verbatim"/>
    /// carries the "exact-text" modifier, which only affects the bracket
    /// (verbatim, no vocal fry merged in) and never the markers outside it.</summary>
    private readonly record struct Directive(DirectiveKind Kind, string Bracket, string Spoken, bool Verbatim = false);

    private static readonly System.Text.RegularExpressions.Regex ExactTextMarker =
        new(@"\bexact[-\s]?text\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BracketSpan =
        new(@"\[([^\]]*)\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AsteriskSpan =
        new(@"\*([^*]+)\*", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Parses a typed direction into its handling mode.  The user
    /// may write an explicit [bracket] with content outside it, or just the
    /// direction on its own; "exact-text" inside the bracket and a
    /// *non-verbal* outside it are the two special markers.</summary>
    private static Directive ParseDirective(string raw)
    {
        raw = raw.Trim();
        var bracketMatch = BracketSpan.Match(raw);
        string bracket, outside;
        if (bracketMatch.Success)
        {
            bracket = bracketMatch.Groups[1].Value.Trim();
            outside = (raw[..bracketMatch.Index] + raw[(bracketMatch.Index + bracketMatch.Length)..]).Trim();
        }
        else
        {
            bracket = raw;
            outside = "";
        }

        // "exact-text" is a modifier on the BRACKET only: it makes the bracket
        // verbatim and suppresses the vocal fry that would be merged in.  It
        // never changes what happens outside the bracket, so it is read as a
        // flag here and stripped from the bracket — the markers below still
        // decide the spoken content.
        var verbatim = ExactTextMarker.IsMatch(bracket);
        if (verbatim)
        {
            bracket = ExactTextMarker.Replace(bracket, " ");
            bracket = System.Text.RegularExpressions.Regex.Replace(bracket, @"\s{2,}", " ").Trim(' ', ',', ';');
        }

        // A *marker* outside the bracket (or anywhere, when no explicit
        // bracket was written) decides the spoken content, independent of
        // exact-text.  "*ph*" translates the onomatopoeic line to IPA; any
        // other marker just drops the line and sends the bracket.
        var scope = bracketMatch.Success ? outside : raw;
        var asterisk = AsteriskSpan.Match(scope);
        if (asterisk.Success)
        {
            var steer = bracketMatch.Success
                ? bracket
                : scope.Remove(asterisk.Index, asterisk.Length).Trim();
            var marker = asterisk.Groups[1].Value.Trim().ToLowerInvariant();
            var kind = marker == "ph" ? DirectiveKind.PhoneticSound : DirectiveKind.NonVerbal;
            return new Directive(kind, steer, asterisk.Value, verbatim);
        }

        // exact-text with no marker → the verbatim passthrough: the bracket
        // exactly as typed, plus only what the user typed outside it.
        if (verbatim)
        {
            return new Directive(DirectiveKind.ExactText, bracket, outside);
        }

        // An explicit [bracket] the user wrote is used verbatim as the
        // steering — only free text (no brackets) is handed to the model to
        // interpret.  Writing brackets means "use exactly these".
        return bracketMatch.Success
            ? new Directive(DirectiveKind.ExplicitBracket, bracket, outside)
            : new Directive(DirectiveKind.Normal, bracket, "");
    }

    /// <summary>Translates an onomatopoeic sound line (e.g. "Nnyyyaaarrgghh!")
    /// into inline IPA the synthesizer voices cleanly as that sound — the
    /// "*ph*" Direct marker.  Inworld mangles the raw letter clusters but
    /// reads /IPA/ reliably, producing one continuous vocalization instead of
    /// fragmented letters.  Falls back to a keyword-based vowel if the router
    /// is unavailable, so a sound always comes out.</summary>
    private async Task<string> OnomatopoeiaToIpaAsync(string onomatopoeia, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var messages = new List<object>
        {
            new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] =
                    "You convert a stylized onomatopoeic sound (like \"Nnyyyaaarrgghh!\") into inline " +
                    "IPA a text-to-speech engine voices as that sound. TTS mangles the raw letter " +
                    "clusters, but reads IPA between slashes cleanly. Map the sound to its core " +
                    "vowel, lengthened with ː, plus at most a light consonant: a scream or roar is " +
                    "an open back /ɑːː/ (add a trailing ɹ or x for a rasp: /ɑːːɹ/); a groan is " +
                    "/ʌːː/ or /ɜːː/; a shriek is /iːː/ or /æːː/; a grunt is a short /ʌ/ maybe with a " +
                    "nasal /ʌŋ/; a gasp is /hɑː/. Match the vowel to the letters given. Reply with " +
                    "ONLY the IPA between slashes, e.g. /ɑːːɹ/ — no words, no brackets, no explanation.",
            },
            new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = $"Convert this sound to IPA: {onomatopoeia.Trim()}",
            },
        };
        try
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = settings.Get("tag_model", "groq/llama-3.1-8b-instant"),
                ["temperature"] = 0.3,
                ["max_tokens"] = 24,
                ["messages"] = messages,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.inworld.ai/v1/chat/completions")
            {
                Content = ProviderHelpers.Json(body),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", settings.Get("API_KEY"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var raw = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                var cleaned = CleanIpaSound(raw);
                if (cleaned.Length > 0)
                {
                    return cleaned;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the deterministic default.
        }
        return FallbackIpaSound(onomatopoeia);
    }

    /// <summary>Pulls the /IPA/ span out of the model reply, or wraps a bare
    /// IPA answer in slashes.  Returns "" if nothing usable came back.</summary>
    private static string CleanIpaSound(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }
        var reply = raw.Trim();
        var slashed = System.Text.RegularExpressions.Regex.Match(reply, @"/[^/]+/");
        if (slashed.Success)
        {
            return slashed.Value;
        }
        // No slashes: take the first token, strip brackets/quotes, and wrap it
        // ourselves so it still reaches Inworld as inline IPA.
        var newline = reply.IndexOf('\n');
        if (newline >= 0)
        {
            reply = reply[..newline].Trim();
        }
        reply = System.Text.RegularExpressions.Regex.Replace(reply, @"\[[^\]]*\]", "").Trim(' ', '"', '\'', '`');
        return reply.Length > 0 ? $"/{reply}/" : "";
    }

    /// <summary>Keyword-based IPA vowel so a phonetic take always voices
    /// something even when the router cannot be reached.  Chooses the vowel
    /// from the dominant letters of the onomatopoeia.</summary>
    private static string FallbackIpaSound(string onomatopoeia)
    {
        var s = onomatopoeia.ToLowerInvariant();
        if (s.Contains("ee") || s.Contains("ii")) { return "/iːː/"; }   // shriek
        if (s.Contains("uu") || s.Contains("oo")) { return "/ʌːː/"; }   // groan
        if (s.Contains("ng") || s.Contains("nn")) { return "/ʌŋ/"; }    // grunt
        return "/ɑːːɹ/";   // the default scream / roar
    }

    public async Task<string> AutoTagAsync(string text, string voiceType, bool isPlayer, ProviderSettings settings, CancellationToken cancellationToken, string voicePath = "", VoiceMapping.Accent? accent = null, int accentImperfection = 0, int retake = 0, string scene = "", bool shoutInCombat = true, string direction = "") =>
        (await AutoTagDetailedAsync(text, voiceType, isPlayer, settings, cancellationToken, voicePath, accent, accentImperfection, retake, scene, shoutInCombat, direction)).Text;

    public async Task<AutoTagResult> AutoTagDetailedAsync(string text, string voiceType, bool isPlayer, ProviderSettings settings, CancellationToken cancellationToken, string voicePath = "", VoiceMapping.Accent? accent = null, int accentImperfection = 0, int retake = 0, string scene = "", bool shoutInCombat = true, string direction = "")
    {
        // Two manual escape hatches the "Direct this line" box understands,
        // handled before any automatic tagging so they genuinely bypass it.
        // These act ONLY when the user is directing a line: a non-empty
        // `direction` comes solely from the Direct button.  Normal game
        // dialogue, prefetch, and plain Regenerate all pass no direction, so
        // a line whose own text contains "exact-text" or a *marker* is tagged
        // normally — the markers are read from the direction, never the line.
        if (!string.IsNullOrWhiteSpace(direction))
        {
            var directive = ParseDirective(direction);
            if (directive.Kind == DirectiveKind.ExactText)
            {
                // The user composes exactly what is sent: their bracket
                // verbatim, then only what they typed outside it — the
                // dialogue line is NOT auto-included, and nothing (fry,
                // scene, accent, model) is added.
                var exact = directive.Spoken.Length > 0
                    ? $"[{directive.Bracket}] {directive.Spoken}"
                    : $"[{directive.Bracket}]";
                return new AutoTagResult(exact, null);
            }
            if (directive.Kind == DirectiveKind.NonVerbal)
            {
                // The *...* is only a marker meaning "this line is non-verbal,
                // drop the spoken words" — the asterisk text is never sent.
                // What gets performed is exactly the bracket the user wrote.
                // If they wrote no bracket (just a bare *action*), the action
                // itself is turned into a valid speakable non-verbal, since
                // there is nothing in the brackets to send.
                var body = directive.Bracket.Length > 0
                    ? $"[{directive.Bracket}]"
                    : RuleBasedTags(directive.Spoken);
                return new AutoTagResult(body, null);
            }
            if (directive.Kind == DirectiveKind.PhoneticSound)
            {
                // The dialogue line is itself an onomatopoeic sound (e.g.
                // "Nnyyyaaarrgghh!") that Inworld can't voice — it chokes on
                // the letter clusters and reads them in fragments.  Translate
                // it to inline IPA, which the engine voices cleanly as one
                // continuous sound (measured: IPA is a single sustained burst
                // where the raw spelling fragments).  The bracket, if any,
                // still steers; the character voice (fry) still layers on.
                var ipa = await OnomatopoeiaToIpaAsync(text, settings, cancellationToken);
                var built = directive.Bracket.Length > 0
                    ? $"[{directive.Bracket}] {ipa}"
                    : ipa;
                // exact-text keeps the bracket verbatim — no fry merged in.
                if (!directive.Verbatim && accent?.VoiceTexture is { Length: > 0 })
                {
                    built = EnsureVoiceTexture(built, accent.VoiceTexture);
                }
                return new AutoTagResult(built, null);
            }
            if (directive.Kind == DirectiveKind.ExplicitBracket)
            {
                // The user wrote their own [bracket]: use it verbatim as the
                // steering instead of letting the model paraphrase it.  The
                // line's own words are kept (delivery only), and the
                // character voice (fry) and accent still layer on — that is
                // the difference from exact-text, which strips those.
                var built = $"[{directive.Bracket}] {text}";
                if (accent?.VoiceTexture is { Length: > 0 })
                {
                    built = EnsureVoiceTexture(built, accent.VoiceTexture);
                }
                if (accent is { IsNeutral: false } && VoiceMapping.AccentLexicon.Has(accent) &&
                    settings.Get("model_id", "inworld-tts-2").Contains("tts-2", StringComparison.OrdinalIgnoreCase))
                {
                    var lineKey = voicePath.Length > 0 ? voicePath : text;
                    built = VoiceMapping.AccentLexicon.Apply(accent, built, lineKey, accentImperfection);
                }
                return new AutoTagResult(built, null);
            }
            // Normal direction (free text, no brackets): hand the steering to
            // the model to interpret.
            direction = directive.Bracket;
        }

        var result = await TagCoreAsync(text, voiceType, isPlayer, settings, cancellationToken, voicePath, accent, accentImperfection, retake, scene, shoutInCombat, direction);
        // Enforced, not merely requested: the model reaches for "slow" and
        // "resigned" on lines whose content does not call for them, and
        // those words audibly stretch and fragment the delivery.
        result = result with { Text = ScrubInstruction(result.Text) };

        // A retake exists to sound different, so the one outcome it must not
        // produce is a line with no steering at all — indistinguishable from
        // not tagging.  That happens when loose sampling fills the whole
        // instruction with pacing words and the scrub above empties it, which
        // is common on exactly the grief-and-despair lines people re-roll
        // most.  A roll costs a few hundred milliseconds and this is a
        // deliberate user action, so try again rather than waste the take.
        for (var roll = 0; roll < 3 && retake > 0 && !result.Text.TrimStart().StartsWith('['); roll++)
        {
            var again = await TagCoreAsync(text, voiceType, isPlayer, settings, cancellationToken, voicePath, accent, accentImperfection, retake + roll + 1, scene, shoutInCombat, direction);
            result = again with { Text = ScrubInstruction(again.Text) };
        }
        // A voice texture (Rick Grimes' vocal fry) is central enough to
        // the character that it cannot be left to whether the model
        // happened to mention it — guaranteed onto every line in code,
        // same principle as the pronunciation lexicon.
        if (accent?.VoiceTexture is { Length: > 0 })
        {
            result = result with { Text = EnsureVoiceTexture(result.Text, accent.VoiceTexture) };
        }
        // Accent pronunciation is applied here, in code, from the curated
        // IPA lexicon — deterministic, model-independent, and verified to
        // synthesize correctly on inworld-tts-2 (which reads /IPA/ inline).
        // It runs on every path, including auto-tag off and every tagging
        // fallback, so an accent can never silently come out plain.
        if (accent is { IsNeutral: false } && VoiceMapping.AccentLexicon.Has(accent) &&
            settings.Get("model_id", "inworld-tts-2").Contains("tts-2", StringComparison.OrdinalIgnoreCase))
        {
            var lineKey = voicePath.Length > 0 ? voicePath : text;
            result = result with { Text = VoiceMapping.AccentLexicon.Apply(accent, result.Text, lineKey, accentImperfection) };
        }
        return result;
    }

    private async Task<AutoTagResult> TagCoreAsync(string text, string voiceType, bool isPlayer, ProviderSettings settings, CancellationToken cancellationToken, string voicePath, VoiceMapping.Accent? accent, int accentImperfection, int retake = 0, string scene = "", bool shoutInCombat = true, string direction = "")
    {
        // A direction typed for this specific line is an explicit request to
        // steer it, so it works even with auto-tagging switched off.
        if ((!settings.GetBool("auto_tag", false) && string.IsNullOrWhiteSpace(direction)) ||
            !settings.Get("model_id", "inworld-tts-2").Contains("tts-2", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text) ||
            text.TrimStart().StartsWith('['))
        {
            return new AutoTagResult(text, null);
        }
        string? routerError = null;

        // Asterisk actions are converted deterministically BEFORE the model
        // sees the line — the audible non-verbal must not depend on the
        // model choosing to keep it.
        var prepared = ConvertAsteriskActions(text);

        async Task<string?> CompleteAsync(List<object> a_messages)
        {
            try
            {
                var body = new Dictionary<string, object>
                {
                    ["model"] = settings.Get("tag_model", "groq/llama-3.1-8b-instant"),
                    // Temperature 0 keeps a line's tagging (and therefore its
                    // cached audio) stable.  A retake is the one case where
                    // repeating the previous answer is the failure mode, so it
                    // samples loosely enough to land somewhere else — unless
                    // the user typed a direction, where the goal is to follow
                    // what they asked for rather than to wander.
                    ["temperature"] = !string.IsNullOrWhiteSpace(direction) ? 0.4 : retake > 0 ? 1.0 : 0,
                    ["max_tokens"] = 200,
                    ["messages"] = a_messages,
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.inworld.ai/v1/chat/completions")
                {
                    Content = ProviderHelpers.Json(body),
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", settings.Get("API_KEY"));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _http.SendAsync(request, timeout.Token);
                var responseText = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    routerError = $"LLM router HTTP {(int)response.StatusCode}: {(responseText.Length > 300 ? responseText[..300] : responseText)}";
                    return null;
                }
                using var json = JsonDocument.Parse(responseText);
                return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
            }
            catch (Exception exception)
            {
                routerError = $"LLM router unreachable: {exception.Message}";
                return null;
            }
        }

        try
        {
            // Deterministic per-line take number: the same line always tags
            // identically (stable cache), while different lines with similar
            // content get varied vocalizations.
            var lineKey = voicePath.Length > 0 ? voicePath : text;
            var take = 1 + VoiceMapping.VoiceMapper.Fnv1a(lineKey) % 7;
            var accented = accent is { IsNeutral: false };
            // An ordinary conversation reports nothing, so a calm exchange
            // pays no extra tokens and is steered exactly as before.
            var hasScene = !string.IsNullOrWhiteSpace(scene);
            var hasDirection = !string.IsNullOrWhiteSpace(direction);
            var messages = new List<object>
            {
                new Dictionary<string, string>
                {
                    ["role"] = "system",
                    ["content"] = TaggerSystemPrompt +
                        (hasScene ? ScenePrompt(shoutInCombat) : "") +
                        (accented ? AccentPrompt(accent!, VoiceMapping.Accents.LineSlips(lineKey, accentImperfection)) : ""),
                },
                new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] =
                        $"Speaker: {(isPlayer ? "the player character" : "an NPC")} (voice type {voiceType}). Take {take} of 7.\n" +
                        (hasScene ? $"Context: {scene.Trim()}.\n" : "") +
                        // A direction typed for this one line outranks the
                        // "find something different" nudge: the user has said
                        // what they want, so the take chases that rather than
                        // wandering off after novelty.
                        (hasDirection
                            ? $"THE DIRECTOR ASKS FOR THIS SPECIFICALLY: {direction.Trim()}\n" +
                              "Build the steering instruction around that direction and follow it closely. " +
                              "Everything else above still applies — the spoken words stay exactly as written, " +
                              "and the instruction goes in square brackets before the line.\n"
                            : retake > 0
                                ? "ALTERNATE TAKE: this line has already been performed the obvious way, so find a " +
                                  "genuinely different angle — a different emotion or attitude behind it, a different " +
                                  "word stressed, a different shift in pitch or volume. Still answer in the required " +
                                  "format, steering instruction in square brackets first.\n"
                                : "") +
                        $"Line: {prepared}",
                },
            };
            var tagged = await CompleteAsync(messages);
            if (string.IsNullOrWhiteSpace(tagged) || tagged.Length > text.Length + 250)
            {
                return new AutoTagResult(RuleBasedTags(text), routerError);
            }
            // Any asterisk action the model left behind would be READ ALOUD
            // by the synthesizer.
            tagged = ConvertAsteriskActions(tagged);

            var originalWords = SpokenWords(text);
            var taggedWords = SpokenWords(tagged);
            if (originalWords.Length > 0 && taggedWords == originalWords)
            {
                // Real dialogue, words intact: make sure every pre-existing
                // non-verbal tag survived the model.
                return new AutoTagResult(EnsureNonVerbalsKept(prepared, tagged), null);
            }

            // The accent prompt forbids respelling — pronunciation comes
            // from the IPA lexicon afterwards, and it matches standard
            // spellings only.  If the model respelled anyway, keep its
            // steering instruction but restore the real words.
            if (accented && originalWords.Length > 0)
            {
                var instruction = System.Text.RegularExpressions.Regex.Match(tagged, @"^\s*(\[[^\]]+\])").Groups[1].Value;
                var restored = string.IsNullOrEmpty(instruction) ? prepared : instruction + " " + prepared;
                return new AutoTagResult(EnsureNonVerbalsKept(prepared, restored), null);
            }

            // Rewritten (or action-only) lines: only legitimate for short
            // vocalization/action lines ('Hm hmm.', '*Whistles*', …), and
            // the result must actually carry direction.  Real sentences are
            // never accepted rewritten.
            var wordCount = originalWords.Length == 0 ? 0 : originalWords.Count(c => c == ' ') + 1;
            if (wordCount > 4 || !tagged.Contains('[') || !tagged.Contains(']'))
            {
                return new AutoTagResult(RuleBasedTags(text), null);
            }

            // Two failure modes worth one corrective retry: a compound
            // action losing parts ("*humming and whistling*" tagged as
            // humming only), and an action-only line coming back as bare
            // bracket description with nothing audible — no vocalization
            // text and no official self-sounding tag.
            var missing = MissingActionWords(text, tagged);
            var needsVoice = originalWords.Length == 0 && SpokenWords(tagged).Length == 0 && !HasOfficialNonVerbal(tagged);
            if (missing.Count > 0 || needsVoice)
            {
                var feedback = "Your line missed part of the performance.";
                if (missing.Count > 0)
                {
                    feedback += $" It must include: {string.Join(", ", missing)}.";
                }
                if (needsVoice)
                {
                    feedback += " It must contain minimal vocalization text spelling out the audible sounds (like 'Fwee-hoo.'), not only bracketed direction.";
                }
                messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = tagged });
                messages.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = feedback + " Reply with the corrected line only." });
                var retry = await CompleteAsync(messages);
                if (!string.IsNullOrWhiteSpace(retry) && retry.Length <= text.Length + 250 &&
                    retry.Contains('[') && retry.Contains(']'))
                {
                    tagged = ConvertAsteriskActions(retry);
                }
            }

            // Inworld rejects instruction-only lines outright (HTTP 400,
            // verified): if the result still has no words and no official
            // self-sounding tag, salvage what can be performed.
            if (SpokenWords(tagged).Length == 0 && !HasOfficialNonVerbal(tagged))
            {
                return new AutoTagResult(SalvageNonVerbalLine(text), null);
            }
            return new AutoTagResult(tagged, null);
        }
        catch (Exception)
        {
            return new AutoTagResult(RuleBasedTags(text), routerError);
        }
    }

    /// <summary>Words and phrases that make TTS-2 stretch the delivery and
    /// break phrasing into pauses the sentence does not call for.  The
    /// system prompt already asks the model to avoid them for ordinary
    /// dialogue, but that is advisory and demonstrably does not hold, so
    /// the instruction is scrubbed in code as well.  Phrases go first:
    /// removing "deliberate" from "with deliberate pauses" would
    /// otherwise leave the pauses behind.</summary>
    private static readonly string[] PacingPhrases =
    [
        @"\bwith\s+(deliberate|long|heavy|slight)?\s*pauses?\b",
        @"\b(deliberate|long|heavy|slight)\s+pauses?\b",
        @"\bpaus(es|ing)\b",
        @"\btrailing\s+off\b",
        @"\bdrawn[-\s]out\b",
        @"\bletting\s+(it|the\s+\w+)\s+hang\b",
    ];

    private const string PacingWords =
        @"\b(slow|slowly|slower|resigned|weary|wearily|measured|relaxed|deliberate|" +
        @"deliberately|halting|haltingly|hesitant|hesitantly|solemn|solemnly|sombre|" +
        @"somber|unhurried|laboured|labored|plodding|ponderous|languid|lingering)\b";

    /// <summary>Removes the delivery-stretching direction from a line's
    /// leading steering instruction, tidying the leftover punctuation; an
    /// instruction emptied entirely is dropped.</summary>
    public static string ScrubInstruction(string tagged)
    {
        return System.Text.RegularExpressions.Regex.Replace(tagged, @"^\s*\[[^\]]+\]", match =>
        {
            var inner = match.Value;
            foreach (var phrase in PacingPhrases)
            {
                inner = System.Text.RegularExpressions.Regex.Replace(
                    inner, phrase, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            inner = System.Text.RegularExpressions.Regex.Replace(
                inner, PacingWords, "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            inner = System.Text.RegularExpressions.Regex.Replace(inner, @"\s{2,}", " ");
            inner = System.Text.RegularExpressions.Regex.Replace(inner, @"\s+([,;])", "$1");
            inner = System.Text.RegularExpressions.Regex.Replace(inner, @"([,;])(\s*[,;])+", "$1");
            inner = System.Text.RegularExpressions.Regex.Replace(inner, @"\s*\b(and|with)\s*,", ",");
            // Whatever connective or punctuation the removal stranded at
            // either end goes with it.
            inner = System.Text.RegularExpressions.Regex.Replace(
                inner, @"^\[[\s,;]*(?:\b(?:and|with)\b)?[\s,;]*", "[");
            inner = System.Text.RegularExpressions.Regex.Replace(
                inner, @"[\s,;]*(?:\b(?:and|with)\b)?[\s,;]*\]$", "]");
            return inner == "[]" ? "" : inner;
        }).TrimStart();
    }

    // Words that already say "this voice is creaky" in some form — if the
    // tagger happened to reach for one on its own, injecting the texture
    // phrase again would just be noise stacked on noise.
    private static readonly System.Text.RegularExpressions.Regex VoiceTexturePresent =
        new(@"\b(fry|creak\w*|rasp\w*|husky|hoarse)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Folds an accent's phonation-quality phrase into the line's
    /// leading steering instruction — merged into an existing one so it
    /// rides alongside whatever emotion the tagger picked (measured live:
    /// the texture phrase's acoustic effect survives being combined with
    /// an unrelated mood tag), or standing as its own bracket when the
    /// line has no instruction at all, including the plain-text fallback
    /// paths (router down, rule-based tagging) — this is a trait of the
    /// voice itself, not conditional on auto-tagging having succeeded.
    /// Idempotent: a tag that already reads as creaky is left alone.</summary>
    internal static string EnsureVoiceTexture(string tagged, string texture)
    {
        // A pure vocalization ("[sigh]" with no spoken words after it)
        // must stay an exact match for Inworld to render it as the
        // official self-vocalizing sound — merging text in would turn it
        // into an ordinary steering bracket and drop the sound entirely.
        // No spoken words also means no voice is actually pronouncing
        // anything for a phonation quality to colour.
        if (SpokenWords(tagged).Length == 0)
        {
            return tagged;
        }
        var leading = System.Text.RegularExpressions.Regex.Match(tagged, @"^\s*\[([^\]]+)\]");
        if (!leading.Success)
        {
            return $"[{texture}] {tagged}".TrimEnd();
        }
        if (VoiceTexturePresent.IsMatch(leading.Groups[1].Value))
        {
            return tagged;
        }
        var merged = leading.Groups[1].Value.TrimEnd().TrimEnd(',') + ", " + texture;
        return tagged[..leading.Groups[1].Index] + merged + tagged[(leading.Groups[1].Index + leading.Groups[1].Length)..];
    }

    // Tags Inworld performs as sounds on their own, no text needed.
    private static readonly string[] OfficialNonVerbals =
        ["[laugh]", "[breathe]", "[clear throat]", "[sigh]", "[cough]", "[yawn]"];

    private static bool HasOfficialNonVerbal(string tagged) =>
        OfficialNonVerbals.Any(tag => tagged.Contains(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Content words of the original's written actions that the
    /// performance does not reflect.  Words are stemmed loosely so
    /// "whistling" is found in "whistles a tune"; short connectives and
    /// adverbs are not required.</summary>
    internal static List<string> MissingActionWords(string original, string tagged)
    {
        var missing = new List<string>();
        var haystack = tagged.ToLowerInvariant();
        foreach (System.Text.RegularExpressions.Match action in StageDirection.Matches(original))
        {
            var content = action.Groups[1].Success ? action.Groups[1].Value : action.Groups[2].Value;
            foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(content.ToLowerInvariant(), @"[a-z']+"))
            {
                var token = word.Value;
                if (token.Length < 4 || token.EndsWith("ly", StringComparison.Ordinal))
                {
                    continue;
                }
                var stem = token;
                foreach (var suffix in new[] { "ing", "ed", "es", "s" })
                {
                    if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length - suffix.Length >= 3)
                    {
                        stem = stem[..^suffix.Length];
                        break;
                    }
                }
                if (stem.Length > 3 && stem[^1] == stem[^2])
                {
                    stem = stem[..^1];  // humming -> humm -> hum
                }
                if (!haystack.Contains(stem, StringComparison.Ordinal))
                {
                    missing.Add(token);
                }
            }
        }
        return missing;
    }

    /// <summary>Tagging without an LLM: written stage directions in
    /// asterisks become real tags, and pure written vocalizations get a
    /// matching instruction.</summary>
    internal static string RuleBasedTags(string text)
    {
        var result = ConvertAsteriskActions(text);
        if (!result.TrimStart().StartsWith('['))
        {
            var words = SpokenWords(result);
            if (words is "hm hmm" or "hmm" or "hm" or "mhm" or "mm hmm")
            {
                result = "[humming a little tune] " + result;
            }
        }
        // A bracket-only line without an official tag would be rejected by
        // Inworld (HTTP 400) — never emit one.
        if (SpokenWords(result).Length == 0 && !HasOfficialNonVerbal(result))
        {
            return SalvageNonVerbalLine(text);
        }
        return result;
    }

    /// <summary>Last resort for an action line nothing else could make
    /// performable: the official tags matching any recognizable action
    /// (those self-vocalize), else the bare words with asterisks stripped —
    /// read aloud, but audible beats a rejected synthesis.</summary>
    internal static string SalvageNonVerbalLine(string original)
    {
        var lower = original.ToLowerInvariant();
        var tags = new List<string>();
        void Add(string tag)
        {
            if (!tags.Contains(tag))
            {
                tags.Add(tag);
            }
        }
        if (lower.Contains("sigh")) { Add("[sigh]"); }
        if (lower.Contains("laugh") || lower.Contains("chuckle") || lower.Contains("giggle")) { Add("[laugh]"); }
        if (lower.Contains("cough")) { Add("[cough]"); }
        if (lower.Contains("yawn")) { Add("[yawn]"); }
        if (lower.Contains("breath")) { Add("[breathe]"); }
        if (lower.Contains("clear") && lower.Contains("throat")) { Add("[clear throat]"); }
        if (tags.Count > 0)
        {
            return string.Join(" ", tags);
        }
        // Hums and whistles have no official tag; a phonetic default beats
        // the written word being read aloud.
        if (lower.Contains("hum"))
        {
            return "[humming a little tune] Mm-hm-hmm.";
        }
        if (lower.Contains("whistl"))
        {
            return "[whistling a light tune] Fwee-hoo.";
        }
        var stripped = original.Replace("*", "").Replace("(", "").Replace(")", "").Trim();
        return stripped.Length > 0 ? stripped : original;
    }

    /// <summary>If the model dropped any of the line's pre-existing
    /// non-verbal tags (folding the sigh into its instruction, say), keep
    /// the model's leading instruction but restore the prepared line — the
    /// audible non-verbals are not the model's to remove.</summary>
    internal static string EnsureNonVerbalsKept(string prepared, string tagged)
    {
        var requiredTags = System.Text.RegularExpressions.Regex.Matches(prepared, @"\[[^\]]+\]");
        var allKept = true;
        foreach (System.Text.RegularExpressions.Match tag in requiredTags)
        {
            if (!tagged.Contains(tag.Value, StringComparison.OrdinalIgnoreCase))
            {
                allKept = false;
                break;
            }
        }
        if (allKept)
        {
            return tagged;
        }
        var instruction = System.Text.RegularExpressions.Regex.Match(tagged, @"^\s*(\[[^\]]+\])").Groups[1].Value;
        return string.IsNullOrEmpty(instruction) ? prepared : instruction + " " + prepared;
    }

    /// <summary>Written stage directions come delimited either way in
    /// game dialogue scripts — *whispers* or (whispers) — and mean the
    /// same thing: a note for the actor, never a word to say.</summary>
    private static readonly System.Text.RegularExpressions.Regex StageDirection =
        new(@"\*([^*]+)\*|\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Converts written stage directions — *asterisk* or
    /// (parenthetical), game dialogue scripts use both for the same
    /// thing — to tags the synthesizer performs instead of reads:
    /// Inworld's official non-verbal tag when one matches, a bracketed
    /// steering description otherwise.  Neither delimiter's text may
    /// survive to synthesis — it would be spoken aloud, and for a style
    /// note like (whispers) that means the word "whispers" gets said
    /// instead of the line actually being whispered.</summary>
    internal static string ConvertAsteriskActions(string text) =>
        StageDirection.Replace(text, match =>
        {
            var action = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                .Trim().ToLowerInvariant();
            var wordCount = System.Text.RegularExpressions.Regex.Matches(action, @"[a-z']+").Count;
            // Only a single-word action maps to an official tag — a
            // compound like "laughs and claps" keeps every part as a
            // steering description, or sounds would be silently dropped.
            if (wordCount == 1)
            {
                if (action.Contains("sigh")) { return "[sigh]"; }
                if (action.Contains("laugh") || action.Contains("chuckle") || action.Contains("giggle")) { return "[laugh]"; }
                if (action.Contains("cough")) { return "[cough]"; }
                if (action.Contains("yawn")) { return "[yawn]"; }
                if (action.Contains("breath")) { return "[breathe]"; }
                // Not a self-vocalizing sound like the ones above — a
                // whisper needs the line's own words, just spoken
                // differently, so it becomes a vocal-style instruction
                // rather than an official tag.  The fuller phrasing is
                // Inworld's own documented example; a bare [whisper]
                // steers more weakly.
                if (action.Contains("whisper")) { return "[whisper in a hushed style]"; }
            }
            else if (wordCount == 2 && action.Contains("clear") && action.Contains("throat"))
            {
                return "[clear throat]";
            }
            return "[" + action + "]";
        });

    private static string SpokenWords(string text)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\*[^*]*\*", " ");
        var builder = new System.Text.StringBuilder(stripped.Length);
        foreach (var character in stripped)
        {
            if (char.IsLetterOrDigit(character) || character == '\'')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }
        return builder.ToString().Trim();
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var sampleRate = settings.GetInt("sample_rate", 48000);
        var body = new Dictionary<string, object>
        {
            ["text"] = text,
            ["voiceId"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid") : voice,
            ["modelId"] = settings.Get("model_id", "inworld-tts-2"),
            ["language"] = settings.Get("language", "en-US"),
            ["audioConfig"] = new Dictionary<string, object>
            {
                ["audioEncoding"] = "LINEAR16",
                ["sampleRateHertz"] = sampleRate,
                ["speakingRate"] = settings.GetDouble("speed", 1.0),
            },
            ["temperature"] = settings.GetDouble("temperature", 1.1),
            // The Studio "Delivery" slider; tts-2 uses this and ignores
            // temperature, tts-1.x the reverse — sending both is harmless.
            ["deliveryMode"] = settings.Get("delivery", "balanced").ToUpperInvariant(),
            // Post-synthesis denoising (the Studio "Enhanced" toggle).
            ["enhanceGeneration"] = settings.GetBool("enhance", true),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.inworld.ai/tts/v1/voice")
        {
            Content = ProviderHelpers.Json(body),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", settings.Get("API_KEY"));
        using var response = await _http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Inworld returned {(int)response.StatusCode}: {ProviderHelpers.TrimForError(responseBody)}");
        }

        using var json = JsonDocument.Parse(responseBody);
        var audioContent = json.RootElement.GetProperty("audioContent").GetString()
            ?? throw new HttpRequestException("Inworld response had no audioContent");
        var audio = Convert.FromBase64String(audioContent);
        return ProviderHelpers.LooksLikeRiff(audio) ? audio : ProviderHelpers.WrapPcmAsWav(audio, sampleRate);
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.inworld.ai/voices/v1/voices");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", settings.Get("API_KEY"));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var voices = new List<TtsVoice>();
        if (json.RootElement.TryGetProperty("voices", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var voice in list.EnumerateArray())
            {
                var id = voice.TryGetProperty("voiceId", out var idElement) ? idElement.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                var gender = voice.TryGetProperty("gender", out var genderElement)
                    ? genderElement.GetString()?.ToUpperInvariant() switch
                    {
                        "VOICE_GENDER_FEMALE" or "FEMALE" => VoiceGender.Female,
                        "VOICE_GENDER_MALE" or "MALE" => VoiceGender.Male,
                        _ => VoiceGender.Unknown,
                    }
                    : VoiceGender.Unknown;
                voices.Add(new TtsVoice(id, id, gender));
            }
        }
        return voices;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            var voices = await ListVoicesAsync(settings, cancellationToken);
            return $"authenticated; {voices.Count} voice(s)";
        });
}

/// <summary>Deepgram Aura: POST /v1/speak with token auth; wav out.</summary>
public sealed class DeepgramProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public DeepgramProvider(HttpClient http) => _http = http;

    public string Id => "deepgram";
    public string DisplayName => "Deepgram Aura";
    public bool IsCloud => true;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => "https://console.deepgram.com/";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("API_KEY", "API key", OptionKind.Secret, ""),
        new("model", "Voice model", OptionKind.Text, "aura-asteria-en", "Deepgram Aura voice, e.g. aura-asteria-en, aura-orion-en."),
        new("sample_rate", "Sample rate", OptionKind.Number, "48000", "Linear16 sample rate (48000 = full quality).", 8000, 48000),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var model = string.IsNullOrEmpty(voice) ? settings.Get("model", "aura-asteria-en") : voice;
        var sampleRate = settings.GetInt("sample_rate", 24000);
        var url = $"https://api.deepgram.com/v1/speak?model={Uri.EscapeDataString(model)}&encoding=linear16&sample_rate={sampleRate}&container=wav";
        return await ProviderHelpers.PostForAudioAsync(
            _http, url, new Dictionary<string, object> { ["text"] = text }, cancellationToken,
            request => request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", settings.Get("API_KEY")));
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<TtsVoice> voices =
        [
            new TtsVoice("aura-asteria-en", "Asteria", VoiceGender.Female),
            new TtsVoice("aura-luna-en", "Luna", VoiceGender.Female),
            new TtsVoice("aura-stella-en", "Stella", VoiceGender.Female),
            new TtsVoice("aura-athena-en", "Athena", VoiceGender.Female),
            new TtsVoice("aura-hera-en", "Hera", VoiceGender.Female),
            new TtsVoice("aura-orion-en", "Orion", VoiceGender.Male),
            new TtsVoice("aura-arcas-en", "Arcas", VoiceGender.Male),
            new TtsVoice("aura-perseus-en", "Perseus", VoiceGender.Male),
            new TtsVoice("aura-angus-en", "Angus", VoiceGender.Male),
            new TtsVoice("aura-orpheus-en", "Orpheus", VoiceGender.Male),
            new TtsVoice("aura-helios-en", "Helios", VoiceGender.Male),
            new TtsVoice("aura-zeus-en", "Zeus", VoiceGender.Male),
        ];
        return Task.FromResult(voices);
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            var bytes = await SynthesizeAsync("Test.", "", settings, cancellationToken);
            return $"authenticated; test synthesis returned {bytes.Length} bytes";
        });
}
