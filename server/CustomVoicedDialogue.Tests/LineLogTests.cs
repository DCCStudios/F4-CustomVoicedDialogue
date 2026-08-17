using CustomVoicedDialogue.Server;
using CustomVoicedDialogue.Server.Lines;

namespace CustomVoicedDialogue.Tests;

public sealed class SceneContextTests
{
    [Fact]
    public void AnOrdinaryConversationReportsNothing()
    {
        // An empty scene is what keeps a calm exchange tagged exactly as it
        // was before the feature existed.
        Assert.Equal("", SceneContexts.Compose(false, false, false));
        Assert.Equal("", SceneContexts.Default.Value);
    }

    [Theory]
    [InlineData(true, false, false, "in combat")]
    [InlineData(false, true, false, "sneaking, staying quiet")]
    [InlineData(false, false, true, "the listener is hostile to them")]
    [InlineData(true, false, true, "in combat; the listener is hostile to them")]
    [InlineData(true, true, true, "in combat; sneaking, staying quiet; the listener is hostile to them")]
    public void ScenesComposeExactlyAsThePluginBuildsThem(bool combat, bool sneaking, bool hostile, string expected)
    {
        // These strings must stay byte-identical to GameContext::Describe on
        // the plugin side: auditioning a delivery in the app is only
        // meaningful if the game sends the same text.
        Assert.Equal(expected, SceneContexts.Compose(combat, sneaking, hostile));
    }

    [Fact]
    public void EveryReportableSituationIsOfferedExactlyOnce()
    {
        // Three independent booleans, so eight situations — the dropdown
        // should cover all of them and repeat none.
        Assert.Equal(8, SceneContexts.All.Count);
        Assert.Equal(8, SceneContexts.All.Select(option => option.Value).Distinct().Count());
        Assert.All(SceneContexts.All, option => Assert.False(string.IsNullOrWhiteSpace(option.DisplayName)));
    }
}

public sealed class LineLogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _logPath;

    public LineLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cvd-linelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "lines.txt");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static LineRecord Sample(string voicePath = @"Sound\Voice\Fallout4.esm\PlayerVoiceMale01\0001ABC2_1.wav") =>
        new()
        {
            VoicePath = voicePath,
            Text = "Are you alright?",
            TaggedText = "[concerned, soft] Are you alRIGHT?",
            Voice = "rick",
            VoiceType = "PlayerVoiceMale01",
            Provider = "inworld",
            Accent = "grimes",
            IsPlayer = true,
            Variant = 0,
            CacheKey = "ABC123",
            Generated = DateTimeOffset.Now,
        };

    [Fact]
    public void RecordsSurviveAReload()
    {
        var log = new LineLog(_logPath);
        log.Record(Sample());

        var reloaded = new LineLog(_logPath);

        var record = Assert.Single(reloaded.Records);
        Assert.Equal(@"Sound\Voice\Fallout4.esm\PlayerVoiceMale01\0001ABC2_1.wav", record.VoicePath);
        Assert.Equal("Are you alright?", record.Text);
        // The tagging is the whole point of the catalogue: it is what was
        // actually performed, and it has to survive the round trip intact.
        Assert.Equal("[concerned, soft] Are you alRIGHT?", record.TaggedText);
        Assert.Equal("grimes", record.Accent);
    }

    [Fact]
    public void TheCatalogueIsPlainTextNamingTheFileAndTheTagging()
    {
        var log = new LineLog(_logPath);
        log.Record(Sample());

        var contents = File.ReadAllText(_logPath);

        Assert.Contains("0001ABC2_1.wav", contents);
        Assert.Contains("Are you alRIGHT?", contents);
    }

    [Fact]
    public void ANewerTakeReplacesTheOlderOneForTheSameFile()
    {
        var log = new LineLog(_logPath);
        log.Record(Sample());
        log.Record(Sample() with { Variant = 1, TaggedText = "[sharp, urgent] Are you alRIGHT?!" });

        var record = Assert.Single(log.Records);
        Assert.Equal(1, record.Variant);
        Assert.Equal("[sharp, urgent] Are you alRIGHT?!", record.TaggedText);
    }

    [Fact]
    public void EachRegenerationAsksForTheNextTake()
    {
        var log = new LineLog(_logPath);
        var sample = Sample();
        log.Record(sample);

        Assert.Equal(1, log.NextVariant(sample.VoicePath));
        log.Record(sample with { Variant = 1 });
        Assert.Equal(2, log.NextVariant(sample.VoicePath));
    }

    [Fact]
    public void ValidateFlagsAWavDeletedFromTheGameFolder()
    {
        var gameRoot = Path.Combine(_directory, "game");
        var cached = Path.Combine(_directory, "ABC123.wav");
        File.WriteAllBytes(cached, [0]);

        var log = new LineLog(_logPath);
        var sample = Sample();
        log.Record(sample);

        // Cached copy present, game copy never written.
        log.Validate(_ => cached, gameRoot);
        Assert.Equal(LineHealth.MissingInGame, Assert.Single(log.Records).Health);

        // Now the game has it too.
        var gameFile = Path.Combine(gameRoot, "Data", sample.VoicePath);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllBytes(gameFile, [0]);
        log.Validate(_ => cached, gameRoot);
        Assert.Equal(LineHealth.Ok, Assert.Single(log.Records).Health);

        // And the user deletes it by hand again.
        File.Delete(gameFile);
        log.Validate(_ => cached, gameRoot);
        Assert.Equal(LineHealth.MissingInGame, Assert.Single(log.Records).Health);
    }

    [Fact]
    public void ValidateFlagsAClearedSoundCache()
    {
        var log = new LineLog(_logPath);
        log.Record(Sample());

        log.Validate(_ => Path.Combine(_directory, "not-there.wav"), gameDataRoot: null);

        Assert.Equal(LineHealth.MissingInCache, Assert.Single(log.Records).Health);
    }

    [Fact]
    public void WithoutTheGameFolderOnlyTheCacheSideIsClaimed()
    {
        var cached = Path.Combine(_directory, "ABC123.wav");
        File.WriteAllBytes(cached, [0]);
        var log = new LineLog(_logPath);
        log.Record(Sample());

        // The plugin has not checked in yet, so the game side is unknown —
        // reporting "ok" there would be a lie.
        log.Validate(_ => cached, gameDataRoot: null);

        Assert.Equal(LineHealth.Unverified, Assert.Single(log.Records).Health);
    }

    [Fact]
    public void ATruncatedLastRecordDoesNotCostTheWholeCatalogue()
    {
        var log = new LineLog(_logPath);
        log.Record(Sample());
        log.Record(Sample(@"Sound\Voice\Fallout4.esm\PlayerVoiceMale01\0001ABC3_1.wav"));

        // Simulate a write cut short by a power loss.
        File.AppendAllText(_logPath, "{\"file\":\"Sound\\\\Voice\\\\broken");

        var reloaded = new LineLog(_logPath);

        Assert.Equal(2, reloaded.Count);
    }

    [Fact]
    public void GeneratingALineAppendsInsteadOfRewritingTheCatalogue()
    {
        var log = new LineLog(_logPath);
        log.Record(Sample(@"Sound\Voice\A.esm\PlayerVoiceMale01\00000001_1.wav"));
        var afterFirst = File.ReadAllLines(_logPath).Length;

        log.Record(Sample(@"Sound\Voice\A.esm\PlayerVoiceMale01\00000002_1.wav"));

        // One more line on disk, not a rewritten file — this is what keeps a
        // generated line's catalogue cost flat as the catalogue grows.
        Assert.Equal(afterFirst + 1, File.ReadAllLines(_logPath).Length);
        Assert.Equal(2, new LineLog(_logPath).Count);
    }

    [Fact]
    public void ANewerTakeWinsOverTheAppendedOlderOne()
    {
        var log = new LineLog(_logPath);
        var sample = Sample();
        log.Record(sample);
        log.Record(sample with { Variant = 3, TaggedText = "[sharp] Are you alRIGHT?!" });

        // Both takes are on disk; loading must resolve to the newest.
        var reloaded = new LineLog(_logPath);
        var record = Assert.Single(reloaded.Records);
        Assert.Equal(3, record.Variant);
        Assert.Equal("[sharp] Are you alRIGHT?!", record.TaggedText);
    }

    [Fact]
    public void SupersededTakesAreEventuallyReclaimed()
    {
        var log = new LineLog(_logPath);
        var sample = Sample();
        // Re-record the same line far more often than the compaction
        // threshold; the file must not grow without bound.
        for (var i = 0; i < 400; i++)
        {
            log.Record(sample with { Variant = i });
        }

        var lines = File.ReadAllLines(_logPath).Count(line => line.StartsWith('{'));

        Assert.True(lines < 400, $"catalogue kept {lines} records for one line");
        Assert.Equal(399, Assert.Single(new LineLog(_logPath).Records).Variant);
    }

    [Fact]
    public void EveryTakeIsRetainedAndSurvivesAReload()
    {
        var log = new LineLog(_logPath);
        var sample = Sample();
        log.Record(sample);
        log.Record(sample with { Variant = 1, CacheKey = "KEY1", TaggedText = "[a] one" });
        log.Record(sample with { Variant = 2, CacheKey = "KEY2", TaggedText = "[b] two" });

        var takes = log.TakesFor(sample.VoicePath);
        Assert.Equal(3, takes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, takes.Select(t => t.Variant));
        // The active (latest) is take 2, but all three are on record.
        Assert.Equal(2, log.Find(sample.VoicePath)!.Variant);
        Assert.Equal(3, log.NextVariant(sample.VoicePath));

        // A reload rebuilds the whole take history from the append-only file.
        var reloaded = new LineLog(_logPath);
        Assert.Equal(3, reloaded.TakesFor(sample.VoicePath).Count);
        Assert.Equal(2, reloaded.Find(sample.VoicePath)!.Variant);
    }

    [Fact]
    public void RestoringAnEarlierTakeMakesItActiveWithoutDuplicating()
    {
        var log = new LineLog(_logPath);
        var sample = Sample();
        log.Record(sample);                                                   // take 0
        log.Record(sample with { Variant = 1, CacheKey = "KEY1" });           // take 1 (active)

        // Restore take 0 by re-recording it: it becomes active, the history
        // still holds exactly two distinct takes, and the next regenerate is
        // still past the highest ever made.
        log.Record(sample);
        Assert.Equal(0, log.Find(sample.VoicePath)!.Variant);
        Assert.Equal(2, log.TakesFor(sample.VoicePath).Count);
        Assert.Equal(2, log.NextVariant(sample.VoicePath));
    }

    [Fact]
    public void ForgettingALineDropsItFromTheFile()
    {
        var log = new LineLog(_logPath);
        var sample = Sample();
        log.Record(sample);

        Assert.True(log.Forget(sample.VoicePath));

        Assert.Empty(new LineLog(_logPath).Records);
    }
}
