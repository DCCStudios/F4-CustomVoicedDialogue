using System.Text.RegularExpressions;

namespace CustomVoicedDialogue.Server.VoiceMapping;

/// <summary>
/// Hand-written pronunciation lexicons: for each accent, the words whose
/// pronunciation actually carries it, mapped to accent-specific IPA that
/// Inworld TTS-2 reads inline (one word per /slash/ pair, per its custom
/// pronunciation support).  Substitution happens in code, deterministically,
/// after emotion tagging — the tagging model is never asked to respell
/// anything, because verified live tests showed models transcribe accents
/// into near-identical or unreadable spellings while the synthesizer reads
/// curated IPA perfectly.
/// </summary>
public static class AccentLexicon
{
    /// <summary>Whether an accent has pronunciation entries.  Accents whose
    /// identity is melody rather than vowels (Welsh, Mid-Atlantic) still have
    /// small lexicons; the steering instruction carries the rest.</summary>
    public static bool Has(Accent accent) =>
        !accent.IsNeutral && Lexicons.ContainsKey(accent.Id);

    /// <summary>Replaces every lexicon word in the spoken text with its
    /// accent IPA, wrapped in forward slashes.  Bracketed steering tags are
    /// left untouched.  On a line where the accent "slips" (see
    /// <see cref="Accents.LineSlips"/>) most substitutions are skipped, so
    /// the performance eases toward standard speech the way a real
    /// speaker's does.  Deterministic for a given line, which the audio
    /// cache depends on.</summary>
    public static string Apply(Accent accent, string text, string lineKey, int imperfection)
    {
        if (!Has(accent) || string.IsNullOrEmpty(text))
        {
            return text;
        }
        var lexicon = Lexicons[accent.Id];
        var slipping = Accents.LineSlips(lineKey, imperfection);
        var index = 0;
        return Token.Replace(text, match =>
        {
            if (match.Value[0] == '[')
            {
                return match.Value;
            }
            // The hand-written entry wins (it carries the irregulars);
            // any other word is derived from its dictionary pronunciation
            // through the accent's phonological rules, staying plain when
            // the rules change nothing.
            var word = match.Value.ToLowerInvariant();
            if (!lexicon.TryGetValue(word, out string? ipa))
            {
                ipa = AccentPhonology.Derive(accent, word);
            }
            if (ipa is null)
            {
                return match.Value;
            }
            var wordIndex = index++;
            // A slipped line keeps roughly a quarter of its substitutions —
            // the accent recedes but does not vanish mid-conversation.
            if (slipping && VoiceMapper.Fnv1a($"slipword {lineKey} {wordIndex}") % 4 != 0)
            {
                return match.Value;
            }
            return "/" + ipa + "/";
        });
    }

    /// <summary>The raw entries for an accent, for validation in tests.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> Entries(Accent accent) =>
        Lexicons.TryGetValue(accent.Id, out var lexicon)
            ? lexicon
            : Enumerable.Empty<KeyValuePair<string, string>>();

    // Steering tags pass through whole; words are letters and apostrophes,
    // so contractions ("don't") match as one token and possessives
    // ("world's") simply miss the lexicon and stay untouched.
    private static readonly Regex Token = new(@"\[[^\]]*\]|[A-Za-z']+", RegexOptions.Compiled);

    private static Dictionary<string, string> Merge(
        Dictionary<string, string> baseLexicon, Dictionary<string, string> overlay)
    {
        var merged = new Dictionary<string, string>(baseLexicon, StringComparer.Ordinal);
        foreach (var (word, ipa) in overlay)
        {
            merged[word] = ipa;
        }
        return merged;
    }

    // Words every non-rhotic accent transforms the same way: the r after a
    // vowel is gone and the vowel carries the length.  Individual accents
    // overlay their own vowel shifts on top.
    private static readonly Dictionary<string, string> NonRhotic = new(StringComparer.Ordinal)
    {
        ["car"] = "kɑː", ["far"] = "fɑː", ["are"] = "ɑː",
        ["hard"] = "hɑːd", ["start"] = "stɑːt", ["part"] = "pɑːt",
        ["heart"] = "hɑːt", ["park"] = "pɑːk",
        ["here"] = "hɪə", ["there"] = "ðɛə", ["where"] = "wɛə", ["near"] = "nɪə",
        ["never"] = "ˈnɛvə", ["better"] = "ˈbɛtə", ["water"] = "ˈwɔːtə",
        ["after"] = "ˈæftə", ["other"] = "ˈʌðə", ["brother"] = "ˈbrʌðə",
        ["together"] = "təˈɡɛðə", ["remember"] = "rɪˈmɛmbə",
        ["more"] = "mɔː", ["door"] = "dɔː", ["four"] = "fɔː",
        ["sure"] = "ʃʊə", ["your"] = "jɔː", ["years"] = "jɪəz",
        ["first"] = "fɜːst", ["work"] = "wɜːk", ["word"] = "wɜːd",
        ["world"] = "wɜːld", ["girl"] = "ɡɜːl",
        ["worn"] = "wɔːn", ["born"] = "bɔːn", ["corner"] = "ˈkɔːnə",
    };

    // The broad-BATH set shared by RP and Mid-Atlantic.
    private static readonly Dictionary<string, string> BroadBath = new(StringComparer.Ordinal)
    {
        ["bath"] = "bɑːθ", ["can't"] = "kɑːnt", ["past"] = "pɑːst",
        ["last"] = "lɑːst", ["ask"] = "ɑːsk", ["chance"] = "tʃɑːns",
        ["dance"] = "dɑːns", ["half"] = "hɑːf", ["laugh"] = "lɑːf",
        ["rather"] = "ˈrɑːðə", ["answer"] = "ˈɑːnsə", ["after"] = "ˈɑːftə",
    };

    // A mild, rhotic Southern drawl: PRICE settles to a held ah before
    // voiced sounds, pen and pin merge, -ing relaxes to -in.
    private static readonly Dictionary<string, string> Southern = new(StringComparer.Ordinal)
    {
        ["i"] = "ɑː", ["i'm"] = "ɑːm", ["i'll"] = "ɑːl", ["i've"] = "ɑːv", ["i'd"] = "ɑːd",
        ["my"] = "mɑː", ["mine"] = "mɑːn",
        ["time"] = "tɑːm", ["fine"] = "fɑːn", ["find"] = "fɑːnd",
        ["mind"] = "mɑːnd", ["side"] = "sɑːd", ["tired"] = "ˈtɑːrd",
        ["pen"] = "pɪn", ["ten"] = "tɪn", ["again"] = "əˈɡɪn",
        ["going"] = "ˈɡoʊɪn", ["doing"] = "ˈduːɪn", ["getting"] = "ˈɡɪtɪn",
        ["nothing"] = "ˈnʌθɪn", ["something"] = "ˈsʌmθɪn", ["anything"] = "ˈɛniθɪn",
        ["looking"] = "ˈlʊkɪn", ["talking"] = "ˈtɔːkɪn", ["coming"] = "ˈkʌmɪn",
        ["waiting"] = "ˈweɪtɪn", ["trying"] = "ˈtrɑːɪn", ["morning"] = "ˈmɔːrnɪn",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Lexicons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["southern"] = Southern,

        // Everything Southern does, further: PRICE flattens everywhere
        // (even before voiceless sounds), MOUTH fronts, get becomes git.
        ["deep-south"] = Merge(Southern, new(StringComparer.Ordinal)
        {
            ["why"] = "wɑː", ["by"] = "bɑː", ["try"] = "trɑː",
            ["right"] = "rɑːt", ["night"] = "nɑːt", ["like"] = "lɑːk", ["life"] = "lɑːf",
            ["can't"] = "keɪnt", ["get"] = "ɡɪt", ["when"] = "wɪn", ["friend"] = "frɪnd",
            ["down"] = "dæʊn", ["now"] = "næʊ", ["out"] = "æʊt", ["how"] = "hæʊ",
            ["about"] = "əˈbæʊt", ["house"] = "hæʊs", ["town"] = "tæʊn",
            ["around"] = "əˈræʊnd", ["south"] = "sæʊθ", ["oil"] = "ɔːl",
        }),

        ["boston"] = Merge(NonRhotic, new(StringComparer.Ordinal)
        {
            ["over"] = "ˈoʊvə",
        }),

        ["new-york"] = Merge(NonRhotic, new(StringComparer.Ordinal)
        {
            ["over"] = "ˈoʊvə",
            ["talk"] = "tɔək", ["walk"] = "wɔək", ["caught"] = "kɔət",
            ["bought"] = "bɔət", ["coffee"] = "ˈkɔəfi", ["dog"] = "dɔəɡ",
            ["off"] = "ɔəf", ["lost"] = "lɔəst", ["call"] = "kɔəl",
            ["water"] = "ˈwɔətə", ["daughter"] = "ˈdɔətə",
            ["this"] = "dɪs", ["that"] = "dæt", ["these"] = "diːz", ["them"] = "dɛm",
            ["those"] = "doʊz",
            ["nothing"] = "ˈnʌtɪn", ["something"] = "ˈsʌmtɪn",
        }),

        ["mid-atlantic"] = Merge(Merge(NonRhotic, BroadBath), new(StringComparer.Ordinal)
        {
            ["again"] = "əˈɡeɪn", ["been"] = "biːn",
            ["either"] = "ˈaɪðə", ["neither"] = "ˈnaɪðə",
            ["new"] = "njuː", ["news"] = "njuːz", ["nuclear"] = "ˈnjuːklɪə",
            ["duty"] = "ˈdjuːtiː", ["lieutenant"] = "lɛfˈtɛnənt",
        }),

        ["british-rp"] = Merge(Merge(NonRhotic, BroadBath), new(StringComparer.Ordinal)
        {
            // Words whose British form the American dictionary entry hides.
            ["again"] = "əˈɡeɪn", ["been"] = "biːn", ["ate"] = "ɛt",
            ["either"] = "ˈaɪðə", ["neither"] = "ˈnaɪðə",
            ["schedule"] = "ˈʃɛdjuːl", ["garage"] = "ˈɡærɪdʒ",
            ["leisure"] = "ˈlɛʒə", ["privacy"] = "ˈprɪvəsiː",
            ["lieutenant"] = "lɛfˈtɛnənt",
            // Yod retention: the j GA dropped after t, d, n, s.
            ["new"] = "njuː", ["news"] = "njuːz", ["knew"] = "njuː",
            ["due"] = "djuː", ["duty"] = "ˈdjuːtiː", ["tune"] = "tjuːn",
            ["student"] = "ˈstjuːdənt", ["stupid"] = "ˈstjuːpɪd",
            ["nuclear"] = "ˈnjuːklɪə",
            ["got"] = "ɡɒt", ["not"] = "nɒt", ["what"] = "wɒt", ["want"] = "wɒnt",
            ["stop"] = "stɒp", ["lot"] = "lɒt", ["job"] = "dʒɒb", ["god"] = "ɡɒd",
            ["gone"] = "ɡɒn", ["off"] = "ɒf", ["was"] = "wɒz", ["wasn't"] = "ˈwɒznt",
            ["go"] = "ɡəʊ", ["no"] = "nəʊ", ["know"] = "nəʊ", ["so"] = "səʊ",
            ["don't"] = "dəʊnt", ["won't"] = "wəʊnt", ["home"] = "həʊm",
            ["old"] = "əʊld", ["road"] = "rəʊd", ["over"] = "ˈəʊvə",
            ["only"] = "ˈəʊnli", ["suppose"] = "səˈpəʊz",
            ["hold"] = "həʊld", ["told"] = "təʊld",
        }),

        ["british-cockney"] = Merge(NonRhotic, new(StringComparer.Ordinal)
        {
            // MOUTH goes long and flat; house also drops its h.
            ["down"] = "daːn", ["out"] = "aːt", ["about"] = "əˈbaːt",
            ["now"] = "naː", ["town"] = "taːn", ["around"] = "əˈraːnd",
            ["how"] = "haː", ["house"] = "aːs", ["mouth"] = "maːf",
            // FACE opens toward PRICE.  "again" rides its əˈɡeɪn variant
            // (the dictionary's first entry is əˈɡɛn, which no rule moves).
            ["again"] = "əˈɡaɪn",
            ["day"] = "daɪ", ["way"] = "waɪ", ["away"] = "əˈwaɪ", ["say"] = "saɪ",
            ["take"] = "taɪk", ["make"] = "maɪk", ["name"] = "naɪm",
            ["place"] = "plaɪs", ["face"] = "faɪs", ["late"] = "laɪt",
            ["wait"] = "waɪt", ["mate"] = "maɪt", ["same"] = "saɪm",
            ["safe"] = "saɪf", ["pay"] = "paɪ",
            // PRICE rounds and widens.
            ["time"] = "tɒɪm", ["like"] = "lɒɪk", ["right"] = "rɒɪt",
            ["night"] = "nɒɪt", ["find"] = "fɒɪnd", ["mind"] = "mɒɪnd", ["my"] = "mɒɪ",
            // Th-fronting.
            ["think"] = "fɪŋk", ["thing"] = "fɪŋ", ["things"] = "fɪŋz",
            ["something"] = "ˈsʌmfɪŋk", ["nothing"] = "ˈnʌfɪŋk", ["anything"] = "ˈɛnifɪŋk",
            ["three"] = "friː", ["through"] = "fruː", ["with"] = "wɪv",
            ["brother"] = "ˈbrʌvə", ["other"] = "ˈʌvə", ["together"] = "təˈɡɛvə",
            // H-dropping.
            ["have"] = "æv", ["here"] = "ɪə", ["home"] = "əʊm",
            ["hold"] = "əʊld", ["help"] = "ɛlp", ["him"] = "ɪm",
            // British EYE-ther with the fronted th, and yod-coalescence
            // (tune → choon) where RP keeps a clean tj.
            ["either"] = "ˈaɪvə", ["neither"] = "ˈnaɪvə",
            ["new"] = "njuː", ["news"] = "njuːz", ["nuclear"] = "ˈnjuːklɪə",
            ["tune"] = "tʃuːn", ["due"] = "dʒuː", ["duty"] = "ˈdʒuːtiː",
        }),

        ["british-north"] = Merge(NonRhotic, new(StringComparer.Ordinal)
        {
            // STRUT sits where FOOT is.
            ["love"] = "lʊv", ["up"] = "ʊp", ["come"] = "kʊm", ["some"] = "sʊm",
            ["blood"] = "blʊd", ["enough"] = "ɪˈnʊf", ["luck"] = "lʊk",
            ["just"] = "dʒʊst", ["much"] = "mʊtʃ", ["must"] = "mʊst",
            ["done"] = "dʊn", ["one"] = "wʊn", ["once"] = "wʊns",
            ["money"] = "ˈmʊni", ["trouble"] = "ˈtrʊbəl", ["us"] = "ʊz",
            ["nothing"] = "ˈnʊθɪn", ["something"] = "ˈsʊmθɪn",
            // FACE and GOAT stay pure single vowels.
            ["day"] = "deː", ["take"] = "teːk", ["make"] = "meːk",
            ["face"] = "feːs", ["place"] = "pleːs",
            ["go"] = "ɡoː", ["no"] = "noː", ["know"] = "noː", ["home"] = "hoːm",
            ["road"] = "roːd", ["don't"] = "doːnt", ["old"] = "oːld",
            ["been"] = "biːn", ["new"] = "njuː", ["news"] = "njuːz",
        }),

        // Rhotic: every r stays, so no shared non-rhotic base.
        ["scottish"] = new(StringComparer.Ordinal)
        {
            ["house"] = "huːs", ["out"] = "uːt", ["about"] = "əˈbuːt",
            ["down"] = "duːn", ["now"] = "nuː", ["town"] = "tuːn",
            ["around"] = "əˈruːnd", ["how"] = "huː",
            ["day"] = "deː", ["take"] = "teːk", ["make"] = "meːk", ["say"] = "seː",
            ["stay"] = "steː", ["name"] = "neːm", ["place"] = "pleːs",
            ["face"] = "feːs", ["away"] = "əˈweː", ["again"] = "əˈɡeːn",
            ["go"] = "ɡoː", ["no"] = "noː", ["know"] = "noː", ["home"] = "hoːm",
            ["don't"] = "doːnt", ["road"] = "roːd", ["old"] = "oːld", ["told"] = "toːld",
            ["going"] = "ˈɡoːɪn", ["nothing"] = "ˈnʌθɪn", ["something"] = "ˈsʌmθɪn",
            ["morning"] = "ˈmɔrnɪn",
            ["been"] = "biːn", ["was"] = "wɪz",
            ["new"] = "njuː", ["news"] = "njuːz",
        },

        // Welsh is carried mostly by melody (the steering instruction);
        // the lexicon purifies the FACE and GOAT vowels, and Welsh English
        // is non-rhotic, so it shares that word set too.
        ["welsh"] = Merge(NonRhotic, new(StringComparer.Ordinal)
        {
            ["day"] = "deː", ["face"] = "feːs", ["place"] = "pleːs",
            ["name"] = "neːm", ["take"] = "teːk", ["make"] = "meːk",
            ["go"] = "ɡoː", ["no"] = "noː", ["know"] = "noː",
            ["home"] = "hoːm", ["road"] = "roːd",
            ["been"] = "biːn",
        }),

        ["irish"] = new(StringComparer.Ordinal)
        {
            ["think"] = "tɪŋk", ["thing"] = "tɪŋ", ["things"] = "tɪŋz",
            ["three"] = "triː", ["through"] = "truː",
            ["nothing"] = "ˈnʌtɪn", ["something"] = "ˈsʌmtɪn", ["anything"] = "ˈɛnitɪn",
            ["this"] = "dɪs", ["that"] = "dæt", ["these"] = "diːz", ["those"] = "doʊz",
            ["them"] = "dɛm", ["then"] = "dɛn", ["there"] = "dɛr", ["with"] = "wɪt",
            ["time"] = "tɒɪm", ["like"] = "lɒɪk", ["right"] = "rɒɪt",
            ["night"] = "nɒɪt", ["fine"] = "fɒɪn", ["mind"] = "mɒɪnd",
        },

        ["spanish-mexican"] = new(StringComparer.Ordinal)
        {
            ["think"] = "tiŋk", ["thing"] = "tiŋ", ["things"] = "tiŋs",
            ["nothing"] = "ˈnotiŋ", ["something"] = "ˈsomtiŋ",
            ["this"] = "dis", ["that"] = "dat", ["three"] = "tri",
            ["the"] = "də", ["them"] = "dɛm", ["is"] = "is",
            ["very"] = "ˈbɛri", ["because"] = "biˈkos", ["people"] = "ˈpipol",
            ["school"] = "esˈkul", ["street"] = "esˈtrit", ["stop"] = "esˈtop",
            ["speak"] = "esˈpik", ["start"] = "esˈtart", ["going"] = "ˈɡoin",
        },

        ["australian"] = Merge(NonRhotic, new(StringComparer.Ordinal)
        {
            ["again"] = "əˈɡæɪn", ["been"] = "biːn", ["my"] = "mɑɪ",
            ["new"] = "njuː", ["news"] = "njuːz", ["nuclear"] = "ˈnjuːklɪə",
            ["tune"] = "tʃuːn", ["due"] = "dʒuː",
            ["day"] = "dæɪ", ["way"] = "wæɪ", ["say"] = "sæɪ", ["take"] = "tæɪk",
            ["make"] = "mæɪk", ["name"] = "næɪm", ["place"] = "plæɪs",
            ["late"] = "læɪt", ["mate"] = "mæɪt", ["wait"] = "wæɪt",
            ["time"] = "tɑɪm", ["like"] = "lɑɪk", ["right"] = "rɑɪt", ["night"] = "nɑɪt",
            ["down"] = "dæʊn", ["now"] = "næʊ", ["out"] = "æʊt",
            ["about"] = "əˈbæʊt", ["town"] = "tæʊn", ["how"] = "hæʊ", ["house"] = "hæʊs",
        }),

        ["russian"] = new(StringComparer.Ordinal)
        {
            ["this"] = "zɪs", ["that"] = "zæt", ["these"] = "ziːz", ["them"] = "zɛm",
            ["think"] = "sɪŋk", ["thing"] = "sɪŋ",
            ["nothing"] = "ˈnʌsɪŋ", ["something"] = "ˈsʌmsɪŋ",
            ["what"] = "vʌt", ["want"] = "vʌnt", ["was"] = "vʌs", ["will"] = "vɪl",
            ["with"] = "vɪs", ["where"] = "vɛr", ["why"] = "vaɪ", ["work"] = "vɔrk",
            ["we"] = "viː", ["were"] = "vɜr", ["would"] = "vʊt",
            ["have"] = "hæf", ["good"] = "ɡʊt", ["need"] = "niːt", ["friend"] = "frɛnt",
        },

        ["french"] = new(StringComparer.Ordinal)
        {
            ["the"] = "zə", ["this"] = "zis", ["that"] = "zæt", ["these"] = "ziz",
            ["them"] = "zɛm",
            ["think"] = "siŋk", ["thing"] = "siŋ",
            ["something"] = "ˈsomsiŋ", ["nothing"] = "ˈnɔsiŋ", ["three"] = "sriː",
            ["have"] = "æv", ["house"] = "aʊs", ["home"] = "oʊm",
            ["happy"] = "ˈæpi", ["is"] = "iːz", ["it"] = "iːt", ["his"] = "iːz",
        },

        ["german"] = new(StringComparer.Ordinal)
        {
            ["the"] = "zə", ["this"] = "zɪs", ["that"] = "zæt", ["these"] = "ziːz",
            ["them"] = "zɛm", ["those"] = "zoʊz", ["we"] = "viː",
            ["think"] = "sɪŋk", ["thing"] = "sɪŋ",
            ["something"] = "ˈsʌmsɪŋ", ["nothing"] = "ˈnʌsɪŋ",
            ["want"] = "vɒnt", ["was"] = "vɒs", ["what"] = "vɒt", ["will"] = "vɪl",
            ["would"] = "vʊt", ["with"] = "vɪs", ["very"] = "ˈfɛri",
            ["have"] = "hɛf", ["good"] = "ɡuːt", ["friend"] = "frɛnt",
        },

        ["italian"] = new(StringComparer.Ordinal)
        {
            ["think"] = "tiŋk", ["thing"] = "tiŋ", ["this"] = "dis", ["that"] = "dat",
            ["the"] = "də", ["them"] = "dɛm", ["is"] = "iːz",
            ["three"] = "tri", ["nothing"] = "ˈnotiŋ", ["something"] = "ˈsomtiŋ",
            ["have"] = "av", ["house"] = "ˈaus",
        },
    };
}
