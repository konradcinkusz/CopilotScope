using CopilotScope.AgentForge.Clients;
using CopilotScope.AgentForge.Domain;
using CopilotScope.AgentForge.Profiling;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using Xunit;

namespace CopilotScope.Tests;

public class PersonaProfileBuilderTests
{
    private sealed class FakeCollectorClient(Dictionary<string, SessionDetailDto> sessions) : ICollectorClient
    {
        public Task<SessionDetailDto?> GetSessionDetailAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(sessions.TryGetValue(sessionId, out var s) ? s : null);
    }

    [Fact]
    public async Task BuildAsync_AggregatesConsentedSessionsAndCapsExemplars()
    {
        var transcriptA = Enumerable.Range(0, 25)
            .Select(i => new TranscriptEntry(DateTimeOffset.UtcNow.AddMinutes(i), "claude", $"prompt {i}", $"response {i}", i))
            .ToList();
        var transcriptB = Enumerable.Range(0, 25)
            .Select(i => new TranscriptEntry(DateTimeOffset.UtcNow.AddMinutes(100 + i), "claude", $"prompt-b {i}", $"response-b {i}", i))
            .ToList();

        var sessionA = AgentForgeTestSupport.MakeSessionDetail(
            "s-1", 80, transcriptA, new List<ToolStatDto> { new("read_file", 5, 0, 10) });
        var sessionB = AgentForgeTestSupport.MakeSessionDetail(
            "s-2", 90, transcriptB, new List<ToolStatDto> { new("read_file", 3, 0, 10), new("edit_file", 2, 0, 20) });

        var fake = new FakeCollectorClient(new Dictionary<string, SessionDetailDto>
        {
            ["s-1"] = sessionA,
            ["s-2"] = sessionB
        });
        var builder = new PersonaProfileBuilder(fake);
        var cohort = new PersonaCohort
        {
            PersonaId = "p-1",
            DisplayLabel = "Test Persona",
            ConsentGrantedBy = "consent-giver",
            ConsentDate = new DateOnly(2026, 1, 1),
            SessionIds = new List<string> { "s-1", "s-2", "session-not-in-cohort-data" }
        };

        var profile = await builder.BuildAsync(cohort, CancellationToken.None);

        Assert.Equal(2, profile.SessionsUsed); // the missing/unknown session id is skipped, not an error
        Assert.Equal(85.0, profile.AvgQualityScore);
        Assert.True(profile.Exemplars.Count <= 40);
        Assert.Contains("read_file", profile.CommonTools);
    }

    [Fact]
    public async Task BuildAsync_WithNoMatchingSessions_ReturnsEmptyProfile()
    {
        var fake = new FakeCollectorClient(new Dictionary<string, SessionDetailDto>());
        var builder = new PersonaProfileBuilder(fake);
        var cohort = new PersonaCohort
        {
            PersonaId = "p-2",
            DisplayLabel = "Nobody Yet",
            ConsentGrantedBy = "consent-giver",
            ConsentDate = new DateOnly(2026, 1, 1),
            SessionIds = new List<string> { "s-missing" }
        };

        var profile = await builder.BuildAsync(cohort, CancellationToken.None);

        Assert.Equal(0, profile.SessionsUsed);
        Assert.Equal(0, profile.AvgQualityScore);
        Assert.Empty(profile.Exemplars);
        Assert.Empty(profile.CommonTools);
    }
}
