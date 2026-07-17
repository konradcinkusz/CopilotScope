using System.Text.Json;

namespace CopilotScope.Collector.Otlp;

/// <summary>
/// Decodes OTLP/HTTP JSON payloads — the protobuf JSON mapping (camelCase field names,
/// base64-encoded trace/span IDs, 64-bit integers as strings). Exists because VS Code
/// Copilot Chat's metrics/logs OTel exporters ship the JSON-only exporter package
/// (@opentelemetry/exporter-*-otlp-http) regardless of exporterType/protocol settings —
/// a confirmed upstream gap (github/copilot-cli#2934) with no client-side fix today.
/// Traces already arrive as protobuf via <see cref="OtlpDecoder"/>; this covers the rest.
/// </summary>
public static class OtlpJsonDecoder
{
    private static DateTimeOffset FromUnixNano(ulong nano) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(nano / 1_000_000UL));

    private static ulong NanoOf(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) ? ParseU64(v) : 0;

    private static ulong ParseU64(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => ulong.TryParse(v.GetString(), out var s) ? s : 0,
        JsonValueKind.Number => v.GetUInt64(),
        _ => 0
    };

    private static long ParseI64(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : 0,
        JsonValueKind.Number => v.GetInt64(),
        _ => 0
    };

    /// <summary>OTLP JSON encodes proto `bytes` fields (trace_id, span_id) as base64 — convert to
    /// the same lowercase hex representation the protobuf decoder produces, so both paths agree.</summary>
    private static string HexFromBase64(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try { return Convert.ToHexString(Convert.FromBase64String(base64)).ToLowerInvariant(); }
        catch (FormatException) { return base64; } // some emitters send hex directly, non-conformant but seen in the wild
    }

    private static AttrValue ReadAnyValue(JsonElement v)
    {
        if (v.TryGetProperty("stringValue", out var s)) return AttrValue.Str(s.GetString() ?? "");
        if (v.TryGetProperty("boolValue", out var b)) return AttrValue.Bool(b.GetBoolean());
        if (v.TryGetProperty("intValue", out var i)) return AttrValue.Int(ParseI64(i));
        if (v.TryGetProperty("doubleValue", out var d)) return AttrValue.Dbl(d.GetDouble());
        if (v.TryGetProperty("bytesValue", out var by)) return AttrValue.Str(by.GetString() ?? "");
        if (v.TryGetProperty("arrayValue", out var arr) && arr.TryGetProperty("values", out var avals))
            return AttrValue.Str("[" + string.Join(",", avals.EnumerateArray().Select(e => ReadAnyValue(e).ToString())) + "]");
        if (v.TryGetProperty("kvlistValue", out var kv) && kv.TryGetProperty("values", out var kvals))
            return AttrValue.Str("{" + string.Join(",", kvals.EnumerateArray().Select(ReadKeyValueString)) + "}");
        return AttrValue.Str("");
    }

    private static string ReadKeyValueString(JsonElement kvEntry)
    {
        var key = kvEntry.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        var val = kvEntry.TryGetProperty("value", out var v) ? ReadAnyValue(v) : AttrValue.Str("");
        return $"{key}:{val}";
    }

    private static Dictionary<string, AttrValue> ReadAttributes(JsonElement parent)
    {
        var attrs = new Dictionary<string, AttrValue>();
        if (!parent.TryGetProperty("attributes", out var list) || list.ValueKind != JsonValueKind.Array)
            return attrs;
        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("key", out var k)) continue;
            var key = k.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            attrs[key] = entry.TryGetProperty("value", out var v) ? ReadAnyValue(v) : AttrValue.Str("");
        }
        return attrs;
    }

    private static IEnumerable<JsonElement> Arr(JsonElement parent, string prop) =>
        parent.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    // ---------------------------------------------------------------- traces

    public static void DecodeTraces(ReadOnlySpan<byte> payload, OtlpBatch batch)
    {
        using var doc = JsonDocument.Parse(payload.ToArray());
        foreach (var rs in Arr(doc.RootElement, "resourceSpans"))
        {
            var resource = rs.TryGetProperty("resource", out var res) ? ReadAttributes(res) : new();
            foreach (var ss in Arr(rs, "scopeSpans"))
            foreach (var span in Arr(ss, "spans"))
            {
                var statusCode = 0; string? statusMsg = null;
                if (span.TryGetProperty("status", out var status))
                {
                    if (status.TryGetProperty("code", out var sc)) statusCode = sc.GetInt32();
                    if (status.TryGetProperty("message", out var sm)) statusMsg = sm.GetString();
                }
                var parentId = span.TryGetProperty("parentSpanId", out var p) ? HexFromBase64(p.GetString()) : null;
                batch.Spans.Add(new OtlpSpan
                {
                    TraceId = span.TryGetProperty("traceId", out var tid) ? HexFromBase64(tid.GetString()) : "",
                    SpanId = span.TryGetProperty("spanId", out var sid) ? HexFromBase64(sid.GetString()) : "",
                    ParentSpanId = string.IsNullOrEmpty(parentId) ? null : parentId,
                    Name = span.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Start = FromUnixNano(NanoOf(span, "startTimeUnixNano")),
                    End = FromUnixNano(NanoOf(span, "endTimeUnixNano")),
                    StatusCode = statusCode,
                    StatusMessage = statusMsg,
                    Attributes = ReadAttributes(span),
                    Resource = resource
                });
            }
        }
    }

    // --------------------------------------------------------------- metrics

    public static void DecodeMetrics(ReadOnlySpan<byte> payload, OtlpBatch batch)
    {
        using var doc = JsonDocument.Parse(payload.ToArray());
        foreach (var rm in Arr(doc.RootElement, "resourceMetrics"))
        {
            var resource = rm.TryGetProperty("resource", out var res) ? ReadAttributes(res) : new();
            foreach (var sm in Arr(rm, "scopeMetrics"))
            foreach (var metric in Arr(sm, "metrics"))
            {
                var name = metric.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var unit = metric.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";

                if (metric.TryGetProperty("gauge", out var gauge))
                    DecodeNumberPoints(gauge, name, unit, MetricKind.Gauge, resource, batch);
                if (metric.TryGetProperty("sum", out var sum))
                    DecodeNumberPoints(sum, name, unit, MetricKind.Sum, resource, batch);
                if (metric.TryGetProperty("histogram", out var hist))
                    DecodeHistogramPoints(hist, name, unit, resource, batch);
                // exponentialHistogram / summary intentionally skipped, matching OtlpDecoder.
            }
        }
    }

    private static void DecodeNumberPoints(JsonElement container, string name, string unit, MetricKind kind,
        Dictionary<string, AttrValue> resource, OtlpBatch batch)
    {
        foreach (var pt in Arr(container, "dataPoints"))
        {
            var value = pt.TryGetProperty("asDouble", out var ad) ? ad.GetDouble()
                      : pt.TryGetProperty("asInt", out var ai) ? ParseI64(ai)
                      : 0;
            batch.Metrics.Add(new OtlpMetricPoint
            {
                MetricName = name, Unit = unit, Kind = kind,
                Time = FromUnixNano(NanoOf(pt, "timeUnixNano")), Value = value, Count = 1,
                Attributes = ReadAttributes(pt), Resource = resource
            });
        }
    }

    private static void DecodeHistogramPoints(JsonElement container, string name, string unit,
        Dictionary<string, AttrValue> resource, OtlpBatch batch)
    {
        foreach (var pt in Arr(container, "dataPoints"))
        {
            var count = pt.TryGetProperty("count", out var c) ? (long)ParseU64(c) : 0;
            var sum = pt.TryGetProperty("sum", out var s) ? s.GetDouble() : 0;
            batch.Metrics.Add(new OtlpMetricPoint
            {
                MetricName = name, Unit = unit, Kind = MetricKind.Histogram,
                Time = FromUnixNano(NanoOf(pt, "timeUnixNano")), Value = sum, Count = count,
                Attributes = ReadAttributes(pt), Resource = resource
            });
        }
    }

    // ------------------------------------------------------------------ logs

    public static void DecodeLogs(ReadOnlySpan<byte> payload, OtlpBatch batch)
    {
        using var doc = JsonDocument.Parse(payload.ToArray());
        foreach (var rl in Arr(doc.RootElement, "resourceLogs"))
        {
            var resource = rl.TryGetProperty("resource", out var res) ? ReadAttributes(res) : new();
            foreach (var sl in Arr(rl, "scopeLogs"))
            foreach (var log in Arr(sl, "logRecords"))
            {
                var attrs = ReadAttributes(log);
                var eventName = log.TryGetProperty("eventName", out var en) ? en.GetString() : null;
                eventName ??= attrs.TryGetValue("event.name", out var enAttr) ? enAttr.ToString() : null;

                batch.Logs.Add(new OtlpLogEvent
                {
                    EventName = eventName,
                    Time = FromUnixNano(NanoOf(log, "timeUnixNano")),
                    SeverityNumber = log.TryGetProperty("severityNumber", out var sn) ? sn.GetInt32() : 0,
                    Body = log.TryGetProperty("body", out var body) ? ReadAnyValue(body).ToString() : null,
                    TraceId = log.TryGetProperty("traceId", out var tid) ? HexFromBase64(tid.GetString()) : null,
                    Attributes = attrs,
                    Resource = resource
                });
            }
        }
    }
}
