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
            _sessions = await Collector.GetSessionsAsync(_cts.Token);

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
