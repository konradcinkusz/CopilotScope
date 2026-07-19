using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Forwarding;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<QualityEngine>();

// Insight pipeline — pluggable per-algorithm analyzers (docs/ANALYSIS.md §8).
var pricing = new PricingOptions();
builder.Configuration.GetSection("CopilotScope:Pricing").Bind(pricing.Models);
builder.Services.AddSingleton(pricing);
builder.Services.AddSingleton<IInsightAnalyzer, EditSurvivalAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, ThroughputAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, LatencyUtilityAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, TokenEconomicsAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, FrustrationAnalyzer>();
builder.Services.AddSingleton<InsightPipeline>();
builder.Services.AddSingleton<OtlpForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OtlpForwarder>());

// Persistence is optional: the "copilotdb" connection string is injected by the
// Aspire AppHost (WithReference(db)); without it the collector runs in-memory only,
// so `dotnet run` on a bare machine still works.
var connectionString = builder.Configuration.GetConnectionString("copilotdb");
var persistenceEnabled = !string.IsNullOrEmpty(connectionString);
if (persistenceEnabled)
{
    builder.Services.AddSingleton(new SessionRepository(connectionString!));
    builder.Services.AddSingleton<PersistenceWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());
}

var app = builder.Build();

var ingestApiKey = app.Configuration["CopilotScope:Ingest:ApiKey"]; // null/empty → open (dev mode)
var store = app.Services.GetRequiredService<SessionStore>();
var quality = app.Services.GetRequiredService<QualityEngine>();
var insightPipeline = app.Services.GetRequiredService<InsightPipeline>();
var forwarder = app.Services.GetRequiredService<OtlpForwarder>();
var persistence = app.Services.GetService<PersistenceWriter>(); // null when persistence disabled

// ---------------------------------------------------------------- OTLP ingest
// Copilot Chat's default exporter is otlp-http (protobuf) on http://localhost:4318.
// The three standard OTLP/HTTP paths are implemented below.

var otlp = app.MapGroup("/v1");

otlp.MapPost("/{signal}", async (string signal, HttpRequest request, ILogger<Program> logger) =>
{
    if (signal is not ("traces" or "metrics" or "logs"))
        return Results.NotFound();

    if (!string.IsNullOrEmpty(ingestApiKey))
    {
        var provided = request.Headers["x-api-key"].FirstOrDefault()
                    ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        if (provided != ingestApiKey)
        {
            logger.LogWarning("Rejected /v1/{Signal}: missing or wrong x-api-key/Authorization header " +
                "from {RemoteIp}. Set OTEL_EXPORTER_OTLP_HEADERS=\"x-api-key=<key>\" on the client.",
                signal, request.HttpContext.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }
    }

    var contentType = request.ContentType ?? "";
    var isProtobuf = contentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase);
    // Some Copilot surfaces (VS Code metrics/logs exporters, as of July 2026) ship the
    // JSON-only OTLP exporter regardless of exporterType/protocol settings — a confirmed
    // upstream gap (github/copilot-cli#2934), not something fixable from the client side.
    // Accept OTLP/HTTP JSON too so those signals aren't silently dropped.
    var isJson = !isProtobuf && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    if (!isProtobuf && !isJson)
    {
        logger.LogWarning("Rejected /v1/{Signal}: unsupported content type '{ContentType}'. " +
            "Expected OTLP/HTTP protobuf or JSON.", signal, contentType);
        return Results.Json(new { error = "Only OTLP/HTTP protobuf or JSON is supported." },
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    // Real-world OTLP exporters frequently compress payloads; ASP.NET Core does
    // not auto-decompress request bodies, so handle it explicitly.
    Stream body = request.Body;
    var contentEncoding = request.Headers.ContentEncoding.ToString();
    if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        body = new System.IO.Compression.GZipStream(body, System.IO.Compression.CompressionMode.Decompress);
    else if (contentEncoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        body = new System.IO.Compression.DeflateStream(body, System.IO.Compression.CompressionMode.Decompress);

    using var ms = new MemoryStream();
    await body.CopyToAsync(ms);
    var payload = ms.ToArray();

    var batch = new OtlpBatch();
    try
    {
        if (isJson)
        {
            switch (signal)
            {
                case "traces": OtlpJsonDecoder.DecodeTraces(payload, batch); break;
                case "metrics": OtlpJsonDecoder.DecodeMetrics(payload, batch); break;
                case "logs": OtlpJsonDecoder.DecodeLogs(payload, batch); break;
            }
        }
        else
        {
            switch (signal)
            {
                case "traces": OtlpDecoder.DecodeTraces(payload, batch); break;
                case "metrics": OtlpDecoder.DecodeMetrics(payload, batch); break;
                case "logs": OtlpDecoder.DecodeLogs(payload, batch); break;
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to decode OTLP {Signal} payload ({Bytes} bytes, {Format})",
            signal, payload.Length, isJson ? "json" : "protobuf");
        return Results.BadRequest(new { error = ex.Message });
    }

    var knownBefore = store.All.Count;
    var touched = store.Ingest(batch);
    persistence?.MarkDirty(touched);

    // Buckets consumed by a merge must also disappear from Postgres, or they'd
    // come back as ghosts on the next rehydration.
    var merged = store.DrainRemoved();
    if (merged.Count > 0 && app.Services.GetService<SessionRepository>() is { } mergeRepo)
        foreach (var id in merged)
        {
            try { await mergeRepo.DeleteAsync(id, CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not delete merged bucket {Id} from Postgres.", id); }
        }

    forwarder.Enqueue($"/v1/{signal}", payload);

    if (store.All.Count > knownBefore)
        logger.LogInformation("New session(s) started: {Sessions}", string.Join(", ", touched));

    logger.LogDebug("OTLP {Signal}: {Spans} spans, {Metrics} points, {Logs} logs → {Sessions} session(s)",
        signal, batch.Spans.Count, batch.Metrics.Count, batch.Logs.Count, touched.Count);

    // OTLP/HTTP success: empty Export*ServiceResponse. An empty protobuf message is valid;
    // the JSON mapping of the same empty response is `{}`.
    return isJson
        ? Results.Text("{}", "application/json")
        : Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
});

// ------------------------------------------------------------------ query API

var api = app.MapGroup("/api");

api.MapGet("/sessions", (bool? includeInternal) =>
{
    // Build per-repo quality score pools so the list can show relative rank within each repo.
    var userSessions = store.All
        .Where(x => !SessionClassifier.IsInternal(x.Kind) && x.ChatCalls > 0)
        .ToList();
    var repoScores = userSessions
        .Where(x => x.Repository is not null)
        .GroupBy(x => x.Repository!, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(x => quality.Evaluate(x).Score).ToList(), StringComparer.Ordinal);

    return Results.Ok(store.All
        .Where(s => includeInternal == true || !SessionClassifier.IsInternal(s.Kind))
        .OrderByDescending(s => s.LastSeen)
        .Select(s =>
        {
            var scores = s.Repository is { } repo
                && repoScores.TryGetValue(repo, out var rs) && rs.Count >= 3 ? rs : null;
            return Dto.Summary(s, quality, scores);
        }));
});

api.MapGet("/sessions/{id}", (string id) =>
{
    if (store.Get(Uri.UnescapeDataString(id)) is not { } s) return Results.NotFound();
    var userSessions = store.All
        .Where(x => !SessionClassifier.IsInternal(x.Kind) && x.ChatCalls > 0);
    // Prefer repo-scoped peer group; fall back to all sessions when fewer than 3 peers share the repo.
    var repoScores = s.Repository is { } repo
        ? userSessions.Where(x => x.Repository == repo).Select(x => quality.Evaluate(x).Score).ToList()
        : null;
    var allScores = repoScores is { Count: >= 3 }
        ? repoScores
        : userSessions.Select(x => quality.Evaluate(x).Score).ToList();
    return Results.Ok(Dto.Detail(s, quality, insightPipeline, allScores));
});

api.MapDelete("/sessions/{id}", async (string id, ILogger<Program> logger) =>
{
    var key = Uri.UnescapeDataString(id);
    var removed = store.Remove(key);
    if (app.Services.GetService<SessionRepository>() is { } repo)
    {
        try { await repo.DeleteAsync(key, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete session {Id} from Postgres.", key); }
    }
    logger.LogInformation("Session {Id} deleted (existed in memory: {Removed}).", key, removed);
    return removed ? Results.NoContent() : Results.NotFound();
});

api.MapGet("/overview", () => Results.Ok(DtoOverview.Build(store.All, quality)));

api.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    sessions = store.All.Count,
    persistence = persistenceEnabled,
    forwarding = forwarder.Enabled,
    environment = app.Environment.EnvironmentName
}));

app.MapGet("/", () => Results.Text(
    "CopilotScope collector.\n" +
    "OTLP ingest: POST /v1/traces | /v1/metrics | /v1/logs\n" +
    "API: GET /api/sessions | /api/sessions/{id} | /api/health\n" +
    "UI lives in the CopilotScope.Dashboard Blazor app (run via the Aspire AppHost).\n"));

app.Logger.LogInformation(
    """
    CopilotScope collector started ({Env}).
      OTLP/HTTP ingest : POST /v1/traces | /v1/metrics | /v1/logs
      Query API        : GET /api/sessions
      Ingest auth      : {Auth}
      Persistence      : {Persist}
      Forwarding       : {Fwd}
    Point VS Code at this endpoint:
      "github.copilot.chat.otel.enabled": true,
      "github.copilot.chat.otel.otlpEndpoint": "<this host>"
    """,
    app.Environment.EnvironmentName,
    string.IsNullOrEmpty(ingestApiKey) ? "disabled (dev)" : "x-api-key required",
    persistenceEnabled ? "Postgres" : "in-memory only",
    forwarder.Enabled ? "enabled" : "disabled");

app.Run();
