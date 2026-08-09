using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace CustomVoicedDialogue.Server.VoiceMapping;

/// <summary>One speech sound.  <see cref="Stress"/> is -1 for consonants,
/// 0/1/2 (none/primary/secondary) for vowels.</summary>
internal readonly record struct Phone(string Sound, int Stress)
{
    public bool IsVowel => Stress >= 0;
}

/// <summary>
/// Derives an accent pronunciation for any English word: the CMU Pronouncing
/// Dictionary supplies the General American phonemes, and a small set of
/// phonological rules per accent — the actual linguistics, like "delete r
/// unless a vowel follows" or "MOUTH becomes a long flat ah" — transforms
/// them into accent IPA.  A dozen rules cover what would otherwise take
/// hundreds of hand-written lexicon entries; the hand lexicon in
/// <see cref="AccentLexicon"/> stays as an override layer for the
/// irregulars rules cannot produce (something → sʌmfɪŋk, school → eskul).
/// </summary>
internal static class AccentPhonology
{
    /// <summary>The accent IPA for a word, or null when it should stay
    /// plain text: unknown to the dictionary, unchanged by the accent's
    /// rules, a function word (whose citation-form IPA would fight natural
    /// sentence rhythm — the override lexicon handles the ones an accent
    /// really wants), or a heteronym (spelling with two pronunciations we
    /// cannot disambiguate).</summary>
    public static string? Derive(Accent accent, string word)
    {
        if (FunctionWords.Contains(word) || Heteronyms.Contains(word))
        {
            return null;
        }
        var phones = Pronounce(word);
        if (phones is null)
        {
            return null;
        }
        var transformed = new List<Phone>(phones);
        Transform(accent.Id, word, transformed);
        return transformed.SequenceEqual(phones) ? null : Render(transformed);
    }

    private static void Transform(string accentId, string word, List<Phone> p)
    {
        switch (accentId)
        {
            case "southern":
                PriceFlattensBeforeVoiced(p);
                PenPinMerger(p);
                IngToIn(p);
                break;
            case "deep-south":
                Vowel(p, "aɪ", "ɑː");
                Vowel(p, "aʊ", "æʊ");
                Vowel(p, "ɔɪ", "ɔː");
                PenPinMerger(p);
                IngToIn(p);
                break;
            case "southern-grimes":
                // Andrew Lincoln's Rick Grimes: restrained, never a
                // caricature — the analyses stress subtlety over broad
                // Southern vowel changes, so ONLY the well-attested
                // features: PRICE keeps a TRACE of its glide from a
                // raised central onset (night → nɐɪt — "nigh-t", neither
                // the flat "naht" nor the full "naight"), -ing drops its
                // g, the r's stay American except the unstressed codas
                // that audibly soften (brother → brʌðə, walkers →
                // wɔːkəz), and the full-mouth delivery holds every long
                // vowel as a sustained pure sound rather than letting it
                // glide (sweet → swiːːt, "swEET" not "swe-eit").
                // Deliberately NO pen-pin merger and NO MOUTH fronting —
                // linguists note both absences as tells of the portrayal.
                Vowel(p, "aɪ", "ɐɪ");
                UnstressedDerhoting(p);
                IngToIn(p);
                HoldLongVowels(p);
                break;
            case "boston":
                NonRhotic(p);
                break;
            case "new-york":
                if (p.Count > 0 && p[0].Sound == "ð") { p[0] = p[0] with { Sound = "d" }; }
                NonRhotic(p);
                Vowel(p, "ɔː", "ɔə");
                break;
            case "mid-atlantic":
                BathBroadening(p);
                NonRhotic(p);
                break;
            case "british-rp":
                LotRounding(word, p);
                BathBroadening(p);
                Vowel(p, "oʊ", "əʊ");
                NonRhotic(p);
                break;
            case "british-cockney":
                Vowel(p, "aɪ", "ɒɪ");   // PRICE first, so FACE's new aɪ is safe
                Vowel(p, "eɪ", "aɪ");
                Vowel(p, "aʊ", "aː");
                Vowel(p, "oʊ", "əʊ");
                ThFronting(p);
                DropInitialH(p);
                IngToIn(p);
                NonRhotic(p);
                break;
            case "british-north":
                Vowel(p, "ʌ", "ʊ");
                Vowel(p, "eɪ", "eː");
                Vowel(p, "oʊ", "oː");
                NonRhotic(p);
                break;
            case "scottish":
                ScotsCore(word, p);
                break;
            case "glaswegian":
                ScotsCore(word, p);
                GlottalT(p);
                break;
            case "welsh":
                Vowel(p, "eɪ", "eː");
                Vowel(p, "oʊ", "oː");
                NonRhotic(p);
                TapR(p);
                break;
            case "irish":
                Sound(p, "ð", "d");
                Sound(p, "θ", "t");
                Vowel(p, "aɪ", "ɒɪ");
                IngToIn(p);
                break;
            case "spanish-mexican":
                Sound(p, "ð", "d");
                Sound(p, "θ", "t");
                Sound(p, "v", "b");
                Sound(p, "z", "s");
                Vowel(p, "ɪ", "i");
                Vowel(p, "iː", "i");
                Vowel(p, "æ", "a");
                Vowel(p, "ʌ", "a");
                SClusterProthesis(p);
                RomanceR(p);
                break;
            case "australian":
                // General Australian, per Cox & Evans and the KT Speech
                // accent breakdown: the diphthong chain shift (PRICE
                // starts rounded — roight; FACE opens; MOUTH ends on the
                // central rounded ɵ; GOAT centres onto the fronted
                // GOOSE), LOT rounds (sorry → sɒɹiː), START/PALM and the
                // fricative-only broad BATH all land on a FRONT long a
                // (start → staːʔ, past → paːst — chance stays flat,
                // unlike RP), SQUARE is a long monophthong (there's →
                // ðɛːz), STRUT is central, t flaps between vowels
                // (better → bɛɾə) and glottals word-finally after a
                // vowel, exactly as transcribed on the reference page.
                Vowel(p, "aɪ", "ɒɪ");
                Vowel(p, "eɪ", "æɪ");
                Vowel(p, "aʊ", "æɵ");
                Vowel(p, "oʊ", "əʉ");
                Vowel(p, "uː", "ʉː");
                LotRounding(word, p);
                FricativeBath(p);
                NonRhotic(p);
                Vowel(p, "ɑː", "aː");
                Vowel(p, "ɛə", "ɛː");
                Vowel(p, "ʌ", "ɐ");
                FlapT(p);
                FinalGlottalT(p);
                IngToIn(p);
                break;
            case "russian":
                Sound(p, "w", "v");
                Sound(p, "θ", "s");
                Sound(p, "ð", "z");
                FinalDevoicing(p);
                TrillR(p);
                break;
            case "french":
                DropInitialH(p);
                Sound(p, "θ", "s");
                Sound(p, "ð", "z");
                Sound(p, "dʒ", "ʒ");
                Vowel(p, "ɪ", "i");
                break;
            case "german":
                Sound(p, "w", "v");
                Sound(p, "θ", "s");
                Sound(p, "ð", "z");
                Sound(p, "dʒ", "tʃ");
                Vowel(p, "æ", "ɛ");
                FinalDevoicing(p);
                break;
            case "italian":
                Sound(p, "ð", "d");
                Sound(p, "θ", "t");
                DropInitialH(p);
                Vowel(p, "æ", "a");
                Vowel(p, "ʌ", "a");
                Vowel(p, "ɪ", "i");
                RomanceR(p);
                break;
        }
    }

    // ---- rules ------------------------------------------------------------

    private static void Vowel(List<Phone> p, string from, string to)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i].IsVowel && p[i].Sound == from)
            {
                p[i] = p[i] with { Sound = to };
            }
        }
    }

    private static void Sound(List<Phone> p, string from, string to)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (!p[i].IsVowel && p[i].Sound == from)
            {
                p[i] = p[i] with { Sound = to };
            }
        }
    }

    /// <summary>Deletes r unless a vowel follows, broadening or centring
    /// the vowel before it the way non-rhotic accents do.</summary>
    private static void NonRhotic(List<Phone> p)
    {
        for (var i = p.Count - 1; i >= 0; i--)
        {
            if (p[i].Sound != "ɹ" || (i + 1 < p.Count && p[i + 1].IsVowel))
            {
                continue;
            }
            if (i > 0 && p[i - 1].IsVowel)
            {
                var vowel = p[i - 1];
                if (NonRhoticVowels.TryGetValue(vowel.Sound, out var replacement))
                {
                    p[i - 1] = vowel with { Sound = replacement };
                }
                p.RemoveAt(i);
            }
        }
    }

    private static readonly Dictionary<string, string> NonRhoticVowels = new(StringComparer.Ordinal)
    {
        ["ɜ"] = "ɜː", ["ə"] = "ə", ["ɑː"] = "ɑː", ["ɔː"] = "ɔː", ["ɒ"] = "ɔː",
        ["ɛ"] = "ɛə", ["ɪ"] = "ɪə", ["iː"] = "ɪə", ["ʊ"] = "ʊə", ["uː"] = "ʊə",
        ["aɪ"] = "aɪə", ["aʊ"] = "aʊə", ["eɪ"] = "ɛə", ["oʊ"] = "ɔː",
        ["æ"] = "ɑː", ["ʌ"] = "ɜː",
    };

    /// <summary>RP/Mid-Atlantic BATH: æ broadens to ɑː before a voiceless
    /// fricative that closes the syllable, or before n/m plus s, t or tʃ
    /// (past, ask, after, laugh, chance, can't) — but not before a
    /// fricative that opens the next syllable (classic, massive stay flat),
    /// and never before nd (hand, stand stay flat).</summary>
    private static void BathBroadening(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (!p[i].IsVowel || p[i].Sound != "æ" || i + 1 >= p.Count)
            {
                continue;
            }
            var next = p[i + 1].Sound;
            var afterNext = i + 2 < p.Count ? p[i + 2] : (Phone?)null;
            var broadens =
                (next is "s" or "f" or "θ" && (afterNext is null || !afterNext.Value.IsVowel)) ||
                (next is "n" or "m" && afterNext is not null &&
                 afterNext.Value.Sound is "s" or "t" or "tʃ" or "θ");
            if (broadens)
            {
                p[i] = p[i] with { Sound = "ɑː" };
            }
        }
    }

    /// <summary>RP LOT: the GA ɑː of got/not/stop rounds to ɒ — except in
    /// the PALM words, and not in a START syllable, where r closes the
    /// syllable and the ɑː stays (start, hard).  An r that opens the next
    /// syllable does not protect it (sorry, tomorrow round normally).</summary>
    private static void LotRounding(string word, List<Phone> p)
    {
        if (PalmWords.Contains(word))
        {
            return;
        }
        for (var i = 0; i < p.Count; i++)
        {
            if (!p[i].IsVowel || p[i].Sound != "ɑː")
            {
                continue;
            }
            var startSyllable = i + 1 < p.Count && p[i + 1].Sound == "ɹ" &&
                (i + 2 >= p.Count || !p[i + 2].IsVowel);
            if (!startSyllable)
            {
                p[i] = p[i] with { Sound = "ɒ" };
            }
        }
    }

    private static readonly HashSet<string> PalmWords = new(StringComparer.Ordinal)
    {
        "father", "calm", "palm", "ma", "pa", "mama", "papa", "drama", "spa", "llama",
    };

    /// <summary>The Australian half of BATH: æ broadens before a
    /// syllable-final voiceless fricative (past, ask, laugh) but stays
    /// flat before the nasal clusters RP broadens (chance, dance,
    /// plant).</summary>
    private static void FricativeBath(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (!p[i].IsVowel || p[i].Sound != "æ" || i + 1 >= p.Count)
            {
                continue;
            }
            var next = p[i + 1].Sound;
            var afterNext = i + 2 < p.Count ? p[i + 2] : (Phone?)null;
            if (next is "s" or "f" or "θ" && (afterNext is null || !afterNext.Value.IsVowel))
            {
                p[i] = p[i] with { Sound = "ɑː" };
            }
        }
    }

    /// <summary>American-style t-flapping (shared by Australian English):
    /// a t between a vowel and an unstressed vowel becomes the tap
    /// (better → beɾə, party → pɑːɾiː).</summary>
    private static void FlapT(List<Phone> p)
    {
        for (var i = 1; i < p.Count - 1; i++)
        {
            if (p[i].Sound == "t" && p[i - 1].IsVowel &&
                p[i + 1] is { IsVowel: true, Stress: 0 })
            {
                p[i] = p[i] with { Sound = "ɾ" };
            }
        }
    }

    /// <summary>Cockney th-fronting: θ becomes f anywhere; ð becomes v
    /// except word-initially (this/that keep their th).</summary>
    private static void ThFronting(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i].Sound == "θ")
            {
                p[i] = p[i] with { Sound = "f" };
            }
            else if (p[i].Sound == "ð" && i > 0)
            {
                p[i] = p[i] with { Sound = "v" };
            }
        }
    }

    private static void DropInitialH(List<Phone> p)
    {
        if (p.Count > 1 && p[0].Sound == "h")
        {
            p.RemoveAt(0);
        }
    }

    /// <summary>-ing relaxes to -in when the final syllable is unstressed.</summary>
    private static void IngToIn(List<Phone> p)
    {
        if (p.Count >= 2 && p[^1].Sound == "ŋ" &&
            p[^2] is { Sound: "ɪ" or "i", Stress: 0 })
        {
            p[^1] = p[^1] with { Sound = "n" };
        }
    }

    /// <summary>Southern PRICE smoothing, mild form: aɪ settles to ɑː
    /// word-finally and before voiced sounds (time, mine, I) but stays a
    /// diphthong before voiceless ones (like, right).</summary>
    private static void PriceFlattensBeforeVoiced(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (!p[i].IsVowel || p[i].Sound != "aɪ")
            {
                continue;
            }
            var voiced = i + 1 >= p.Count || p[i + 1].IsVowel ||
                Voiced.Contains(p[i + 1].Sound);
            if (voiced)
            {
                p[i] = p[i] with { Sound = "ɑː" };
            }
        }
    }

    private static readonly HashSet<string> Voiced = new(StringComparer.Ordinal)
    {
        "b", "d", "ɡ", "v", "ð", "z", "ʒ", "dʒ", "m", "n", "ŋ", "l", "ɹ", "w", "j",
    };

    private static void PenPinMerger(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i].IsVowel && p[i].Sound == "ɛ" &&
                i + 1 < p.Count && p[i + 1].Sound is "n" or "m")
            {
                p[i] = p[i] with { Sound = "ɪ" };
            }
        }
    }

    private static void FinalDevoicing(List<Phone> p)
    {
        if (p.Count == 0)
        {
            return;
        }
        if (Devoiced.TryGetValue(p[^1].Sound, out var voiceless))
        {
            p[^1] = p[^1] with { Sound = voiceless };
        }
    }

    private static readonly Dictionary<string, string> Devoiced = new(StringComparer.Ordinal)
    {
        ["b"] = "p", ["d"] = "t", ["ɡ"] = "k", ["v"] = "f",
        ["z"] = "s", ["ʒ"] = "ʃ", ["dʒ"] = "tʃ",
    };

    /// <summary>Spanish never starts a word with s+consonant: school gets
    /// its e (eskul), street its estrit.</summary>
    private static void SClusterProthesis(List<Phone> p)
    {
        if (p.Count > 1 && p[0].Sound == "s" && !p[1].IsVowel)
        {
            p.Insert(0, new Phone("e", 0));
        }
    }

    /// <summary>Standard Scottish English, per Wells and Stuart-Smith's
    /// descriptions: rhotic with tapped r's; FOOT and GOOSE merge on the
    /// famously fronted ʉ and Scots MOUTH joins them (hoose, doon);
    /// LOT, THOUGHT and PALM merge on a short ɔ while START and TRAP/BATH
    /// keep a plain a; FACE and GOAT are pure long monophthongs; FLEECE is
    /// clipped short; PRICE takes the Scottish Vowel Length Rule's short
    /// ʌi before voiceless sounds but stays long finally and before voiced
    /// fricatives (right → ɾʌit, why stays ʍaɪ); and wine and whine are
    /// still distinct — wh carries the voiceless ʍ.</summary>
    private static void ScotsCore(string word, List<Phone> p)
    {
        WhVoiceless(word, p);
        Vowel(p, "aʊ", "ʉː");
        Vowel(p, "uː", "ʉː");
        Vowel(p, "ʊ", "ʉ");
        Vowel(p, "iː", "i");
        Vowel(p, "eɪ", "eː");
        Vowel(p, "oʊ", "oː");
        Vowel(p, "æ", "a");
        LotPalmThought(word, p);
        ScotsPrice(p);
        IngToIn(p);
        TapR(p);
    }

    /// <summary>The Scottish three-way collapse of the open back vowels:
    /// LOT and THOUGHT merge on short ɔ, while PALM words and START
    /// syllables keep a plain a (got → ɡɔt, talk → tɔk, father → faðəɾ,
    /// start → staɾt).</summary>
    private static void LotPalmThought(string word, List<Phone> p)
    {
        var palm = PalmWords.Contains(word);
        for (var i = 0; i < p.Count; i++)
        {
            if (!p[i].IsVowel)
            {
                continue;
            }
            if (p[i].Sound == "ɑː")
            {
                var beforeR = i + 1 < p.Count && p[i + 1].Sound == "ɹ";
                p[i] = p[i] with { Sound = palm || beforeR ? "a" : "ɔ" };
            }
            else if (p[i].Sound == "ɔː")
            {
                p[i] = p[i] with { Sound = "ɔ" };
            }
        }
    }

    /// <summary>Aitken's law for PRICE: short ʌi before a voiceless or
    /// plain-stop consonant, long aɪ word-finally and before voiced
    /// fricatives or r.</summary>
    private static void ScotsPrice(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i].Sound == "aɪ" && i + 1 < p.Count && !p[i + 1].IsVowel &&
                p[i + 1].Sound is not ("v" or "ð" or "z" or "ʒ" or "ɹ" or "ɾ" or "r"))
            {
                p[i] = p[i] with { Sound = "ʌi" };
            }
        }
    }

    /// <summary>Scots keeps wine and whine distinct: a spelled wh- onset
    /// is the voiceless ʍ ("who" starts with h and is untouched).</summary>
    private static void WhVoiceless(string word, List<Phone> p)
    {
        if (word.StartsWith("wh", StringComparison.Ordinal) &&
            p.Count > 0 && p[0].Sound == "w")
        {
            p[0] = p[0] with { Sound = "ʍ" };
        }
    }

    /// <summary>The Rick Grimes half-rhoticity: r drops from a coda only
    /// after an unstressed vowel (brother → brʌðə, walkers → wɔːkəz);
    /// stressed syllables keep their r (hard, right, run).</summary>
    private static void UnstressedDerhoting(List<Phone> p)
    {
        for (var i = p.Count - 1; i >= 0; i--)
        {
            if (p[i].Sound == "ɹ" && (i + 1 >= p.Count || !p[i + 1].IsVowel) &&
                i > 0 && p[i - 1] is { IsVowel: true, Stress: 0 })
            {
                p.RemoveAt(i);
            }
        }
    }

    /// <summary>Rick Grimes' full-mouth articulation: every already-long
    /// vowel (FLEECE, GOOSE, START, THOUGHT, NURSE…) gets an extra length
    /// mark, sustaining it as a pure held sound instead of letting it
    /// glide toward the next one (sweet → swiːːt).  Diphthongs are left
    /// alone — a glide is what they are — this only extends vowels that
    /// were already a single sustained sound.</summary>
    private static void HoldLongVowels(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i].IsVowel && p[i].Sound.EndsWith('ː'))
            {
                p[i] = p[i] with { Sound = p[i].Sound + "ː" };
            }
        }
    }

    /// <summary>Australian final-plosive glottaling: a word-final t after
    /// a vowel is a glottal catch (start → staːʔ, right → ɹɒɪʔ), while
    /// intervocalic t stays a flap.</summary>
    private static void FinalGlottalT(List<Phone> p)
    {
        if (p.Count >= 2 && p[^1].Sound == "t" && p[^2].IsVowel)
        {
            p[^1] = p[^1] with { Sound = "ʔ" };
        }
    }

    /// <summary>Glaswegian t-glottaling: a t after a vowel becomes the
    /// glottal catch word-finally and between vowels (water → wɔʔəɾ,
    /// get → ɡɛʔ).</summary>
    private static void GlottalT(List<Phone> p)
    {
        for (var i = 1; i < p.Count; i++)
        {
            if (p[i].Sound == "t" && p[i - 1].IsVowel &&
                (i == p.Count - 1 || p[i + 1].IsVowel))
            {
                p[i] = p[i] with { Sound = "ʔ" };
            }
        }
    }

    // The three r's: ɹ is the English approximant (the default every
    // derived word gets), ɾ the alveolar tap, r the full trill.  Verified
    // live: Inworld renders ɹ identically to a plain word (envelope
    // correlation 0.95) while ɾ and r audibly change the articulation.

    /// <summary>Scottish/Welsh: every surviving r is tapped.</summary>
    private static void TapR(List<Phone> p) => Sound(p, "ɹ", "ɾ");

    /// <summary>Russian: every r is a full trill.</summary>
    private static void TrillR(List<Phone> p) => Sound(p, "ɹ", "r");

    /// <summary>Spanish/Italian distribution: a word-initial r is trilled,
    /// every other r is a tap.</summary>
    private static void RomanceR(List<Phone> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            if (p[i].Sound == "ɹ")
            {
                p[i] = p[i] with { Sound = i == 0 ? "r" : "ɾ" };
            }
        }
    }

    // ---- rendering --------------------------------------------------------

    /// <summary>Phones to an IPA string, with a stress mark placed at the
    /// onset of the stressed syllable (one preceding consonant, two for
    /// s-clusters and consonant+liquid onsets).  Monosyllables carry no
    /// mark.</summary>
    internal static string Render(List<Phone> phones)
    {
        var marks = new Dictionary<int, char>();
        if (phones.Count(phone => phone.IsVowel) > 1)
        {
            for (var i = 0; i < phones.Count; i++)
            {
                if (phones[i].Stress is not (1 or 2))
                {
                    continue;
                }
                var start = i;
                var taken = 0;
                while (start > 0 && !phones[start - 1].IsVowel && taken < 2)
                {
                    var candidate = phones[start - 1].Sound;
                    if (candidate == "ŋ")
                    {
                        break;
                    }
                    if (taken == 1 &&
                        candidate != "s" &&
                        phones[start].Sound is not ("ɹ" or "ɾ" or "r" or "l" or "w" or "j"))
                    {
                        break;
                    }
                    start--;
                    taken++;
                }
                marks[start] = phones[i].Stress == 1 ? 'ˈ' : 'ˌ';
            }
        }
        var builder = new StringBuilder();
        for (var i = 0; i < phones.Count; i++)
        {
            if (marks.TryGetValue(i, out var mark))
            {
                builder.Append(mark);
            }
            builder.Append(phones[i].Sound);
        }
        return builder.ToString();
    }

    // ---- General American base pronunciations -----------------------------

    /// <summary>The GA phones for a word (lowercase), or null if the CMU
    /// dictionary does not know it.</summary>
    internal static List<Phone>? Pronounce(string word)
    {
        if (!Dictionary.Value.TryGetValue(word, out var arpabet))
        {
            return null;
        }
        // This CMUdict release is cot-caught inconsistent: caught and
        // bought are filed under the merged AA while thought and talk
        // keep AO.  The THOUGHT words matter to several accents (RP ɔː,
        // New York ɔə, Australian oː), so refile them.
        if (ThoughtMisfiled.Contains(word))
        {
            arpabet = arpabet.Replace("AA", "AO");
        }
        var phones = new List<Phone>();
        foreach (var token in arpabet.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var stress = -1;
            var symbol = token;
            if (char.IsDigit(token[^1]))
            {
                stress = token[^1] - '0';
                symbol = token[..^1];
            }
            // ER is r-coloured in GA; splitting it into vowel + r lets the
            // non-rhotic rule treat it like every other vowel-r sequence.
            if (symbol == "ER")
            {
                phones.Add(new Phone(stress is 1 or 2 ? "ɜ" : "ə", stress < 0 ? 0 : stress));
                phones.Add(new Phone("ɹ", -1));
                continue;
            }
            // AH is the one stress-dependent vowel: unstressed it is schwa,
            // stressed it is STRUT.
            if (symbol == "AH")
            {
                phones.Add(new Phone(stress == 0 ? "ə" : "ʌ", stress < 0 ? 0 : stress));
                continue;
            }
            phones.Add(new Phone(
                Arpabet.TryGetValue(symbol, out var ipa) ? ipa : symbol.ToLowerInvariant(),
                stress));
        }
        return phones;
    }

    private static readonly Dictionary<string, string> Arpabet = new(StringComparer.Ordinal)
    {
        ["AA"] = "ɑː", ["AE"] = "æ", ["AO"] = "ɔː", ["AW"] = "aʊ", ["AY"] = "aɪ",
        ["EH"] = "ɛ", ["EY"] = "eɪ", ["IH"] = "ɪ", ["IY"] = "iː", ["OW"] = "oʊ",
        ["OY"] = "ɔɪ", ["UH"] = "ʊ", ["UW"] = "uː",
        ["B"] = "b", ["CH"] = "tʃ", ["D"] = "d", ["DH"] = "ð", ["F"] = "f",
        ["G"] = "ɡ", ["HH"] = "h", ["JH"] = "dʒ", ["K"] = "k", ["L"] = "l",
        ["M"] = "m", ["N"] = "n", ["NG"] = "ŋ", ["P"] = "p", ["R"] = "ɹ",
        ["S"] = "s", ["SH"] = "ʃ", ["T"] = "t", ["TH"] = "θ", ["V"] = "v",
        ["W"] = "w", ["Y"] = "j", ["Z"] = "z", ["ZH"] = "ʒ",
    };

    private static readonly HashSet<string> ThoughtMisfiled = new(StringComparer.Ordinal)
    {
        "caught", "taught", "fought", "bought", "brought", "sought", "wrought",
        "naught", "naughty", "slaughter", "daughter", "daughters",
    };

    private static readonly Lazy<Dictionary<string, string>> Dictionary = new(Load);

    private static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>(140_000, StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CustomVoicedDialogue.Server.Resources.cmudict.dict.gz")
            ?? throw new InvalidOperationException("cmudict resource missing");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment];
            }
            var split = line.IndexOf(' ');
            if (split <= 0)
            {
                continue;
            }
            var word = line[..split];
            // Only the first pronunciation of a word; alternates are named
            // word(2), word(3)…  Words with digits or punctuation beyond
            // apostrophes are not dialogue tokens.
            if (word.Any(c => c is not ('\'' or >= 'a' and <= 'z')))
            {
                continue;
            }
            table.TryAdd(word, line[(split + 1)..].Trim());
        }
        return table;
    }

    // Function words whose citation-form IPA would fight natural sentence
    // rhythm (they are unstressed and reduced in real speech).  The hand
    // lexicon overrides still cover the ones an accent genuinely wants
    // (RP was/what, German the, NY that…).
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "and", "or", "but", "if", "as", "at",
        "in", "on", "for", "by", "it", "its", "it's", "is", "am", "are",
        "was", "were", "be", "been", "being", "do", "does", "did", "done",
        "have", "has", "had", "will", "would", "can", "could", "shall",
        "should", "may", "might", "must", "i", "you", "he", "she", "we",
        "they", "me", "him", "her", "us", "them", "my", "your", "his",
        "their", "our", "this", "that", "these", "those", "there", "than",
        "then", "so", "not", "no", "yes", "what", "who", "whom", "which",
        "with", "from", "into", "onto", "up", "out", "off",
    };

    // Spellings with two common pronunciations we cannot tell apart
    // without knowing the part of speech.
    private static readonly HashSet<string> Heteronyms = new(StringComparer.Ordinal)
    {
        "read", "lead", "live", "lives", "wind", "tear", "bow", "row", "sow",
        "close", "use", "wound", "dove", "bass", "minute", "record",
        "present", "object", "produce", "refuse", "conduct", "subject",
        "content", "desert", "contract", "excuse", "house",
    };
}
