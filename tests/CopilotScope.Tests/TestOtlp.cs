using System.Buffers.Binary;
using System.Text;

namespace CopilotScope.Tests;

/// <summary>
/// Minimal OTLP protobuf writer for tests. Field numbers mirror
/// open-telemetry/opentelemetry-proto — the same wire format the decoder reads.
/// </summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public void Varint(int field, ulong value) { Tag(field, 0); WriteVarint(value); }

    public void Fixed64(int field, ulong value)
    {
        Tag(field, 1);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        _ms.Write(b);
    }

    public void Double(int field, double value) => Fixed64(field, BitConverter.DoubleToUInt64Bits(value));
    public void Bytes(int field, byte[] value) { Tag(field, 2); WriteVarint((ulong)value.Length); _ms.Write(value); }
    public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public void Message(int field, byte[] encoded) => Bytes(field, encoded);

    public byte[] ToArray() => _ms.ToArray();

    private void Tag(int field, int wire) => WriteVarint((ulong)(field << 3 | wire));

    private void WriteVarint(ulong v)
    {
        while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
        _ms.WriteByte((byte)v);
    }
}

public static class TestOtlp
{
    public static byte[] TracesRequest(string sessionId, params byte[][] spans)
    {
        var scopeSpans = new ProtoWriter();
        foreach (var s in spans) scopeSpans.Message(2, s);

        var resourceSpans = new ProtoWriter();
        resourceSpans.Message(1, Resource(sessionId));
        resourceSpans.Message(2, scopeSpans.ToArray());

        var req = new ProtoWriter();
        req.Message(1, resourceSpans.ToArray());
        return req.ToArray();
    }

    public static byte[] Resource(string sessionId)
    {
        var res = new ProtoWriter();
        res.Message(1, KeyValue("service.name", "copilot-chat"));
        res.Message(1, KeyValue("session.id", sessionId));
        return res.ToArray();
    }

    public static byte[] Span(byte[] traceId, byte[] spanId, string name,
        DateTimeOffset start, DateTimeOffset end, string? error, params (string, object)[] attrs)
    {
        var w = new ProtoWriter();
        w.Bytes(1, traceId);
        w.Bytes(2, spanId);
        w.String(5, name);
        w.Fixed64(7, (ulong)start.ToUnixTimeMilliseconds() * 1_000_000UL);
        w.Fixed64(8, (ulong)end.ToUnixTimeMilliseconds() * 1_000_000UL);
        foreach (var (k, v) in attrs) w.Message(9, KeyValue(k, v));
        if (error is not null)
        {
            w.Message(9, KeyValue("error.type", error));
            var status = new ProtoWriter();
            status.String(2, error);
            status.Varint(3, 2); // STATUS_CODE_ERROR
            w.Message(15, status.ToArray());
        }
        return w.ToArray();
    }

    public static byte[] KeyValue(string key, object value)
    {
        var any = new ProtoWriter();
        switch (value)
        {
            case string s: any.String(1, s); break;
            case bool b: any.Varint(2, b ? 1UL : 0UL); break;
            case long l: any.Varint(3, unchecked((ulong)l)); break;
            case int i: any.Varint(3, unchecked((ulong)(long)i)); break;
            case double d: any.Double(4, d); break;
            default: any.String(1, value.ToString() ?? ""); break;
        }
        var kv = new ProtoWriter();
        kv.String(1, key);
        kv.Message(2, any.ToArray());
        return kv.ToArray();
    }

    public static byte[] Trace(int seed)
    {
        var b = new byte[16];
        new Random(seed).NextBytes(b);
        return b;
    }

    public static byte[] SpanId(int seed)
    {
        var b = new byte[8];
        new Random(seed + 1000).NextBytes(b);
        return b;
    }
}
