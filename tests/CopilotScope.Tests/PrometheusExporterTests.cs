using System.Globalization;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

// The exposition format is a contract with Prometheus, not a rendering detail: a
// stray locale-formatted float or an unescaped label value breaks the scrape for
// every metric in the payload, not just the offending line.

public class PrometheusExporterTests
{
    private static PrometheusExporter Build(PrometheusOptions? options = null, params CopilotSession[] sessions)
    {
        var store = new SessionStore();
        foreach (var session in sessions) store.Put(session);
        return new PrometheusExporter(store, new QualityEngine(), new PricingOptions(),
            options ?? new PrometheusOptions());
    }

    private static CopilotSession Session(string id, EmitterKind emitter = EmitterKind.VSCode)
    {
        var session = new CopilotSession { Id = id, EmitterKind = emitter };
        session.Apply(s =>
        {
            s.ChatCalls = 4;
            s.ToolCalls = 6;
            s.ToolErrors = 1;
            s.Turns = 3;
            s.InputTokens = 1_000;
            s.OutputTokens = 500;
            s.CacheReadTokens = 250;
            s.EditsAccepted = 2;
            s.EditsRejected = 1;
            s.ThumbsUp = 1;
            s.TtftMs.Add(400);
            s.TtftMs.Add(900);
            s.SurvivalScores.Add(0.8);
            s.ModelUsage["gpt-4o"] = new ModelStat { Calls = 4, InputTokens = 1_000, OutputTokens = 500, CacheReadTokens = 250 };
            s.ErrorTypes["timeout"] = 2;
        });
        return session;
    }

    [Fact]
    public void EmitsHelpAndTypeBeforeSamples()
    {
        var output = Build(null, Session("s1")).Render();

        Assert.Contains("# HELP copilotscope_sessions ", output);
        Assert.Contains("# TYPE copilotscope_sessions gauge\n", output);
        Assert.Contains("copilotscope_sessions{emitter=\"vscode\",grade=", output);
    }

    [Fact]
    public void EveryMetricFamilyIsDeclaredExactlyOnceAndContiguous()
    {
        var output = Build(new PrometheusOptions { PerSession = true }, Session("s1"), Session("s2", EmitterKind.CLI)).Render();

        var declared = new List<string>();
        var sampleOrder = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
            {
                declared.Add(line.Split(' ')[2]);
                continue;
            }
            if (line.StartsWith('#')) continue;

            var name = line.Split('{')[0].Split(' ')[0];
            if (sampleOrder.Count == 0 || sampleOrder[^1] != name) sampleOrder.Add(name);
        }

        // A metric name declared twice, or samples split into two blocks, both make
        // the scrape fail with "second HELP line" / "unexpected duplicate".
        Assert.Equal(declared.Distinct().Count(), declared.Count);
        Assert.Equal(sampleOrder.Distinct().Count(), sampleOrder.Count);
        Assert.All(sampleOrder, name => Assert.Contains(name, declared));
    }

    [Fact]
    public void FloatsUseInvariantFormattingRegardlessOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // pl-PL renders 0.5 as "0,5", which Prometheus reads as two label-less tokens.
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            var output = Build(null, Session("s1")).Render();

            var samples = output.Split('\n')
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();

            Assert.NotEmpty(samples);
            Assert.All(samples, line => Assert.DoesNotContain(",", line.Split(' ')[^1]));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void LabelValuesAreEscaped()
    {
        var session = Session("weird\"id\\with\nbreaks");
        var output = Build(new PrometheusOptions { PerSession = true }, session).Render();

        Assert.Contains("session=\"weird\\\"id\\\\with\\nbreaks\"", output);
        // The raw newline must not survive — it would terminate the sample line early.
        Assert.DoesNotContain("weird\"id", output);
    }

    [Fact]
    public void PerSessionSeriesAreOffByDefault()
    {
        var output = Build(null, Session("s1")).Render();

        Assert.DoesNotContain("copilotscope_session_quality_score", output);
        Assert.Contains("copilotscope_quality_score_sum", output);
    }

    [Fact]
    public void PerSessionSeriesRespectTheCeiling()
    {
        var sessions = Enumerable.Range(0, 10).Select(i => Session($"s{i}")).ToArray();
        var output = Build(new PrometheusOptions { PerSession = true, MaxSessionSeries = 3 }, sessions).Render();

        var emitted = output.Split('\n')
            .Count(l => l.StartsWith("copilotscope_session_quality_score{", StringComparison.Ordinal));

        Assert.Equal(3, emitted);
        Assert.Contains("copilotscope_session_series_dropped 7", output);
    }

    [Fact]
    public void SumAndCountLetPromqlRecoverTheMean()
    {
        var output = Build(null, Session("s1"), Session("s2")).Render();

        var sum = SampleValue(output, "copilotscope_quality_score_sum{emitter=\"vscode\"}");
        var count = SampleValue(output, "copilotscope_quality_score_count{emitter=\"vscode\"}");

        Assert.Equal(2, count);
        // Two identical sessions: the mean must land back on a single session's score.
        Assert.InRange(sum / count, 1, 100);
    }

    [Fact]
    public void InternalHelperSessionsAreExcluded()
    {
        var helper = new CopilotSession { Id = "helper", EmitterKind = EmitterKind.VSCode };
        helper.Apply(s =>
        {
            s.ChatCalls = 1;
            s.AddTranscript(DateTimeOffset.UtcNow, "gpt-4o-mini",
                "Please write a brief title for the following request", "Title", 0);
        });

        var output = Build(new PrometheusOptions { PerSession = true }, helper, Session("real")).Render();

        Assert.DoesNotContain("session=\"helper\"", output);
        Assert.Contains("session=\"real\"", output);
        Assert.Equal(1, SampleValue(output, "copilotscope_quality_score_count{emitter=\"vscode\"}"));
    }

    [Fact]
    public void TokenAndCostSamplesCarryTheirLabels()
    {
        var output = Build(null, Session("s1")).Render();

        Assert.Equal(1_000, SampleValue(output, "copilotscope_tokens_total{emitter=\"vscode\",type=\"input\"}"));
        Assert.Equal(500, SampleValue(output, "copilotscope_tokens_total{emitter=\"vscode\",type=\"output\"}"));
        Assert.Contains("copilotscope_cost_usd_total{emitter=\"vscode\",model=\"gpt-4o\"}", output);
        Assert.Contains("copilotscope_errors_by_type_total{type=\"timeout\"} 2", output);
    }

    [Fact]
    public void LatencyIsReportedInSeconds()
    {
        var output = Build(null, Session("s1")).Render();

        // 400 ms p50 sample -> 0.4 s, not 400.
        var p50 = SampleValue(output, "copilotscope_ttft_seconds{emitter=\"vscode\",aggregate=\"p50\"}");
        Assert.InRange(p50, 0.01, 10);
    }

    [Fact]
    public void EmptyStoreStillRendersValidOutput()
    {
        var output = Build().Render();

        Assert.Contains("# TYPE copilotscope_sessions gauge", output);
        Assert.DoesNotContain("NaN", output);
    }

    private static double SampleValue(string output, string series)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith(series + " ", StringComparison.Ordinal));
        Assert.NotNull(line);
        return double.Parse(line!.Split(' ')[^1], CultureInfo.InvariantCulture);
    }
}
