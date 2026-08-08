using System.Net;
using System.Net.Http.Json;
using CopilotScope.Collector.Api;

namespace CopilotScope.AgentForge.Clients;

/// <summary>Thin HTTP client for the Collector's read-only session API. Never calls any
/// ingest/admin endpoint — AgentForge only ever reads sessions that a PersonaCohort already
/// names explicitly.</summary>
public sealed class CollectorClient(HttpClient http) : ICollectorClient
{
    public async Task<SessionDetailDto?> GetSessionDetailAsync(string sessionId, CancellationToken ct)
    {
        var path = $"/api/sessions/{Uri.EscapeDataString(sessionId)}";
        using var response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionDetailDto>(cancellationToken: ct);
    }
}
