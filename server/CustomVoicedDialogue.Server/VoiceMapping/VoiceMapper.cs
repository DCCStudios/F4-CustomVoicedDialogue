using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Providers;

namespace CustomVoicedDialogue.Server.VoiceMapping;

/// <summary>
/// Chooses which provider voice speaks a line, with zero per-NPC setup:
///  - player lines use the configured player voice;
///  - NPC lines use the per-voice-type override when the user set one;
///  - otherwise, when the provider offers native Fallout 4 models
///    (xVASynth "f4_*"), a case-normalized match on the voice type is used;
///  - otherwise a deterministic hash of the voice type picks from the
///    provider's voice list, filtered by a gender heuristic on the editor
///    ID ("...Female..." / "...Male...") when the voices carry gender tags.
/// The hash is stable across sessions, so every NPC keeps their voice.
/// </summary>
public sealed class VoiceMapper
{
    private readonly AppConfig _config;

    public VoiceMapper(AppConfig config)
    {
        _config = config;
    }

    public string ResolveVoice(bool isPlayer, string voiceType, IReadOnlyList<TtsVoice> availableVoices)
    {
        if (isPlayer)
        {
            // Player lines never fall through to NPC overrides or the hash
            // pick — an empty PlayerVoice means the provider's configured
            // default voice (resolved by the caller).
            return _config.PlayerVoice;
        }

        if (!string.IsNullOrEmpty(voiceType) &&
            _config.NpcVoiceOverrides.TryGetValue(voiceType, out var overrideVoice) &&
            !string.IsNullOrEmpty(overrideVoice))
        {
            return overrideVoice;
        }

        if (availableVoices.Count == 0)
        {
            // Nothing to pick from — let the provider fall back to its own
            // configured default voice option.
            return _config.PlayerVoice;
        }

        // Native Fallout 4 voice models (xVASynth ships them as f4_<voicetype>).
        if (!string.IsNullOrEmpty(voiceType))
        {
            var native = availableVoices.FirstOrDefault(v =>
                v.Id.Equals($"f4_{voiceType}", StringComparison.OrdinalIgnoreCase) ||
                v.Id.Equals(voiceType, StringComparison.OrdinalIgnoreCase));
            if (native is not null)
            {
                return native.Id;
            }
        }

        // Gender heuristic + stable hash.
        var pool = FilterByGender(availableVoices, GuessGender(voiceType));
        var index = (int)(Fnv1a(voiceType ?? "") % (uint)pool.Count);
        return pool[index].Id;
    }

    internal static VoiceGender GuessGender(string? voiceType)
    {
        if (string.IsNullOrEmpty(voiceType))
        {
            return VoiceGender.Unknown;
        }
        // "Female" must be probed first — it contains "male".
        if (voiceType.Contains("female", StringComparison.OrdinalIgnoreCase))
        {
            return VoiceGender.Female;
        }
        if (voiceType.Contains("male", StringComparison.OrdinalIgnoreCase))
        {
            return VoiceGender.Male;
        }
        return VoiceGender.Unknown;
    }

    private static IReadOnlyList<TtsVoice> FilterByGender(IReadOnlyList<TtsVoice> voices, VoiceGender gender)
    {
        if (gender == VoiceGender.Unknown)
        {
            return voices;
        }
        var filtered = voices.Where(v => v.Gender == gender).ToList();
        return filtered.Count > 0 ? filtered : voices;
    }

    internal static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= char.ToLowerInvariant(character);
            hash *= 16777619u;
        }
        return hash;
    }
}
