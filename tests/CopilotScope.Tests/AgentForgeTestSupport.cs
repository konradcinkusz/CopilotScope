using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Tests;

/// <summary>Shared helper for AgentForge tests that need a hand-built SessionDetailDto — avoids
/// duplicating the (long) SessionSummaryDto constructor call across test files.</summary>
internal static class AgentForgeTestSupport
{
    public static SessionDetailDto MakeSessionDetail(
        string id, double qualityScore, List<TranscriptEntry> transcript, List<ToolStatDto> tools)
    {
        var quality = new QualityReport(qualityScore, 1.0, "B", new List<QualityComponent>());

        var summary = new SessionSummaryDto(
            id, "claude-code", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            0, 0, 0,
            0, 0, 0, 0,
            0, 0,
            0, 0, 0, 0,
            0, 0,
            0, 0,
            new Dictionary<string, int>(),
            quality,
            SessionKind.UserChat);

        return new SessionDetailDto(
            summary,
            tools,
            new Dictionary<string, int>(),
            new List<SessionEvent>(),
            transcript,
            new TurnAnalysis("TFRA", new List<TurnReport>(), null, null, new List<string>()),
            new List<InsightReport>());
    }
}
