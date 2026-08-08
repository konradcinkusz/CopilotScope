using System.Net;
using System.Text;
using CopilotScope.AgentForge.Clients;
using Xunit;

namespace CopilotScope.Tests;

public class CollectorClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private const string SampleSessionJson = """
        {
          "summary": {
            "id": "s-1",
            "agent": "claude-code",
            "repository": null,
            "branch": null,
            "firstSeen": "2026-08-01T00:00:00Z",
            "lastSeen": "2026-08-01T00:05:00Z",
            "inputTokens": 0, "outputTokens": 0, "cacheReadTokens": 0,
            "chatCalls": 0, "chatErrors": 0, "toolCalls": 0, "toolErrors": 0,
            "agentInvocations": 0, "turns": 0,
            "editsAccepted": 0, "editsRejected": 0, "thumbsUp": 0, "thumbsDown": 0,
            "linesAdded": 0, "linesRemoved": 0,
            "ttftP50Ms": 0, "ttftP95Ms": 0,
            "models": {},
            "quality": { "score": 88.5, "confidence": 0.9, "grade": "B", "components": [] },
            "kind": 0

          },
          "tools": [],
          "errorTypes": {},
          "events": [],
          "transcript": [
            { "time": "2026-08-01T00:01:00Z", "model": "claude", "prompt": "hi", "response": "hello", "turn": 0 }
          ],
          "turns": { "algorithm": "TFRA", "turns": [], "bestIndex": null, "worstIndex": null, "findings": [] },
          "insights": []
        }
        """;

    [Fact]
    public async Task GetSessionDetailAsync_DeserializesSuccessResponse()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleSessionJson, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-collector") };
        var client = new CollectorClient(http);

        var detail = await client.GetSessionDetailAsync("s-1", CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("s-1", detail!.Summary.Id);
        Assert.Equal(88.5, detail.Summary.Quality.Score);
        Assert.Single(detail.Transcript);
        Assert.Equal("hi", detail.Transcript[0].Prompt);
    }

    [Fact]
    public async Task GetSessionDetailAsync_ReturnsNullOn404()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-collector") };
        var client = new CollectorClient(http);

        var detail = await client.GetSessionDetailAsync("missing", CancellationToken.None);

        Assert.Null(detail);
    }
}
