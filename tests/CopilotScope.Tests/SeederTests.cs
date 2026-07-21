using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using CopilotScope.Seeder;
using Xunit;

namespace CopilotScope.Tests;

// Regression coverage for tools/CopilotScope.Seeder: the generated sessions must actually
// exercise the quality engine and insight analyzers the way real telemetry would, not just
// look plausible on paper.

public class SessionFactoryTests
{
    [Fact]
    public void GoldenPersonaScoresHigherThanErrorProne()
    {
        var engine = new QualityEngine();
        var rng = new Random(1);
        var golden = SessionFactory.Build("seed-t-golden", PersonaCatalog.Get("golden"), DateTimeOffset.UtcNow, rng);
        var errorProne = SessionFactory.Build("seed-t-error", PersonaCatalog.Get("error-prone"), DateTimeOffset.UtcNow, rng);

        Assert.True(engine.Evaluate(golden).Score > engine.Evaluate(errorProne).Score);
    }

    [Fact]
    public void FrustratedPersonaTriggersFrustrationMarkers()
    {
        var rng = new Random(2);
        var session = SessionFactory.Build("seed-t-frustrated", PersonaCatalog.Get("frustrated"), DateTimeOffset.UtcNow, rng);

        var report = new FrustrationAnalyzer().Analyze(session);

        Assert.Equal("ok", report.Status);
        Assert.Contains(report.Findings, f => f.Contains("strong marker") || f.Contains("rephrasing"));
    }

    [Fact]
    public void LaggyPersonaFlagsAbandonmentRisk()
    {
        var rng = new Random(3);
        var session = SessionFactory.Build("seed-t-laggy", PersonaCatalog.Get("laggy"), DateTimeOffset.UtcNow, rng);

        var report = new LatencyUtilityAnalyzer().Analyze(session);

        Assert.Equal("ok", report.Status);
        Assert.Contains(report.Findings, f => f.Contains("abandonment"));
    }

    [Fact]
    public void RejectedEditsPersonaHasLowThroughput()
    {
        var rng = new Random(4);
        var golden = SessionFactory.Build("seed-t-golden2", PersonaCatalog.Get("golden"), DateTimeOffset.UtcNow, rng);
        var rejected = SessionFactory.Build("seed-t-rejected", PersonaCatalog.Get("rejected-edits"), DateTimeOffset.UtcNow, rng);

        var analyzer = new ThroughputAnalyzer();
        Assert.True(analyzer.Analyze(golden).Score > analyzer.Analyze(rejected).Score);
    }

    [Theory]
    [InlineData("internal-title")]
    [InlineData("internal-summary")]
    public void InternalPersonasAreClassifiedInternal(string slug)
    {
        var rng = new Random(5);
        var session = SessionFactory.Build($"seed-t-{slug}", PersonaCatalog.Get(slug), DateTimeOffset.UtcNow, rng);

        Assert.True(SessionClassifier.IsInternal(session.Kind));
    }

    [Fact]
    public void UserChatPersonasAreNotClassifiedInternal()
    {
        var rng = new Random(6);
        var session = SessionFactory.Build("seed-t-golden3", PersonaCatalog.Get("golden"), DateTimeOffset.UtcNow, rng);

        Assert.False(SessionClassifier.IsInternal(session.Kind));
    }

    [Fact]
    public void ShowcasePersonaIsLongVariedAndLightsUpEveryPanel()
    {
        var rng = new Random(11);
        var s = SessionFactory.Build("seed-t-showcase", PersonaCatalog.Get("showcase"), DateTimeOffset.UtcNow, rng);

        // At least 30 turns, as required for the demo headline chat.
        Assert.True(s.Turns >= 30, $"expected >= 30 turns, got {s.Turns}");

        // VS Code emitter with every editor-only signal present (both sides non-zero), so no tile is empty.
        Assert.Equal(EmitterKind.VSCode, s.EmitterKind);
        Assert.True(s.EditsAccepted > 0 && s.EditsRejected > 0);
        Assert.True(s.ThumbsUp > 0 && s.ThumbsDown > 0);
        Assert.True(s.LinesAdded > 0 && s.LinesRemoved > 0);

        // Intra-session variety: errors, multiple models, multiple tools and multiple error types.
        Assert.True(s.ChatErrors > 0 || s.ToolErrors > 0);
        Assert.True(s.ModelCalls.Count > 1, "expected model switching across turns");
        Assert.True(s.Tools.Count > 1);
        Assert.True(s.ErrorTypes.Count >= 1);

        // Every insight analyzer produces data (no "no-data" placeholder on the headline session).
        Assert.Equal("ok", new EditSurvivalAnalyzer().Analyze(s).Status);
        Assert.Equal("ok", new ThroughputAnalyzer().Analyze(s).Status);
        Assert.Equal("ok", new LatencyUtilityAnalyzer().Analyze(s).Status);
        Assert.Equal("ok", new TokenEconomicsAnalyzer(new PricingOptions()).Analyze(s).Status);

        // Frustration is scattered in, so it flags on strong markers and rephrasing.
        var frustration = new FrustrationAnalyzer().Analyze(s);
        Assert.Equal("ok", frustration.Status);
        Assert.Contains(frustration.Findings, f => f.Contains("strong marker") || f.Contains("rephrasing"));

        // Latency variety: a pinned >8s stall turn is always present, so the abandonment-risk
        // bucket in the latency-utility model is never empty.
        Assert.Contains(s.TtftMs, t => t > 8000);

        // Turn analysis is not uniformly clean — best/worst are meaningful.
        var turns = SegmentAnalyzer.Analyze(s);
        Assert.True(turns.Turns.Count >= 30);
        Assert.False(turns.Turns.All(t => t.Score >= 1.0), "expected a mix of clean and friction turns");
    }

    [Fact]
    public void BuiltSessionSurvivesPersistedSessionRoundtrip()
    {
        var rng = new Random(7);
        var session = SessionFactory.Build("seed-t-roundtrip", PersonaCatalog.Get("long-epic"), DateTimeOffset.UtcNow, rng);

        var restored = PersistedSession.From(session).ToSession();

        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(session.ChatCalls, restored.ChatCalls);
        Assert.Equal(session.InputTokens, restored.InputTokens);
        Assert.Equal(session.Transcript.Count, restored.Transcript.Count);
        Assert.Equal(session.TurnList.Count, restored.TurnList.Count);
        Assert.Equal(session.LastSeen, restored.LastSeen);
    }
}

public class SessionGeneratorTests
{
    [Fact]
    public void QuickSetHasDistinctPersonaSessionsIncludingShowcase()
    {
        var sessions = SessionGenerator.BuildQuickSet(new Random(42));

        Assert.Equal(7, sessions.Count);
        Assert.Equal(sessions.Count, sessions.Select(s => s.Id).Distinct().Count());
        Assert.All(sessions, s => Assert.StartsWith("seed-quick-", s.Id));
        Assert.Contains(sessions, s => s.Id.EndsWith("showcase"));
    }

    [Fact]
    public void DemoSetGuaranteesAtLeastOneShowcaseSession()
    {
        var sessions = SessionGenerator.BuildDemoSet(new Random(42), days: 5);

        Assert.Contains(sessions, s => s.Id.EndsWith("-showcase") && s.Turns >= 30);
    }

    [Fact]
    public void DemoSetSpansRequestedDaysAndIsDeterministicPerSeed()
    {
        var first = SessionGenerator.BuildDemoSet(new Random(42), days: 5);
        var second = SessionGenerator.BuildDemoSet(new Random(42), days: 5);

        Assert.True(first.Count > 15); // ~4-7 per day across 5 days
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(s => s.Id), second.Select(s => s.Id));
        Assert.Equal(first.Select(s => s.Id).Distinct().Count(), first.Count);
    }
}
