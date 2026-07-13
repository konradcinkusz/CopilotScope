using System.Threading.Channels;

namespace CopilotScope.Collector.Forwarding;

public sealed class ForwardOptions
{
    /// <summary>Base OTLP/HTTP endpoint of the upstream backend, e.g. https://otlp.example.com (paths /v1/* are appended).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Headers added to forwarded requests, e.g. Authorization or api keys.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>
/// Fire-and-forget passthrough of the raw OTLP payloads to an upstream backend
/// (Grafana Cloud, an enterprise OTel Collector, Azure Monitor via a gateway, …).
/// The local dashboard never depends on the upstream being reachable: payloads
/// are queued on a bounded channel and dropped under sustained backpressure.
/// </summary>
public sealed class OtlpForwarder : BackgroundService
{
    private readonly Channel<(string Path, byte[] Body)> _queue =
        Channel.CreateBounded<(string, byte[])>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ForwardOptions _options;
    private readonly ILogger<OtlpForwarder> _logger;

    public OtlpForwarder(IConfiguration config, ILogger<OtlpForwarder> logger)
    {
        _logger = logger;
        _options = config.GetSection("CopilotScope:Forward").Get<ForwardOptions>() ?? new ForwardOptions();
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.Endpoint);

    public void Enqueue(string path, byte[] body)
    {
        if (Enabled) _queue.Writer.TryWrite((path, body));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        _logger.LogInformation("OTLP forwarding enabled → {Endpoint}", _options.Endpoint);

        await foreach (var (path, body) in _queue.Reader.ReadAllAsync(ct))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post,
                        _options.Endpoint!.TrimEnd('/') + path)
                    {
                        Content = new ByteArrayContent(body)
                    };
                    req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
                    foreach (var (k, v) in _options.Headers)
                        req.Headers.TryAddWithoutValidation(k, v);

                    var resp = await _http.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode) break;
                    _logger.LogWarning("Forward {Path} → HTTP {Status} (attempt {Attempt})", path, (int)resp.StatusCode, attempt + 1);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Forward {Path} failed: {Message} (attempt {Attempt})", path, ex.Message, attempt + 1);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt)), ct);
            }
        }
    }
}
