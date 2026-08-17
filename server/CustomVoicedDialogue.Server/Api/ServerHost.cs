using System.Text.Json;
using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CustomVoicedDialogue.Server.Api;

/// <summary><paramref name="Context"/> is the plugin's short description of
/// the scene the line is spoken in (in combat, sneaking, a hostile
/// listener).  Empty for an ordinary conversation.</summary>
public sealed record SynthRequest(string Text, string VoicePath, string? VoiceType, bool IsPlayer, bool? Wait = null, string? Context = null);

public sealed record PrefetchRequest(List<SynthRequest> Lines);

/// <summary>
/// The localhost HTTP server the F4SE plugin talks to.  Binds loopback
/// only — that is the trust boundary; nothing is reachable from the
/// network.  Wav responses stream from the content cache.
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly SynthesisService _synthesis;
    private WebApplication? _app;

    public ServerHost(AppConfig config, SynthesisService synthesis)
    {
        _config = config;
        _synthesis = synthesis;
    }

    public bool Running => _app is not null;

    public DateTimeOffset? LastGameContact { get; private set; }

    public event Action? StateChanged;

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenLocalhost(_config.Port);
        });

        var app = builder.Build();
        MapEndpoints(app);
        await app.StartAsync();
        _app = app;
        // The first synthesis otherwise pays the provider voice-list fetch
        // (seconds); warm it now so the session's first line is fast.
        _synthesis.WarmVoiceCache();
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }
        var app = _app;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
        StateChanged?.Invoke();
    }

    private void MapEndpoints(WebApplication app)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        app.MapGet("/api/status", (string? gameRoot) =>
        {
            LastGameContact = DateTimeOffset.Now;
            // The plugin polls this; retiring stale jobs here means a voice
            // change takes effect within one poll even if the next request
            // is a cache-warm replay.
            _synthesis.SyncVoiceGeneration();
            // The plugin reports where it writes voice files.  Only it knows
            // the real path (MO2 resolves it through the virtual file
            // system), and the line catalogue needs it to tell whether
            // generated audio still exists in the game.
            if (!string.IsNullOrWhiteSpace(gameRoot) && !string.Equals(_config.GameRoot, gameRoot, StringComparison.OrdinalIgnoreCase))
            {
                _config.GameRoot = gameRoot;
                _config.Save();
                _synthesis.ValidateLines();
            }
            return Results.Json(new
            {
                version = typeof(ServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                provider = _config.Provider,
                ready = !string.IsNullOrEmpty(_config.Provider),
                queueDepth = _synthesis.QueueDepth,
                voiceFingerprint = _synthesis.PlayerVoiceFingerprint(),
                npcVoiceFingerprint = _synthesis.NpcVoiceFingerprint(),
                invalidated = _synthesis.TakePendingInvalidations(),
            });
        });

        app.MapPost("/api/synth", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            LastGameContact = DateTimeOffset.Now;
            var synth = await JsonSerializer.DeserializeAsync<SynthRequest>(request.Body, jsonOptions, cancellationToken);
            if (synth is null || string.IsNullOrWhiteSpace(synth.Text) || string.IsNullOrWhiteSpace(synth.VoicePath))
            {
                return Results.BadRequest(new { error = "text and voicePath are required" });
            }

            var status = await _synthesis.RequestAsync(synth.Text, synth.VoicePath, synth.VoiceType ?? "", synth.IsPlayer, cancellationToken, synth.Wait ?? true, synth.Context ?? "");
            return status.State switch
            {
                // The File.Exists guard keeps a delete race from throwing an
                // unhandled 500; the client treats 202 as "ask again".
                JobState.Done when File.Exists(status.WavPath) => Results.File(status.WavPath!, "audio/wav"),
                JobState.Failed => Results.UnprocessableEntity(new { error = status.Failure }),
                _ => Results.Accepted(value: new { queued = true }),
            };
        });

        app.MapGet("/api/result", async (string voicePath, int? waitMs) =>
        {
            LastGameContact = DateTimeOffset.Now;
            var status = await _synthesis.QueryAsync(voicePath, Math.Clamp(waitMs ?? 0, 0, 3000));
            if (status is null)
            {
                return Results.NotFound(new { error = "unknown voicePath" });
            }
            return status.State switch
            {
                JobState.Done when File.Exists(status.WavPath) => Results.File(status.WavPath!, "audio/wav"),
                JobState.Failed => Results.UnprocessableEntity(new { error = status.Failure }),
                _ => Results.Accepted(value: new { queued = true }),
            };
        });

        app.MapPost("/api/prefetch", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            LastGameContact = DateTimeOffset.Now;
            var prefetch = await JsonSerializer.DeserializeAsync<PrefetchRequest>(request.Body, jsonOptions, cancellationToken);
            if (prefetch is null)
            {
                return Results.BadRequest(new { error = "lines are required" });
            }

            var queued = 0;
            var cached = 0;
            foreach (var line in prefetch.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text) && !string.IsNullOrWhiteSpace(l.VoicePath)))
            {
                var status = await _synthesis.RequestAsync(line.Text, line.VoicePath, line.VoiceType ?? "", line.IsPlayer, cancellationToken, scene: line.Context ?? "");
                if (status.State == JobState.Done)
                {
                    cached++;
                }
                else
                {
                    queued++;
                }
            }
            return Results.Accepted(value: new { queued, cached });
        });

        app.MapGet("/api/voices", async (CancellationToken cancellationToken) =>
        {
            var provider = _synthesis.Providers.Get(_config.Provider);
            if (provider is null)
            {
                return Results.Json(Array.Empty<object>());
            }
            try
            {
                var settings = _config.SettingsFor(provider);
                var voices = await provider.ListVoicesAsync(settings, cancellationToken);
                return Results.Json(voices.Select(v => new { id = v.Id, name = v.DisplayName, gender = v.Gender.ToString().ToLowerInvariant() }));
            }
            catch (Exception exception)
            {
                return Results.Problem(exception.Message);
            }
        });
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
