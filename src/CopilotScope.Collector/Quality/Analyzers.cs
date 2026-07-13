using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>
/// #4 Edit Survival Analysis — full implementation.
/// Consumes both survival signals Copilot emits: four-gram overlap (how much of
/// the generated text still exists verbatim after the observation window) and
/// no-revert (whether the edit avoided being undone). No-revert is weighted
/// higher: text can be legitimately refactored (low four-gram) while the change
/// itself survives.
/// </summary>
public sealed class EditSurvivalAnalyzer : IInsightAnalyzer
{
    public InsightReport Analyze(CopilotSession session) => session.Snapshot(s =>
    {
        var fourGram = s.SurvivalFourGram;
        var noRevert = s.SurvivalNoRevert;
        if (fourGram.Count == 0 && noRevert.Count == 0)
            return new InsightReport("Edit survival", "Edit Survival Analysis", "no-data", null, [],
                ["No survival telemetry yet — emitted by editor surfaces (VS Code) after edits age past the observation windows."]);

        var fg = fourGram.Count > 0 ? fourGram.Average() : (double?)null;
        var nr = noRevert.Count > 0 ? noRevert.Average() : (double?)null;
        var combined = (fg, nr) switch
        {
            ({ } f, { } n) => 0.4 * f + 0.6 * n,
            ({ } f, null) => f,
            (null, { } n) => n,
            _ => 0
        };

        var metrics = new List<InsightMetric>();
        if (fg is { } f2) metrics.Add(new("four-gram survival (mean)", $"{f2:P0} · {fourGram.Count} samples"));
        if (nr is { } n2) metrics.Add(new("no-revert survival (mean)", $"{n2:P0} · {noRevert.Count} samples"));
        metrics.Add(new("combined (0.4·4-gram + 0.6·no-revert)", $"{combined:P0}"));

        var finding = combined switch
        {
            >= 0.8 => "Generated code is durable — the vast majority survives untouched.",
            >= 0.5 => "Partial rework: suggestions land but get meaningfully edited afterwards.",
            _ => "Heavy rewrite pattern — most generated code is replaced or reverted."
        };

        return new InsightReport("Edit survival", "Edit Survival Analysis", "ok", combined, metrics, [finding]);
    });
}

/// <summary>
/// #5 Acceptance-weighted throughput — full implementation.
/// Productivity density of the session: how much accepted code per unit of
/// interaction and per unit of model output, discounted by rejections.
/// </summary>
public sealed class ThroughputAnalyzer : IInsightAnalyzer
{
    public InsightReport Analyze(CopilotSession session) => session.Snapshot(s =>
    {
        var edits = s.EditsAccepted + s.EditsRejected;
        if (edits == 0 && s.LinesAdded == 0)
            return new InsightReport("Throughput", "Acceptance-weighted throughput", "no-data", null, [],
                ["No edit/LOC telemetry yet — emitted by editor surfaces (VS Code)."]);

        var acceptance = edits > 0 ? (double)s.EditsAccepted / edits : 1.0;
        var turns = Math.Max(1, s.Turns);
        var locPerTurn = s.LinesAdded / turns;
        var adjusted = s.LinesAdded * acceptance / turns;
        var outK = s.OutputTokens / 1000.0;
        var locPerKTok = outK > 0 ? s.LinesAdded / outK : (double?)null;

        var metrics = new List<InsightMetric>
        {
            new("acceptance ratio", $"{acceptance:P0} ({s.EditsAccepted}✓ / {s.EditsRejected}✗)"),
            new("lines added / removed", $"+{s.LinesAdded:0} / −{s.LinesRemoved:0}"),
            new("accepted LOC per turn", $"{adjusted:0.0} (raw {locPerTurn:0.0})")
        };
        if (locPerKTok is { } l) metrics.Add(new("LOC per 1k output tokens", $"{l:0.0}"));

        var findings = new List<string>();
        if (acceptance < 0.5 && edits >= 4)
            findings.Add("More rejections than acceptances — suggestions miss the mark in this session.");
        else if (adjusted >= 10)
            findings.Add("High effective throughput — accepted code lands at a strong rate per turn.");
        else
            findings.Add("Moderate throughput.");

        // Score: acceptance dominates, throughput saturates at ~20 accepted LOC/turn.
        var score = Math.Clamp(0.6 * acceptance + 0.4 * Math.Min(1.0, adjusted / 20.0), 0, 1);
        return new InsightReport("Throughput", "Acceptance-weighted throughput", "ok", score, metrics, findings);
    });
}

/// <summary>
/// #7 Latency-utility model — full implementation.
/// Instead of a single point estimate on p50, the utility curve is applied to
/// every TTFT sample: mean/percentile utilities plus attention-threshold risk
/// buckets (>2 s = degraded attention, >8 s = high abandonment risk).
/// </summary>
public sealed class LatencyUtilityAnalyzer : IInsightAnalyzer
{
    private static double Utility(double ms) => ms <= 300 ? 1.0
        : ms >= 10_000 ? 0.0
        : 1.0 - Math.Log(ms / 300.0) / Math.Log(10_000.0 / 300.0);

    public InsightReport Analyze(CopilotSession session) => session.Snapshot(s =>
    {
        if (s.TtftMs.Count == 0)
            return new InsightReport("Latency utility", "Latency-utility model", "no-data", null, [],
                ["No TTFT samples yet."]);

        var utilities = s.TtftMs.Select(Utility).ToList();
        var mean = utilities.Average();
        var p50 = CopilotSession.Percentile(s.TtftMs, 0.5);
        var p95 = CopilotSession.Percentile(s.TtftMs, 0.95);
        var degraded = (double)s.TtftMs.Count(t => t > 2000) / s.TtftMs.Count;
        var abandon = (double)s.TtftMs.Count(t => t > 8000) / s.TtftMs.Count;

        var metrics = new List<InsightMetric>
        {
            new("mean utility (per-sample)", $"{mean:P0} over {s.TtftMs.Count} samples"),
            new("TTFT p50 / p95", $"{p50:0} ms / {p95:0} ms"),
            new("responses > 2 s (degraded attention)", $"{degraded:P0}"),
            new("responses > 8 s (abandonment risk)", $"{abandon:P0}")
        };

        var findings = new List<string>();
        if (abandon > 0.1) findings.Add("A meaningful share of responses crosses the 8 s abandonment threshold.");
        else if (degraded > 0.5) findings.Add("Over half of responses exceed the 2 s attention threshold — flow suffers.");
        else findings.Add("Latency stays within comfortable interaction thresholds.");

        return new InsightReport("Latency utility", "Latency-utility model", "ok", mean, metrics, findings);
    });
}

/// <summary>Per-MTok USD pricing; configurable via CopilotScope:Pricing, prefix-matched by model name.</summary>
public sealed class PricingOptions
{
    public sealed record ModelPrice(double Input, double Output, double CacheRead);

    public Dictionary<string, ModelPrice> Models { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = new(3.0, 15.0, 0.30),
        ["gpt-4o-mini"] = new(0.15, 0.60, 0.075),
        ["gpt-4o"] = new(2.50, 10.0, 1.25),
        ["gpt-4.1"] = new(2.00, 8.0, 0.50),
        ["claude-haiku"] = new(0.80, 4.0, 0.08),
        ["claude-sonnet"] = new(3.0, 15.0, 0.30),
        ["claude-opus"] = new(15.0, 75.0, 1.50),
    };

    public ModelPrice Resolve(string model)
    {
        // Longest-prefix wins so "gpt-4o-mini" isn't swallowed by "gpt-4o".
        var best = Models
            .Where(kv => kv.Key != "default" && model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Value)
            .FirstOrDefault();
        return best ?? Models["default"];
    }
}

/// <summary>
/// #8 Token &amp; cache economics — full implementation.
/// Per-model cost estimate from the configurable price sheet, cache savings
/// (what the prompt cache avoided paying) and unit economics per turn /
/// accepted edit. Estimates, not invoices — prices drift and Copilot
/// subscriptions abstract them away; the point is relative comparison.
/// </summary>
public sealed class TokenEconomicsAnalyzer(PricingOptions pricing) : IInsightAnalyzer
{
    public InsightReport Analyze(CopilotSession session) => session.Snapshot(s =>
    {
        if (s.ModelUsage.IsEmpty)
            return new InsightReport("Token economics", "Token & cache economics", "no-data", null, [],
                ["No per-model token data yet."]);

        double cost = 0, savings = 0;
        foreach (var (model, u) in s.ModelUsage)
        {
            var p = pricing.Resolve(model);
            cost += u.InputTokens / 1e6 * p.Input
                  + u.OutputTokens / 1e6 * p.Output
                  + u.CacheReadTokens / 1e6 * p.CacheRead;
            savings += u.CacheReadTokens / 1e6 * Math.Max(0, p.Input - p.CacheRead);
        }

        var promptTokens = s.InputTokens + s.CacheReadTokens;
        var cacheRatio = promptTokens > 0 ? (double)s.CacheReadTokens / promptTokens : 0;

        var metrics = new List<InsightMetric>
        {
            new("estimated session cost", $"${cost:0.0000}"),
            new("cache savings", $"${savings:0.0000} ({cacheRatio:P0} of prompt tokens from cache)"),
            new("cost per turn", s.Turns > 0 ? $"${cost / s.Turns:0.0000}" : "—"),
            new("cost per accepted edit", s.EditsAccepted > 0 ? $"${cost / s.EditsAccepted:0.0000}" : "—")
        };
        foreach (var (model, u) in s.ModelUsage.OrderByDescending(kv => kv.Value.InputTokens + kv.Value.OutputTokens))
        {
            var p = pricing.Resolve(model);
            var c = u.InputTokens / 1e6 * p.Input + u.OutputTokens / 1e6 * p.Output + u.CacheReadTokens / 1e6 * p.CacheRead;
            metrics.Add(new($"· {model}", $"${c:0.0000} — {u.InputTokens + u.CacheReadTokens} in / {u.OutputTokens} out"));
        }

        var findings = new List<string> { "Prices are list-price estimates (CopilotScope:Pricing) for relative comparison, not billing." };
        if (cacheRatio >= 0.5) findings.Add("Prompt cache is doing heavy lifting — over half of prompt tokens were cache reads.");
        else if (promptTokens > 50_000 && cacheRatio < 0.1) findings.Add("Large context with almost no cache reuse — likely fresh context every call.");

        return new InsightReport("Token economics", "Token & cache economics", "ok",
            Math.Clamp(cacheRatio, 0, 1), metrics, findings);
    });
}
