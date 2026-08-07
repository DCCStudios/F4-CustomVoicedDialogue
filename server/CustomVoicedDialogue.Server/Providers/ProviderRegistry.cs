namespace CustomVoicedDialogue.Server.Providers;

/// <summary>
/// All available TTS providers, keyed by id.  A single HttpClient (and
/// therefore a single injectable HttpMessageHandler, for tests) backs
/// every provider.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ITtsProvider> _providers;

    public ProviderRegistry(HttpClient httpClient)
    {
        var all = new ITtsProvider[]
        {
            new ElevenLabsProvider(httpClient),
            new OpenAiProvider(httpClient),
            new AzureProvider(httpClient),
            new XvaSynthProvider(httpClient),
            new PiperProvider(httpClient),
            new KokoroProvider(httpClient),
            new XttsFastApiProvider(httpClient),
            new PocketTtsProvider(httpClient),
            new ChatterboxProvider(httpClient),
            new OmniVoiceProvider(httpClient),
            new InworldProvider(httpClient),
            new CartesiaProvider(httpClient),
            new DeepgramProvider(httpClient),
            new MeloTtsProvider(httpClient),
            new Mimic3Provider(httpClient),
            new KoboldCppProvider(httpClient),
        };
        _providers = all.ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ITtsProvider> All => _providers.Values;

    public ITtsProvider? Get(string id) =>
        !string.IsNullOrEmpty(id) && _providers.TryGetValue(id, out var provider) ? provider : null;

    /// <summary>Adds or replaces a provider (tests, future plugins).</summary>
    public void Register(ITtsProvider provider) => _providers[provider.Id] = provider;
}
