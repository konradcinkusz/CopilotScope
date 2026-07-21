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
    public void QuickSetHasSixDistinctPersonaSessions()
    {
        var sessions = SessionGenerator.BuildQuickSet(new Random(42));

        Assert.Equal(6, sessions.Count);
        Assert.Equal(6, sessions.Select(s => s.Id).Distinct().Count());
        Assert.All(sessions, s => Assert.StartsWith("seed-quick-", s.Id));
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
