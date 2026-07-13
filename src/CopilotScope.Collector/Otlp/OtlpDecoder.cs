using System.Text;

namespace CopilotScope.Collector.Otlp;

/// <summary>
/// Decodes OTLP/HTTP protobuf payloads (ExportTraceServiceRequest,
/// ExportMetricsServiceRequest, ExportLogsServiceRequest) into the domain model.
/// Field numbers verified against open-telemetry/opentelemetry-proto (v1.x).
/// Unknown fields are skipped, so the decoder is forward-compatible.
/// </summary>
public static class OtlpDecoder
{
    private static DateTimeOffset FromUnixNano(ulong nano) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(nano / 1_000_000UL));

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // ---------------------------------------------------------------- common

    private static AttrValue ReadAnyValue(ReadOnlySpan<byte> buf)
    {
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: return AttrValue.Str(r.ReadString());          // string_value
                case 2: return AttrValue.Bool(r.ReadVarint() != 0);    // bool_value
                case 3: return AttrValue.Int(unchecked((long)r.ReadVarint())); // int_value
                case 4: return AttrValue.Dbl(r.ReadDouble());          // double_value
                case 5: // array_value → JSON-ish string for display
                case 6: // kvlist_value
                    return AttrValue.Str(ReadCompositeAsString(r.ReadBytes(), f == 5));
                case 7: return AttrValue.Str(Convert.ToBase64String(r.ReadBytes())); // bytes_value
                default: r.Skip(w); break;
            }
        }
        return AttrValue.Str("");
    }

    private static string ReadCompositeAsString(ReadOnlySpan<byte> buf, bool isArray)
    {
        var sb = new StringBuilder(isArray ? "[" : "{");
        var r = new ProtoReader(buf);
        var first = true;
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited)
            {
                if (!first) sb.Append(',');
                first = false;
                if (isArray)
                {
                    sb.Append(ReadAnyValue(r.ReadBytes()).ToString());
                }
                else
                {
                    var (k, v) = ReadKeyValue(r.ReadBytes());
                    sb.Append(k).Append(':').Append(v.ToString());
                }
            }
            else r.Skip(w);
        }
        return sb.Append(isArray ? ']' : '}').ToString();
    }

    private static (string Key, AttrValue Value) ReadKeyValue(ReadOnlySpan<byte> buf)
    {
        var r = new ProtoReader(buf);
        string key = "";
        AttrValue val = default;
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: key = r.ReadString(); break;
                case 2: val = ReadAnyValue(r.ReadBytes()); break;
                default: r.Skip(w); break;
            }
        }
        return (key, val);
    }

    private static Dictionary<string, AttrValue> ReadResource(ReadOnlySpan<byte> buf)
    {
        var attrs = new Dictionary<string, AttrValue>();
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited)
            {
                var (k, v) = ReadKeyValue(r.ReadBytes());
                if (k.Length > 0) attrs[k] = v;
            }
            else r.Skip(w);
        }
        return attrs;
    }

    // ---------------------------------------------------------------- traces

    public static void DecodeTraces(ReadOnlySpan<byte> payload, OtlpBatch batch)
    {
        var r = new ProtoReader(payload);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited) DecodeResourceSpans(r.ReadBytes(), batch);
            else r.Skip(w);
        }
    }

    private static void DecodeResourceSpans(ReadOnlySpan<byte> buf, OtlpBatch batch)
    {
        Dictionary<string, AttrValue> resource = new();
        var scopeSpans = new List<byte[]>();
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: resource = ReadResource(r.ReadBytes()); break;      // Resource
                case 2: scopeSpans.Add(r.ReadBytes().ToArray()); break;      // ScopeSpans
                default: r.Skip(w); break;
            }
        }
        foreach (var ss in scopeSpans)
        {
            var sr = new ProtoReader(ss);
            while (!sr.End)
            {
                var (f, w) = sr.ReadTag();
                if (f == 2 && w == WireType.LengthDelimited)                 // Span
                    batch.Spans.Add(DecodeSpan(sr.ReadBytes(), resource));
                else sr.Skip(w);
            }
        }
    }

    private static OtlpSpan DecodeSpan(ReadOnlySpan<byte> buf, Dictionary<string, AttrValue> resource)
    {
        string traceId = "", spanId = "", name = "";
        string? parentId = null, statusMsg = null;
        ulong start = 0, end = 0;
        var status = 0;
        var attrs = new Dictionary<string, AttrValue>();

        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: traceId = Hex(r.ReadBytes()); break;
                case 2: spanId = Hex(r.ReadBytes()); break;
                case 4: parentId = Hex(r.ReadBytes()); break;
                case 5: name = r.ReadString(); break;
                case 7: start = r.ReadFixed64(); break;
                case 8: end = r.ReadFixed64(); break;
                case 9:
                    var (k, v) = ReadKeyValue(r.ReadBytes());
                    if (k.Length > 0) attrs[k] = v;
                    break;
                case 15:
                    var sr = new ProtoReader(r.ReadBytes());
                    while (!sr.End)
                    {
                        var (sf, sw) = sr.ReadTag();
                        if (sf == 2) statusMsg = sr.ReadString();
                        else if (sf == 3) status = (int)sr.ReadVarint();
                        else sr.Skip(sw);
                    }
                    break;
                default: r.Skip(w); break;
            }
        }

        return new OtlpSpan
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = string.IsNullOrEmpty(parentId) ? null : parentId,
            Name = name,
            Start = FromUnixNano(start),
            End = FromUnixNano(end),
            StatusCode = status,
            StatusMessage = statusMsg,
            Attributes = attrs,
            Resource = resource
        };
    }

    // --------------------------------------------------------------- metrics

    public static void DecodeMetrics(ReadOnlySpan<byte> payload, OtlpBatch batch)
    {
        var r = new ProtoReader(payload);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited) DecodeResourceMetrics(r.ReadBytes(), batch);
            else r.Skip(w);
        }
    }

    private static void DecodeResourceMetrics(ReadOnlySpan<byte> buf, OtlpBatch batch)
    {
        Dictionary<string, AttrValue> resource = new();
        var scopeMetrics = new List<byte[]>();
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: resource = ReadResource(r.ReadBytes()); break;
                case 2: scopeMetrics.Add(r.ReadBytes().ToArray()); break;
                default: r.Skip(w); break;
            }
        }
        foreach (var sm in scopeMetrics)
        {
            var sr = new ProtoReader(sm);
            while (!sr.End)
            {
                var (f, w) = sr.ReadTag();
                if (f == 2 && w == WireType.LengthDelimited)
                    DecodeMetric(sr.ReadBytes(), resource, batch);
                else sr.Skip(w);
            }
        }
    }

    private static void DecodeMetric(ReadOnlySpan<byte> buf, Dictionary<string, AttrValue> resource, OtlpBatch batch)
    {
        string name = "", unit = "";
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: name = r.ReadString(); break;
                case 3: unit = r.ReadString(); break;
                case 5:  // gauge
                case 7:  // sum
                    DecodeNumberPoints(r.ReadBytes(), name, unit, f == 5 ? MetricKind.Gauge : MetricKind.Sum, resource, batch);
                    break;
                case 9:  // histogram
                    DecodeHistogramPoints(r.ReadBytes(), name, unit, resource, batch);
                    break;
                default: r.Skip(w); break; // exponential_histogram / summary skipped
            }
        }
    }

    private static void DecodeNumberPoints(ReadOnlySpan<byte> buf, string name, string unit, MetricKind kind,
        Dictionary<string, AttrValue> resource, OtlpBatch batch)
    {
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited)
            {
                var pr = new ProtoReader(r.ReadBytes());
                var attrs = new Dictionary<string, AttrValue>();
                ulong time = 0; double value = 0;
                while (!pr.End)
                {
                    var (pf, pw) = pr.ReadTag();
                    switch (pf)
                    {
                        case 3: time = pr.ReadFixed64(); break;
                        case 4: value = pr.ReadDouble(); break;                       // as_double
                        case 6: value = unchecked((long)pr.ReadFixed64()); break;     // as_int (sfixed64)
                        case 7:
                            var (k, v) = ReadKeyValue(pr.ReadBytes());
                            if (k.Length > 0) attrs[k] = v;
                            break;
                        default: pr.Skip(pw); break;
                    }
                }
                batch.Metrics.Add(new OtlpMetricPoint
                {
                    MetricName = name, Unit = unit, Kind = kind,
                    Time = FromUnixNano(time), Value = value, Count = 1,
                    Attributes = attrs, Resource = resource
                });
            }
            else r.Skip(w);
        }
    }

    private static void DecodeHistogramPoints(ReadOnlySpan<byte> buf, string name, string unit,
        Dictionary<string, AttrValue> resource, OtlpBatch batch)
    {
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited)
            {
                var pr = new ProtoReader(r.ReadBytes());
                var attrs = new Dictionary<string, AttrValue>();
                ulong time = 0, count = 0; double sum = 0;
                while (!pr.End)
                {
                    var (pf, pw) = pr.ReadTag();
                    switch (pf)
                    {
                        case 3: time = pr.ReadFixed64(); break;
                        case 4: count = pr.ReadFixed64(); break;
                        case 5: sum = pr.ReadDouble(); break;
                        case 9:
                            var (k, v) = ReadKeyValue(pr.ReadBytes());
                            if (k.Length > 0) attrs[k] = v;
                            break;
                        default: pr.Skip(pw); break;
                    }
                }
                batch.Metrics.Add(new OtlpMetricPoint
                {
                    MetricName = name, Unit = unit, Kind = MetricKind.Histogram,
                    Time = FromUnixNano(time), Value = sum, Count = (long)count,
                    Attributes = attrs, Resource = resource
                });
            }
            else r.Skip(w);
        }
    }

    // ------------------------------------------------------------------ logs

    public static void DecodeLogs(ReadOnlySpan<byte> payload, OtlpBatch batch)
    {
        var r = new ProtoReader(payload);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            if (f == 1 && w == WireType.LengthDelimited) DecodeResourceLogs(r.ReadBytes(), batch);
            else r.Skip(w);
        }
    }

    private static void DecodeResourceLogs(ReadOnlySpan<byte> buf, OtlpBatch batch)
    {
        Dictionary<string, AttrValue> resource = new();
        var scopeLogs = new List<byte[]>();
        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: resource = ReadResource(r.ReadBytes()); break;
                case 2: scopeLogs.Add(r.ReadBytes().ToArray()); break;
                default: r.Skip(w); break;
            }
        }
        foreach (var sl in scopeLogs)
        {
            var sr = new ProtoReader(sl);
            while (!sr.End)
            {
                var (f, w) = sr.ReadTag();
                if (f == 2 && w == WireType.LengthDelimited)
                    batch.Logs.Add(DecodeLogRecord(sr.ReadBytes(), resource));
                else sr.Skip(w);
            }
        }
    }

    private static OtlpLogEvent DecodeLogRecord(ReadOnlySpan<byte> buf, Dictionary<string, AttrValue> resource)
    {
        ulong time = 0;
        var severity = 0;
        string? body = null, eventName = null, traceId = null;
        var attrs = new Dictionary<string, AttrValue>();

        var r = new ProtoReader(buf);
        while (!r.End)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: time = r.ReadFixed64(); break;
                case 2: severity = (int)r.ReadVarint(); break;
                case 5: body = ReadAnyValue(r.ReadBytes()).ToString(); break;
                case 6:
                    var (k, v) = ReadKeyValue(r.ReadBytes());
                    if (k.Length > 0) attrs[k] = v;
                    break;
                case 9: traceId = Hex(r.ReadBytes()); break;
                case 12: eventName = r.ReadString(); break;
                default: r.Skip(w); break;
            }
        }

        // Some SDKs emit the event name as an attribute instead of field 12.
        eventName ??= attrs.TryGetValue("event.name", out var en) ? en.ToString() : null;

        return new OtlpLogEvent
        {
            EventName = eventName,
            Time = FromUnixNano(time),
            SeverityNumber = severity,
            Body = body,
            TraceId = traceId,
            Attributes = attrs,
            Resource = resource
        };
    }
}
