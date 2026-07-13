using System.Buffers.Binary;
using System.Text;

namespace CopilotScope.Collector.Otlp;

/// <summary>
/// Minimal protobuf wire-format reader. Supports everything OTLP needs:
/// varint, fixed32/64, length-delimited fields and packed repeated scalars.
/// Implemented in-repo so the collector has zero NuGet dependencies.
/// </summary>
public ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    public readonly bool End => _pos >= _buffer.Length;

    /// <summary>Reads the next tag. Returns (fieldNumber, wireType); fieldNumber 0 means end of buffer.</summary>
    public (int Field, WireType Wire) ReadTag()
    {
        if (End) return (0, WireType.Varint);
        var tag = ReadVarint();
        return ((int)(tag >> 3), (WireType)(tag & 0x7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (_pos >= _buffer.Length) throw new InvalidDataException("Truncated varint.");
            var b = _buffer[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 64) throw new InvalidDataException("Varint too long.");
        }
    }

    public ulong ReadFixed64()
    {
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_pos, 8));
        _pos += 8;
        return v;
    }

    public uint ReadFixed32()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadFixed64());

    public ReadOnlySpan<byte> ReadBytes()
    {
        var len = (int)ReadVarint();
        if (len < 0 || _pos + len > _buffer.Length) throw new InvalidDataException("Truncated length-delimited field.");
        var span = _buffer.Slice(_pos, len);
        _pos += len;
        return span;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(WireType wire)
    {
        switch (wire)
        {
            case WireType.Varint: ReadVarint(); break;
            case WireType.Fixed64: _pos += 8; break;
            case WireType.LengthDelimited: _ = ReadBytes(); break;
            case WireType.Fixed32: _pos += 4; break;
            default: throw new InvalidDataException($"Unsupported wire type {wire}.");
        }
    }
}

public enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5
}
