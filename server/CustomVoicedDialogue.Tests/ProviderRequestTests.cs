using System.Text.Json;
using CustomVoicedDialogue.Server.Providers;
using CustomVoicedDialogue.Server.VoiceMapping;

namespace CustomVoicedDialogue.Tests;

/// <summary>
/// One test per provider asserting the exact request the service receives
/// (URL, auth header, body fields) against the shapes documented in the
/// HerikaServer PHP sources and provider API docs.
/// </summary>
public class ProviderRequestTests
{
    private static (MockHttpHandler Handler, HttpClient Client) Mock()
    {
        var handler = new MockHttpHandler();
        return (handler, new HttpClient(handler));
    }

    private static JsonElement ParseBody(MockHttpHandler.CapturedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement;

    [Fact]
    public async Task ElevenLabs_SendsVoiceSettingsAndApiKey()
    {
        var (handler, client) = Mock();
        handler.Respond(System.Net.HttpStatusCode.OK, TestAudio.ValidSourceWav(), "audio/mpeg");
        var provider = new ElevenLabsProvider(client);
        var settings = ProviderSettings.Defaults(provider)
            .WithDefaults(provider);
        settings = new ProviderSettings(new Dictionary<string, string>(settings.Values) { ["API_KEY"] = "k-123" }).WithDefaults(provider);

        await provider.SynthesizeAsync("Hello wasteland.", "voiceX", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/voiceX", request.Url);
        Assert.Equal("k-123", request.Headers.Get("xi-api-key"));
        var body = ParseBody(request);
        Assert.Equal("Hello wasteland.", body.GetProperty("text").GetString());
        Assert.Equal("eleven_multilingual_v2", body.GetProperty("model_id").GetString());
        Assert.Equal(0.75, body.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.True(body.GetProperty("voice_settings").GetProperty("use_speaker_boost").GetBoolean());
    }

    [Fact]
    public async Task ElevenLabs_V3DropsSpeakerBoostAndPrefixesTags()
    {
        var (handler, client) = Mock();
        handler.Respond(System.Net.HttpStatusCode.OK, TestAudio.ValidSourceWav(), "audio/mpeg");
        var provider = new ElevenLabsProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "k",
            ["model_id"] = "eleven_v3",
            ["v3_audio_tags"] = "[whispers]",
        }).WithDefaults(provider);

        await provider.SynthesizeAsync("Quiet now.", "v", settings, CancellationToken.None);

        var body = ParseBody(handler.Requests[0]);
        Assert.Equal("[whispers] Quiet now.", body.GetProperty("text").GetString());
        Assert.False(body.GetProperty("voice_settings").TryGetProperty("use_speaker_boost", out _));
    }

    [Fact]
    public async Task OpenAi_SendsBearerAndModel()
    {
        var (handler, client) = Mock();
        handler.Respond(System.Net.HttpStatusCode.OK, TestAudio.ValidSourceWav(), "audio/mpeg");
        var provider = new OpenAiProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string> { ["API_KEY"] = "sk-1" }).WithDefaults(provider);

        await provider.SynthesizeAsync("Hi.", "onyx", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/audio/speech", request.Url);
        Assert.Equal("Bearer sk-1", request.Headers.Get("Authorization"));
        var body = ParseBody(request);
        Assert.Equal("Hi.", body.GetProperty("input").GetString());
        Assert.Equal("tts-1", body.GetProperty("model").GetString());
        Assert.Equal("onyx", body.GetProperty("voice").GetString());
    }

    [Fact]
    public async Task Azure_FetchesTokenThenPostsSsml()
    {
        var (handler, client) = Mock();
        handler.Respond(System.Net.HttpStatusCode.OK, "token-abc"u8.ToArray(), "text/plain");
        handler.Respond(System.Net.HttpStatusCode.OK, TestAudio.ValidSourceWav(), "audio/wav");
        var provider = new AzureProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "azkey",
            ["region"] = "westeurope",
        }).WithDefaults(provider);

        await provider.SynthesizeAsync("Hello <world> & you.", "en-US-NancyNeural", settings, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://westeurope.api.cognitive.microsoft.com/sts/v1.0/issueToken", handler.Requests[0].Url);
        Assert.Equal("azkey", handler.Requests[0].Headers.Get("Ocp-Apim-Subscription-Key"));
        Assert.Equal("https://westeurope.tts.speech.microsoft.com/cognitiveservices/v1", handler.Requests[1].Url);
        Assert.Equal("Bearer token-abc", handler.Requests[1].Headers.Get("Authorization"));
        Assert.Contains("Hello &lt;world&gt; &amp; you.", handler.Requests[1].Body);
        Assert.Contains("en-US-NancyNeural", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Piper_ClampsAndOmitsOptionalFields()
    {
        var (handler, client) = Mock();
        var provider = new PiperProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["length_scale"] = "9.0",  // above the 4.0 clamp
        }).WithDefaults(provider);

        await provider.SynthesizeAsync("Line.", "en_US-amy-low", settings, CancellationToken.None);

        var body = ParseBody(handler.Requests[0]);
        Assert.Equal("http://127.0.0.1:5000/", handler.Requests[0].Url);
        Assert.Equal(4.0, body.GetProperty("length_scale").GetDouble());
        Assert.False(body.TryGetProperty("noise_scale", out _));
        Assert.False(body.TryGetProperty("speaker", out _));
    }

    [Fact]
    public async Task Kokoro_PassesVoiceThroughUnmapped()
    {
        var (handler, client) = Mock();
        var provider = new KokoroProvider(client);
        var settings = ProviderSettings.Defaults(provider);

        // A voice id HerikaServer's Skyrim map would have nulled.
        await provider.SynthesizeAsync("Line.", "af_totally_custom", settings, CancellationToken.None);

        var body = ParseBody(handler.Requests[0]);
        Assert.Equal("http://127.0.0.1:8880/v1/audio/speech", handler.Requests[0].Url);
        Assert.Equal("af_totally_custom", body.GetProperty("voice").GetString());
        Assert.Equal("kokoro", body.GetProperty("model").GetString());
        Assert.Equal("wav", body.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task Xtts_PostsSpeakerWavShape()
    {
        var (handler, client) = Mock();
        var provider = new XttsFastApiProvider(client);
        var settings = ProviderSettings.Defaults(provider);

        await provider.SynthesizeAsync("Line.", "MyClonedVoice", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:8020/tts_to_audio", request.Url);
        var body = ParseBody(request);
        Assert.Equal("MyClonedVoice", body.GetProperty("speaker_wav").GetString());
        Assert.Equal("en", body.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Cartesia_SendsVersionHeaderAndWavFormat()
    {
        var (handler, client) = Mock();
        var provider = new CartesiaProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string> { ["API_KEY"] = "ck" }).WithDefaults(provider);

        await provider.SynthesizeAsync("Line.", "voice-guid", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.cartesia.ai/tts/bytes", request.Url);
        Assert.Equal("ck", request.Headers.Get("X-API-Key"));
        Assert.Equal("2024-11-13", request.Headers.Get("Cartesia-Version"));
        var body = ParseBody(request);
        Assert.Equal("Line.", body.GetProperty("transcript").GetString());
        Assert.Equal("voice-guid", body.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("wav", body.GetProperty("output_format").GetProperty("container").GetString());
    }

    [Fact]
    public async Task Inworld_DecodesBase64PcmIntoWav()
    {
        var (handler, client) = Mock();
        var pcm = new byte[22050 * 2];  // 1s of silence as raw PCM
        handler.RespondJson(JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(pcm) }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "base64cred",
            ["voiceid"] = "Ashar",
        }).WithDefaults(provider);

        var audio = await provider.SynthesizeAsync("Line.", "", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.inworld.ai/tts/v1/voice", request.Url);
        Assert.Equal("Basic base64cred", request.Headers.Get("Authorization"));
        Assert.Equal("Ashar", ParseBody(request).GetProperty("voiceId").GetString());
        // The Studio Delivery slider must reach the wire as deliveryMode.
        Assert.Equal("BALANCED", ParseBody(request).GetProperty("deliveryMode").GetString());
        // Full quality is requested by default.
        Assert.Equal(48000, ParseBody(request).GetProperty("audioConfig").GetProperty("sampleRateHertz").GetInt32());
        // Raw PCM must come back wrapped in a RIFF container.
        Assert.Equal((byte)'R', audio[0]);
        Assert.Equal(pcm.Length + 44, audio.Length);
    }

    [Fact]
    public async Task Inworld_AutoTag_SendsRouterRequestAndKeepsFaithfulResult()
    {
        var (handler, client) = Mock();
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "[bitter, low] Not today." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync("Not today.", "PlayerVoiceMale01", true, settings, CancellationToken.None);

        Assert.Equal("[bitter, low] Not today.", tagged);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.inworld.ai/v1/chat/completions", request.Url);
        Assert.Equal("Basic cred", request.Headers.Get("Authorization"));
        var body = ParseBody(request);
        Assert.Equal("openai/gpt-4o-mini", body.GetProperty("model").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetInt32());
    }

    [Fact]
    public async Task Inworld_AutoTag_RewrittenDialogueFallsBackToRules()
    {
        var (handler, client) = Mock();
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "[cheery] Something else entirely, my friend." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        // Real dialogue must never come back rewritten — fall back to rules.
        var tagged = await provider.AutoTagAsync(
            "*Sighs* Stay quiet, something big is moving down there.", "PlayerVoiceMale01", true, settings, CancellationToken.None);
        Assert.Equal("[sigh] Stay quiet, something big is moving down there.", tagged);
    }

    [Fact]
    public async Task Inworld_AutoTag_AcceptsPerformableRewriteOfVocalizations()
    {
        var (handler, client) = Mock();
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            // Vocalization-only lines may be rewritten into something
            // performable — this is the dynamic non-verbal path.
            choices = new[] { new { message = new { content = "[whistling a short appreciative tune] Fwee-hoo." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync("*Whistles*", "PlayerVoiceMale01", true, settings, CancellationToken.None);
        Assert.Equal("[whistling a short appreciative tune] Fwee-hoo.", tagged);
    }

    [Fact]
    public async Task Inworld_AutoTag_RetriesWhenPartOfACompoundActionIsDropped()
    {
        var (handler, client) = Mock();
        // First answer parrots the prompt's humming example, dropping the
        // whistling; the corrective retry covers both.
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "[humming a casual tune, absent-minded] Hm hm hmm." } } },
        }));
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "[humming a tune, then whistling along brightly] Hm hm hmm, fwee-hoo." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync("*humming and whistling*", "PlayerVoiceMale01", true, settings, CancellationToken.None);

        Assert.Equal("[humming a tune, then whistling along brightly] Hm hm hmm, fwee-hoo.", tagged);
        Assert.Equal(2, handler.Requests.Count);
        var retryContent = ParseBody(handler.Requests[1]).GetProperty("messages")[3].GetProperty("content").GetString();
        Assert.Contains("whistling", retryContent);
    }

    [Fact]
    public void Inworld_MissingActionWords_StemsAndSkipsConnectives()
    {
        Assert.Equal(
            ["whistling"],
            InworldProvider.MissingActionWords("*humming and whistling*", "[humming a casual tune] Hm hm hmm."));
        Assert.Empty(InworldProvider.MissingActionWords("*humming and whistling*", "[hums then whistles a tune] Hmm, fwee."));
        Assert.Empty(InworldProvider.MissingActionWords("*whistles softly*", "[whistling quietly] Fwee."));
        Assert.Empty(InworldProvider.MissingActionWords("No actions here.", "[calm] No actions here."));
    }

    [Fact]
    public void Accent_LexiconAppliesIpaOutsideTags()
    {
        var cockney = Accents.Get("british-cockney");
        const string line = "[irritated, firm] I'm not going to ask you again. Put the gun down and walk away.";
        var applied = AccentLexicon.Apply(cockney, line, "key-1", 0);

        // The steering tag passes through whole; lexicon words become IPA
        // with their punctuation intact.
        Assert.StartsWith("[irritated, firm]", applied);
        Assert.Contains("gun /daːn/ and", applied);
        // "again" rides its hand-written entry — the dictionary's first
        // pronunciation (əˈɡɛn) hides the FACE vowel the accent shifts.
        Assert.Contains("/əˈɡaɪn/.", applied);
        Assert.EndsWith("walk /əˈwaɪ/.", applied);
        // Deterministic — the audio cache depends on it.
        Assert.Equal(applied, AccentLexicon.Apply(cockney, line, "key-1", 0));

        // The same words read differently in a different accent, and
        // capitalized forms still match.
        Assert.Contains("/dʉːn/", AccentLexicon.Apply(Accents.Get("scottish"), "Down! Get down!", "k", 0));
        // Neighbouring substitutions join into ONE span: separately
        // delimited runs make the synthesizer pause mid-sentence.
        Assert.Equal("/mɐɪ ˈbɹʌðə/ knows.",
            AccentLexicon.Apply(Accents.Get("southern-grimes"), "My brother knows.", "k", 0));
        // A span still closes when a plain word follows.
        Assert.Equal("Ask /mɐɪ ˈbɹʌðə/ about it /təˈnɐɪt/.",
            AccentLexicon.Apply(Accents.Get("southern-grimes"), "Ask my brother about it tonight.", "k", 0));
        // Neutral changes nothing at all.
        Assert.Equal(line, AccentLexicon.Apply(Accents.Get(null), line, "key-1", 0));
    }

    [Fact]
    public void Accent_LexiconSlipsEaseTheAccentOff()
    {
        var cockney = Accents.Get("british-cockney");
        const string line = "Take the house down now, mate — my brother will think something got away.";

        // Find a line key that slips at full imperfection and one that
        // does not (both exist: the slip rate tops out around 60%).
        var keys = Enumerable.Range(0, 100).Select(i => $"line-{i}").ToList();
        var slippingKey = keys.First(k => Accents.LineSlips(k, 100));
        var steadyKey = keys.First(k => !Accents.LineSlips(k, 100));

        // Words inside the slash spans, not spans — adjacent substitutions
        // are merged into one span.
        int IpaCount(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"/([^/]+)/")
            .Sum(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var full = IpaCount(AccentLexicon.Apply(cockney, line, steadyKey, 100));
        var slipped = IpaCount(AccentLexicon.Apply(cockney, line, slippingKey, 100));

        // A steady line carries the full accent; a slipping line keeps
        // some of it but visibly eases off.
        Assert.True(full >= 7, $"expected a dense accent, got {full} IPA words");
        Assert.True(slipped < full, $"slipping line ({slipped}) should ease off the steady line ({full})");
        // At zero imperfection nothing ever slips.
        Assert.Equal(full, IpaCount(AccentLexicon.Apply(cockney, line, slippingKey, 0)));
    }

    [Fact]
    public void Accent_LexiconCatalogueIsWellFormed()
    {
        // A sample that touches at least one lexicon word of every accent.
        const string sample = "I think my friend will take the house down that road, but there's nothing better going now, right?";
        var word = new System.Text.RegularExpressions.Regex(@"^[a-z']+$");
        var ipa = new System.Text.RegularExpressions.Regex(@"^[a-zæɑɒɔəɛɜɪʊʌʉɐɵŋθðʃʒɡɹɾʍʔːˈˌ]+$");

        Assert.False(AccentLexicon.Has(Accents.Get(null)));
        Assert.All(Accents.All.Skip(1), accent =>
        {
            Assert.True(AccentLexicon.Has(accent), $"{accent.Id} has no pronunciation lexicon");
            foreach (var entry in AccentLexicon.Entries(accent))
            {
                Assert.True(word.IsMatch(entry.Key), $"{accent.Id}: bad lexicon key '{entry.Key}'");
                Assert.True(ipa.IsMatch(entry.Value), $"{accent.Id}: bad IPA '{entry.Value}' for '{entry.Key}'");
            }
            Assert.Contains('/', AccentLexicon.Apply(accent, sample, "k", 0));
        });

        // Rhotic accents must not carry the non-rhotic r-deletions.
        foreach (var id in new[] { "southern", "deep-south", "scottish", "glaswegian", "irish" })
        {
            Assert.DoesNotContain(AccentLexicon.Entries(Accents.Get(id)), e => e.Key == "car");
        }
        // Southern stays milder than Deep South, per design.
        Assert.True(
            AccentLexicon.Entries(Accents.Get("southern")).Count() <
            AccentLexicon.Entries(Accents.Get("deep-south")).Count());
    }

    [Theory]
    // The rule engine against ground truth: words that were never
    // hand-written must come out with the accent's signature phonology.
    [InlineData("british-cockney", "going", "ˈɡəʊɪn")]     // GOAT + -in
    [InlineData("british-cockney", "walking", "ˈwɔːkɪn")]
    [InlineData("british-cockney", "thinking", "ˈfɪŋkɪn")] // th-fronting
    [InlineData("british-cockney", "hurry", "ˈɜɹiː")]      // h-drop, approximant r
    [InlineData("british-rp", "start", "stɑːt")]           // START keeps ɑː
    [InlineData("british-rp", "forgot", "fəˈɡɒt")]         // non-rhotic + LOT
    [InlineData("british-rp", "father", "ˈfɑːðə")]         // PALM exception
    [InlineData("british-rp", "chances", "ˈtʃɑːnsəz")]     // BATH
    [InlineData("scottish", "found", "fʉːnd")]             // MOUTH on fronted ʉ, rhotic kept
    [InlineData("scottish", "raider", "ˈɾeːdəɾ")]          // tapped r
    [InlineData("scottish", "good", "ɡʉd")]                // FOOT-GOOSE merger
    [InlineData("scottish", "start", "staɾt")]             // START keeps plain a
    [InlineData("scottish", "right", "ɾʌit")]              // short PRICE before voiceless
    [InlineData("scottish", "talk", "tɔk")]                // LOT-THOUGHT on short ɔ
    [InlineData("glaswegian", "water", "ˈwɔʔəɾ")]          // glottal t
    [InlineData("glaswegian", "better", "ˈbɛʔəɾ")]
    [InlineData("boston", "harbor", "ˈhɑːbə")]
    [InlineData("deep-south", "outside", "ˈæʊtˈsɑːd")]     // CMUdict marks both syllables primary
    [InlineData("southern", "time", "tɑːm")]               // matches the hand entry
    [InlineData("southern-grimes", "right", "ɹɐɪt")]       // PRICE keeps a trace of its glide
    [InlineData("southern-grimes", "night", "nɐɪt")]       // "nigh-t": not "naht", not "naight"
    [InlineData("southern-grimes", "brother", "ˈbɹʌðə")]   // unstressed coda r drops
    [InlineData("southern-grimes", "walkers", "ˈwɔːkəz")]
    [InlineData("russian", "everything", "ˈɛvriːˌsɪŋ")]    // th → s
    [InlineData("german", "javelin", "ˈtʃɛvələn")]         // dʒ → tʃ, æ → ɛ
    [InlineData("spanish-mexican", "strange", "esˈtɾeɪndʒ")] // tapped cluster r
    [InlineData("russian", "rifle", "ˈraɪfəl")]            // trilled r
    [InlineData("australian", "party", "ˈpaːɾiː")]         // front START + flapped t
    [InlineData("australian", "getting", "ˈɡɛɾɪn")]        // flap + -in, DRESS stays ɛ
    [InlineData("australian", "sorry", "ˈsɒɹiː")]          // LOT rounds
    [InlineData("australian", "there's", "ðɛːz")]          // SQUARE monophthong by rule
    [InlineData("australian", "start", "staːʔ")]           // the reference page's staːʔ
    [InlineData("australian", "route", "ɹʉːʔ")]            // root, on the fronted GOOSE
    [InlineData("australian", "go", "ɡəʉ")]                // GOAT centres onto fronted GOOSE
    [InlineData("australian", "school", "skʉːl")]          // GOOSE fronts
    [InlineData("australian", "gun", "ɡɐn")]               // STRUT central
    [InlineData("australian", "past", "paːst")]            // fricative BATH, front a
    public void Accent_PhonologyDerivesUnlistedWords(string accentId, string word, string expected)
    {
        Assert.Equal(expected, AccentPhonology.Derive(Accents.Get(accentId), word));
    }

    [Fact]
    public void Accent_PhonologyKnowsItsLimits()
    {
        var cockney = Accents.Get("british-cockney");
        // Unchanged by the rules: stays plain text.
        Assert.Null(AccentPhonology.Derive(cockney, "put"));
        // BATH must not overreach: fricative-before-vowel and nd stay flat.
        Assert.Null(AccentPhonology.Derive(Accents.Get("british-rp"), "classic"));
        Assert.Null(AccentPhonology.Derive(Accents.Get("british-rp"), "hand"));
        // Australian BATH is fricative-only: the nasal words RP broadens
        // keep their flat a (only the vowel differs from GA, so the whole
        // word may still derive — the æ must survive).
        Assert.DoesNotContain("ɑː", AccentPhonology.Derive(Accents.Get("australian"), "chance") ?? "");
        Assert.DoesNotContain("ɑː", AccentPhonology.Derive(Accents.Get("australian"), "plant") ?? "");
        // Function words are never derived (their citation forms would
        // fight sentence rhythm); the hand lexicon overrides them instead.
        Assert.Null(AccentPhonology.Derive(cockney, "the"));
        Assert.Null(AccentPhonology.Derive(Accents.Get("british-rp"), "of"));
        // Heteronyms cannot be disambiguated without part of speech.
        Assert.Null(AccentPhonology.Derive(cockney, "read"));
        // Words the dictionary does not know stay plain.
        Assert.Null(AccentPhonology.Derive(cockney, "stimpak"));

        // Where a rule fully covers a hand-written word the two layers
        // must agree — the overrides only exist for the irregulars.
        Assert.Equal("daːn", AccentPhonology.Derive(Accents.Get("british-cockney"), "down"));
        Assert.Equal("dʉːn", AccentPhonology.Derive(Accents.Get("scottish"), "down"));
        Assert.Equal("wɔːn", AccentPhonology.Derive(Accents.Get("british-rp"), "worn"));
        // PRICE stays long word-finally and before voiced fricatives
        // (Aitken's law) — and why still gets its voiceless wh.
        Assert.DoesNotContain("ʌi", AccentPhonology.Derive(Accents.Get("scottish"), "five") ?? "");
        Assert.Equal("ʍaɪ", AccentPhonology.Derive(Accents.Get("scottish"), "why"));

        // Southern stays milder than Deep South on the same word.
        Assert.Null(AccentPhonology.Derive(Accents.Get("southern"), "right"));
        Assert.Equal("ɹɑːt", AccentPhonology.Derive(Accents.Get("deep-south"), "right"));
        // Rick Grimes: stressed syllables keep their r, and — per the
        // linguists — NO pen-pin merger (general Southern does merge).
        Assert.Null(AccentPhonology.Derive(Accents.Get("southern-grimes"), "hard"));
        Assert.Null(AccentPhonology.Derive(Accents.Get("southern-grimes"), "pen"));
        Assert.Null(AccentPhonology.Derive(Accents.Get("southern-grimes"), "ten"));
        Assert.Equal("pɪn", AccentPhonology.Derive(Accents.Get("southern"), "pen"));
        // And the Coral phenomenon rides the hand lexicon.
        Assert.Contains("/ˈkɔɹəl/", AccentLexicon.Apply(
            Accents.Get("southern-grimes"), "Carl! Stay back, Carl!", "k", 0));
    }

    [Fact]
    public void Accent_DerivedWordsFlowThroughApply()
    {
        var cockney = Accents.Get("british-cockney");
        // "going" and "walking" have no hand entries — the rule engine
        // must carry them; "put" stays plain.
        // Neighbouring substitutions join into one span (see MergeAdjacent).
        var applied = AccentLexicon.Apply(cockney, "Put it down before going out walking.", "k", 0);
        Assert.Equal("Put it /daːn bɪˈfɔː ˈɡəʊɪn aːt ˈwɔːkɪn/.", applied);
    }

    [Fact]
    public void Tagger_StretchingAdjectivesAreScrubbedFromInstructions()
    {
        Assert.Equal("[calm, low] Fine.",
            InworldProvider.ScrubInstruction("[calm and measured, low] Fine."));
        Assert.Equal("[a warm Southern drawl] Sure thing.",
            InworldProvider.ScrubInstruction("[a warm relaxed Southern drawl] Sure thing."));
        Assert.Equal("[quiet] Go.",
            InworldProvider.ScrubInstruction("[measured, quiet] Go."));
        // The words the model actually reaches for in game.
        Assert.Equal("[firm] Not today.",
            InworldProvider.ScrubInstruction("[firm, slow and resigned] Not today."));
        Assert.Equal("[quiet] I know.",
            InworldProvider.ScrubInstruction("[quiet, weary, with deliberate pauses] I know."));
        Assert.Equal("[flat] It's done.",
            InworldProvider.ScrubInstruction("[flat, trailing off] It's done."));
        // An instruction that was nothing but the banned word disappears.
        Assert.Equal("Fine.", InworldProvider.ScrubInstruction("[relaxed] Fine."));
        Assert.Equal("Not today.", InworldProvider.ScrubInstruction("[slow, resigned] Not today."));
        // Untouched lines pass through, including inline non-verbals.
        Assert.Equal("[stern] Put it down. [sigh] Now.",
            InworldProvider.ScrubInstruction("[stern] Put it down. [sigh] Now."));
        Assert.Equal("No tags here.", InworldProvider.ScrubInstruction("No tags here."));
    }

    [Fact]
    public async Task Accent_TaggerKeepsWordsAndLexiconAddsIpa()
    {
        var (handler, client) = Mock();
        // The model behaves: instruction only, words untouched.
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "[irritated, with a quick Cockney edge] I'm not going to ask you again. Put the gun down and walk away." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync(
            "I'm not going to ask you again. Put the gun down and walk away.",
            "PlayerVoiceMale01", true, settings, CancellationToken.None, "",
            Accents.Get("british-cockney"), 0);

        Assert.StartsWith("[irritated, with a quick Cockney edge]", tagged);
        Assert.Contains("/daːn/", tagged);
        Assert.Contains("/əˈwaɪ/", tagged);
        // Words neither hand-listed nor changed by the rules stay real words.
        Assert.Contains("to ask you", tagged);
    }

    [Fact]
    public async Task Accent_TaggerRespellingIsRevertedBeforeTheLexicon()
    {
        var (handler, client) = Mock();
        // The model disobeys and respells — those spellings would miss the
        // lexicon, so the real words must be restored first.
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "[cheeky] I'm not gonna ask ya again. Put the gun dahn and walk awye." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync(
            "I'm not going to ask you again. Put the gun down and walk away.",
            "PlayerVoiceMale01", true, settings, CancellationToken.None, "",
            Accents.Get("british-cockney"), 0);

        Assert.StartsWith("[cheeky]", tagged);
        Assert.DoesNotContain("dahn", tagged);
        Assert.DoesNotContain("awye", tagged);
        Assert.Contains("/daːn/", tagged);
        Assert.Contains("/əˈwaɪ/", tagged);
    }

    [Fact]
    public async Task Accent_LexiconWorksWithoutAutoTagging()
    {
        var (handler, client) = Mock();
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "false",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync(
            "Put the gun down and walk away.",
            "PlayerVoiceMale01", true, settings, CancellationToken.None, "",
            Accents.Get("british-cockney"), 0);

        // No LLM call at all, yet the accent still lands.
        Assert.Empty(handler.Requests);
        Assert.Equal("Put the gun /daːn/ and walk /əˈwaɪ/.", tagged);
    }

    [Fact]
    public void Accent_SlipsAreDeterministicAndScaleWithImperfection()
    {
        // The same line always performs the same way, or the audio cache
        // would serve a different take than the one it keyed.
        Assert.Equal(
            Accents.LineSlips("Sound\\Voice\\Fallout4.esm\\PlayerVoiceMale01\\0001F5FF_1.wav", 50),
            Accents.LineSlips("Sound\\Voice\\Fallout4.esm\\PlayerVoiceMale01\\0001F5FF_1.wav", 50));

        // Zero never slips; higher settings slip more often, but even 100
        // leaves most of the accent intact rather than removing it.
        var keys = Enumerable.Range(0, 400).Select(i => $"line-{i}").ToList();
        Assert.All(keys, key => Assert.False(Accents.LineSlips(key, 0)));

        var atFifteen = keys.Count(key => Accents.LineSlips(key, 15));
        var atFifty = keys.Count(key => Accents.LineSlips(key, 50));
        var atHundred = keys.Count(key => Accents.LineSlips(key, 100));
        Assert.True(atFifteen < atFifty, $"15% ({atFifteen}) should slip less than 50% ({atFifty})");
        Assert.True(atFifty < atHundred, $"50% ({atFifty}) should slip less than 100% ({atHundred})");
        Assert.InRange(atHundred / (double)keys.Count, 0.5, 0.7);
    }

    [Fact]
    public void Accent_CatalogueIsWellFormed()
    {
        Assert.Equal(Accents.Default, Accents.All[0].Id);
        Assert.True(Accents.All[0].IsNeutral);
        // The neutral default must add no direction at all.
        Assert.Equal("", Accents.All[0].Guidance);
        // Every other accent needs a real character note for the tagger.
        Assert.All(Accents.All.Skip(1), accent =>
        {
            Assert.False(accent.IsNeutral);
            Assert.True(accent.Guidance.Length > 30, $"{accent.Id} guidance is too thin");
        });
        Assert.Equal(Accents.All.Count, Accents.All.Select(a => a.Id).Distinct().Count());
        // Unknown or missing ids fall back to neutral rather than throwing.
        Assert.True(Accents.Get("nonsense").IsNeutral);
        Assert.True(Accents.Get(null).IsNeutral);
        Assert.Equal("scottish", Accents.Get("SCOTTISH").Id);
    }

    [Fact]
    public void Inworld_RuleBasedTags_ConvertActionsAndVocalizations()
    {
        Assert.Equal("[hums] Sure.", InworldProvider.RuleBasedTags("*Hums* Sure."));
        Assert.Equal("[humming a little tune] Hm hmm.", InworldProvider.RuleBasedTags("Hm hmm."));
        Assert.Equal("Plain line.", InworldProvider.RuleBasedTags("Plain line."));
        // Known actions map to Inworld's official non-verbal tags.
        Assert.Equal("[sigh] Fine. Have it your way.", InworldProvider.RuleBasedTags("*Sighs* Fine. Have it your way."));
        Assert.Equal("[laugh] Good one.", InworldProvider.RuleBasedTags("*Chuckles* Good one."));
        Assert.Equal("[clear throat] As I was saying.", InworldProvider.RuleBasedTags("*Clears throat* As I was saying."));
        // Compound actions must keep every part.
        Assert.Equal("[laughs and claps] Nice!", InworldProvider.RuleBasedTags("*Laughs and claps* Nice!"));
        // Bracket-only lines without an official tag are rejected by
        // Inworld (400) — salvage official tags or bare words instead.
        Assert.Equal("[sigh]", InworldProvider.RuleBasedTags("*Sighs*"));
        Assert.Equal("[humming a little tune] Mm-hm-hmm.", InworldProvider.RuleBasedTags("*hums melodically*"));
        Assert.Equal("[clear throat]", InworldProvider.SalvageNonVerbalLine("*clears throat and spits*"));
        Assert.Equal("[whistling a light tune] Fwee-hoo.", InworldProvider.SalvageNonVerbalLine("*whistles*"));
    }

    [Fact]
    public async Task Inworld_AutoTag_ConvertsAsteriskActionsTheModelLeftBehind()
    {
        var (handler, client) = Mock();
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            // The model added steering but left the asterisk action in the
            // line — spoken aloud unless converted.
            choices = new[] { new { message = new { content = "[flat] *Sighs* Fine. Have it your way." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync("*Sighs* Fine. Have it your way.", "PlayerVoiceMale01", true, settings, CancellationToken.None);
        Assert.Equal("[flat] [sigh] Fine. Have it your way.", tagged);
    }

    [Fact]
    public async Task Inworld_AutoTag_RestoresNonVerbalsTheModelDropped()
    {
        var (handler, client) = Mock();
        handler.RespondJson(JsonSerializer.Serialize(new
        {
            // The model folded the sigh into its instruction and dropped
            // the audible non-verbal — it must be restored.
            choices = new[] { new { message = new { content = "[flat] Fine. Have it your way." } } },
        }));
        var provider = new InworldProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "cred",
            ["auto_tag"] = "true",
        }).WithDefaults(provider);

        var tagged = await provider.AutoTagAsync("*Sighs* Fine. Have it your way.", "PlayerVoiceMale01", true, settings, CancellationToken.None);
        Assert.Equal("[flat] [sigh] Fine. Have it your way.", tagged);

        // The model was shown the pre-converted line, not raw asterisks.
        var request = Assert.Single(handler.Requests);
        var userContent = ParseBody(request).GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Contains("[sigh] Fine. Have it your way.", userContent);
        Assert.DoesNotContain("*Sighs*", userContent);
    }

    [Fact]
    public async Task Inworld_AutoTag_DisabledOrTts15IsNoOp()
    {
        var (handler, client) = Mock();
        var provider = new InworldProvider(client);
        var off = new ProviderSettings(new Dictionary<string, string> { ["API_KEY"] = "c" }).WithDefaults(provider);
        Assert.Equal("Hello.", await provider.AutoTagAsync("Hello.", "V", true, off, CancellationToken.None));

        var tts15 = new ProviderSettings(new Dictionary<string, string>
        {
            ["API_KEY"] = "c",
            ["auto_tag"] = "true",
            ["model_id"] = "inworld-tts-1.5",
        }).WithDefaults(provider);
        Assert.Equal("Hello.", await provider.AutoTagAsync("Hello.", "V", true, tts15, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Deepgram_EncodesModelInQuery()
    {
        var (handler, client) = Mock();
        var provider = new DeepgramProvider(client);
        var settings = new ProviderSettings(new Dictionary<string, string> { ["API_KEY"] = "dg" }).WithDefaults(provider);

        await provider.SynthesizeAsync("Line.", "aura-orion-en", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.deepgram.com/v1/speak?model=aura-orion-en&encoding=linear16", request.Url);
        Assert.Equal("Token dg", request.Headers.Get("Authorization"));
        Assert.Equal("Line.", ParseBody(request).GetProperty("text").GetString());
    }

    [Fact]
    public async Task XvaSynth_LoadsModelOncePerVoice()
    {
        var (handler, client) = Mock();
        // loadModel + synthesize for the first call, synthesize only after.
        handler.RespondJson("{}");
        handler.RespondJson("{}");
        handler.RespondJson("{}");
        var provider = new XvaSynthProvider(client);
        var settings = ProviderSettings.Defaults(provider);

        // The outfile never appears (no real xVASynth), so expect timeouts —
        // but the request sequencing is what this test asserts.
        await Assert.ThrowsAnyAsync<Exception>(() => provider.SynthesizeAsync("A", "f4_ma_boone", settings, new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.SynthesizeAsync("B", "f4_ma_boone", settings, new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token));

        var loadCalls = handler.Requests.Count(r => r.Url.EndsWith("/loadModel"));
        var synthesisCalls = handler.Requests.Count(r => r.Url.EndsWith("/synthesize"));
        Assert.Equal(1, loadCalls);       // cached for the second call
        Assert.Equal(2, synthesisCalls);
        // No sk_ prefix mangling on Fallout 4 models.
        Assert.Contains("resources/app/models/fallout4/f4_ma_boone", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Mimic3_PostsSsmlWithEscapedText()
    {
        var (handler, client) = Mock();
        var provider = new Mimic3Provider(client);
        var settings = ProviderSettings.Defaults(provider);

        await provider.SynthesizeAsync("Fish & chips", "en_UK/apope_low#default", settings, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:59125/api/tts", request.Url);
        Assert.Contains("Fish &amp; chips", request.Body);
        Assert.Contains("en_UK/apope_low#default", request.Body);
    }
}
