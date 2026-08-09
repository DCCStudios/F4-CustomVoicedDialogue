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
        new("auto_tag", "Emotion auto-tagging", OptionKind.Toggle, "false",
            "Uses Inworld's LLM Router (same API key, billed at provider rates) to add a short " +
            "steering instruction and non-verbal tags to each line before synthesis. " +
            "inworld-tts-2 only; each line is tagged once and cached. Toggling regenerates cached audio."),
        new("tag_model", "Tagging model", OptionKind.Choice, "openai/gpt-4o-mini",
            "LLM Router model used for auto-tagging (fast, inexpensive models; all verified " +
            "available on Inworld's router). Any other provider/model string the router supports " +
            "also works, but some need plan credits — the Test synthesis box reports availability " +
            "errors.",
            Choices:
            [
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
        "Reply with the line unchanged except for: (1) at most one short steering instruction " +
        "in square brackets placed before the line, written like direction to a voice actor " +
        "(emotion, pacing, volume, pitch, vocal style; under 15 words); " +
        "(2) optional non-verbal tags from [laugh] [breathe] [clear throat] [sigh] [cough] [yawn] " +
        "inserted inline where the sound occurs; " +
        "(3) optionally capitalizing the one word or syllable the delivery stresses (NOT, aGAIN) — " +
        "at most one per line, only when the line clearly stresses it. " +
        "Real dialogue must keep its spoken words exactly. The line may already contain " +
        "non-verbal tags such as [sigh]; keep every one of them where they are. " +
        "Pacing matters: default to a natural conversational tempo, the way someone actually " +
        "talks mid-conversation. Steer with feeling, attitude, or volume rather than with speed. " +
        "The words slowly, drawn-out, deliberate, measured, halting, hesitant, weary, resigned, " +
        "solemn, sombre, and trailing off all stretch the delivery: do not use them, or synonyms " +
        "of them, for ordinary dialogue — greetings, questions, directions, trade, banter, " +
        "mild annoyance, or simple tiredness. Reserve them for the rare line whose impact truly " +
        "depends on it: grief, dread, a revelation landing, or a threat meant to hang in the " +
        "air. When in doubt, choose the brisker reading. " +
        "Written actions in asterisks are stage directions, not spoken words: replace them " +
        "with the matching non-verbal tag; never leave asterisk text in the line. " +
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
    /// cache stays stable.</summary>
    public async Task<string> AutoTagAsync(string text, string voiceType, bool isPlayer, ProviderSettings settings, CancellationToken cancellationToken, string voicePath = "", VoiceMapping.Accent? accent = null, int accentImperfection = 0) =>
        (await AutoTagDetailedAsync(text, voiceType, isPlayer, settings, cancellationToken, voicePath, accent, accentImperfection)).Text;

    public async Task<AutoTagResult> AutoTagDetailedAsync(string text, string voiceType, bool isPlayer, ProviderSettings settings, CancellationToken cancellationToken, string voicePath = "", VoiceMapping.Accent? accent = null, int accentImperfection = 0)
    {
        var result = await TagCoreAsync(text, voiceType, isPlayer, settings, cancellationToken, voicePath, accent, accentImperfection);
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

    private async Task<AutoTagResult> TagCoreAsync(string text, string voiceType, bool isPlayer, ProviderSettings settings, CancellationToken cancellationToken, string voicePath, VoiceMapping.Accent? accent, int accentImperfection)
    {
        if (!settings.GetBool("auto_tag", false) ||
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
                    ["model"] = settings.Get("tag_model", "openai/gpt-4o-mini"),
                    ["temperature"] = 0,
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
            var messages = new List<object>
            {
                new Dictionary<string, string>
                {
                    ["role"] = "system",
                    ["content"] = TaggerSystemPrompt + (accented ? AccentPrompt(accent!, VoiceMapping.Accents.LineSlips(lineKey, accentImperfection)) : ""),
                },
                new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] = $"Speaker: {(isPlayer ? "the player character" : "an NPC")} (voice type {voiceType}). Take {take} of 7.\nLine: {prepared}",
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
        foreach (System.Text.RegularExpressions.Match action in System.Text.RegularExpressions.Regex.Matches(original, @"\*([^*]+)\*"))
        {
            foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(action.Groups[1].Value.ToLowerInvariant(), @"[a-z']+"))
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
        var stripped = original.Replace("*", "").Trim();
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

    /// <summary>Converts written asterisk actions to tags the synthesizer
    /// performs instead of reads: Inworld's official non-verbal tag when
    /// one matches, a bracketed steering description otherwise.  Asterisk
    /// text must never survive to synthesis — it would be spoken aloud.</summary>
    internal static string ConvertAsteriskActions(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\*([^*]+)\*", match =>
        {
            var action = match.Groups[1].Value.Trim().ToLowerInvariant();
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
