using CopilotScope.Dashboard.Services;
using Microsoft.AspNetCore.Components;

namespace CopilotScope.Dashboard.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    [Inject] public required CollectorClient Collector { get; set; }

    private List<SessionSummaryDto>? _sessions;
    private SessionDetailDto? _detail;
    private HealthDto? _health;
    private string? _selectedId;
    private bool _confirmDelete;
    private bool _showChat;
    private bool _showInternal;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Parses one transcript entry into renderable chat messages (prompt side then response side).</summary>
    private static IEnumerable<ChatMessage> Messages(TranscriptEntryDto entry)
    {
        foreach (var m in ChatMessageParser.Parse(entry.Prompt, "user")) yield return m;
        foreach (var m in ChatMessageParser.Parse(entry.Response, "assistant")) yield return m;
    }

    private static string RoleClass(string role) => role switch
    {
        "user" => "user",
        "assistant" or "model" => "assistant",
        "system" or "developer" => "system",
        "tool" or "function" => "tool",
        _ => "assistant"
    };

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
        _ = PollAsync(); // fire-and-forget refresh loop for the lifetime of the circuit
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await RefreshAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { /* circuit closed */ }
    }

    private async Task RefreshAsync()
    {
        try
        {
            _health = await Collector.GetHealthAsync(_cts.Token);
            _sessions = await Collector.GetSessionsAsync(_showInternal, _cts.Token);

            // Auto-focus the most recent session until the user picks one explicitly.
            _selectedId ??= _sessions.FirstOrDefault()?.Id;
            if (_selectedId is not null)
                _detail = await Collector.GetSessionAsync(_selectedId, _cts.Token) ?? _detail;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _health = null; // collector unreachable — keep the last known data on screen
        }
    }

    private async Task ToggleShowInternalAsync(bool value)
    {
        _showInternal = value;
        await RefreshAsync();
    }

    private async Task SelectAsync(string id)
    {
        _selectedId = id;
        _confirmDelete = false;
        _showChat = false;
        _detail = await Collector.GetSessionAsync(id, _cts.Token);
    }

    private async Task DeleteAsync()
    {
        if (_selectedId is null) return;
        var deleted = await Collector.DeleteSessionAsync(_selectedId, _cts.Token);
        if (deleted)
        {
            _sessions?.RemoveAll(s => s.Id == _selectedId);
            _selectedId = null;
            _detail = null;
            _showChat = false;
        }
        _confirmDelete = false;
        await RefreshAsync();
    }

    private static string KindLabel(SessionKind kind) => kind switch
    {
        SessionKind.InternalTitleGeneration => "title-gen",
        SessionKind.InternalSummary => "summary",
        SessionKind.InternalHelper => "internal",
        SessionKind.Unattributed => "unattributed",
        _ => ""
    };

    private static string SegClass(int i) => i switch
    {
        < 16 => "seg-low",
        < 28 => "seg-mid",
        < 34 => "seg-high",
        _ => "seg-top"
    };

    private static string FmtTokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.0") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.0") + "k",
        _ => n.ToString()
    };

    private static string Pct(double v) =>
        (v * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Traffic-light class for one quality component. "unknown" (no samples yet) is
    /// distinct from "bad" — it means the signal hasn't arrived, not that it's failing.</summary>
    private static string TrafficClass(QualityComponentDto c) => c.Samples switch
    {
        0 => "unknown",
        _ when c.Value >= 0.7 => "good",
        _ when c.Value >= 0.4 => "warn",
        _ => "bad"
    };

    /// <summary>The component doing the most damage to the composite score right now, ranked by
    /// weighted deficit (weight × shortfall) rather than raw value — a low-weight component at 0
    /// matters less than a high-weight one at 0.5. Ignores components with no samples: you can't
    /// blame a factor that hasn't reported in yet.</summary>
    private static QualityComponentDto? WorstComponent(QualityReportDto q) =>
        q.Components.Where(c => c.Samples > 0)
                     .OrderByDescending(c => c.Weight * (1 - c.Value))
                     .FirstOrDefault(c => c.Weight * (1 - c.Value) > 0.01);

    /// <summary>Coarse session lifecycle read purely off FirstSeen/LastSeen — no explicit "session
    /// closed" signal exists in the telemetry, so this is a heuristic, not a fact from the client.</summary>
    private static (string Label, string Css) SessionStatus(SessionSummaryDto s)
    {
        var now = DateTimeOffset.UtcNow;
        var sinceStart = now - s.FirstSeen;
        var sinceActivity = now - s.LastSeen;

        if (sinceActivity > TimeSpan.FromMinutes(15)) return ("Ended", "ended");
        if (sinceActivity > TimeSpan.FromMinutes(1)) return ("Idle", "idle");
        if (sinceStart < TimeSpan.FromSeconds(20)) return ("Just started", "new");
        return ("Active", "active");
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        return d.TotalSeconds < 60 ? $"{d.TotalSeconds:0} s temu"
             : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0} min temu"
             : $"{d.TotalHours:0} h temu";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
