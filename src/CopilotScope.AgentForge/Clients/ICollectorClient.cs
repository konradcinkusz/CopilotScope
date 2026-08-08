using CopilotScope.Collector.Api;

namespace CopilotScope.AgentForge.Clients;

/// <summary>Read-only access to the Collector's session query API. Exists as an interface so
/// profile building can be unit tested without a live Collector.</summary>
public interface ICollectorClient
{
    Task<SessionDetailDto?> GetSessionDetailAsync(string sessionId, CancellationToken ct);
}
