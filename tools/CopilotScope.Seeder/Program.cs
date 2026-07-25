// CopilotScope.Seeder
// Builds a batch of realistic, comprehensive Copilot sessions and pushes them straight into
// a running collector via POST /api/admin/seed — no OTLP encoding, no Postgres network access,
// no restart required. Always clears previously seeded data first (rows/sessions whose id
// starts with "seed-"), so re-running against an already-running container never piles up
// duplicates or leaves stale sessions behind.
//
//   dotnet run --project tools/CopilotScope.Seeder -- [profile] [collectorUrl]
//     profile:      quick (~7 sessions incl. showcase, for local first-run) | demo (default: a big varied set incl. showcase chats)
//     collectorUrl: default http://localhost:4318
//
//   Optional flags (any position): --days N (demo spread window, default 14)
//                                   --seed N (RNG seed, default 42 — reproducible by default)
//                                   --api-key KEY (defaults to $COPILOTSCOPE_API_KEY)

using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Persistence;
using CopilotScope.Seeder;

var positional = new List<string>();
string? daysArg = null, seedArg = null, apiKeyArg = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--days": daysArg = args[++i]; break;
        case "--seed": seedArg = args[++i]; break;
        case "--api-key": apiKeyArg = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}

var profile = positional.Count > 0 ? positional[0] : "demo";
var collectorUrl = (positional.Count > 1 ? positional[1] : "http://localhost:4318").TrimEnd('/');
var spreadDays = daysArg is not null ? int.Parse(daysArg) : 14;
var rngSeed = seedArg is not null ? int.Parse(seedArg) : 42;
var apiKey = apiKeyArg ?? Environment.GetEnvironmentVariable("COPILOTSCOPE_API_KEY");

var rng = new Random(rngSeed);
var sessions = profile.Equals("quick", StringComparison.OrdinalIgnoreCase)
    ? SessionGenerator.BuildQuickSet(rng)
    : SessionGenerator.BuildDemoSet(rng, spreadDays);

Console.WriteLine($"Generated {sessions.Count} session(s) for profile '{profile}' → {collectorUrl}");

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var payload = new SeedRequest(Reset: true, Sessions: sessions.Select(PersistedSession.From).ToList());

using var http = new HttpClient();
using var request = new HttpRequestMessage(HttpMethod.Post, collectorUrl + "/api/admin/seed")
{
    Content = JsonContent.Create(payload, options: jsonOptions)
};
if (!string.IsNullOrEmpty(apiKey)) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

HttpResponseMessage response;
try
{
    response = await http.SendAsync(request);
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"Could not reach the collector at {collectorUrl}: {ex.Message}");
    Console.WriteLine("Is it running? (dotnet run --project src/CopilotScope.AppHost, or docker compose up)");
    Environment.Exit(1);
    return;
}

var body = await response.Content.ReadAsStringAsync();
if (!response.IsSuccessStatusCode)
{
    Console.WriteLine($"Seed request failed: HTTP {(int)response.StatusCode} — {body}");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"Collector response: {body}");
Console.WriteLine("Done. Open the dashboard to see the seeded sessions.");
