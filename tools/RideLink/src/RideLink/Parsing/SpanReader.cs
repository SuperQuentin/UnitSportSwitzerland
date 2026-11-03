using System.Buffers.Binary;

namespace RideLink.Parsing;

/// <summary>
/// Thrown when a characteristic value is shorter than its own flags claim, or otherwise
/// cannot be decoded. Carries the raw bytes so the failure can be reproduced from a log.
/// </summary>
public sealed class RideLinkParseException : Exception
{
    public RideLinkParseException(string message, ReadOnlySpan<byte> buffer, int offset)
        : base($"{message} (offset {offset} of {buffer.Length}: {Convert.ToHexString(buffer)})")
    {
        Payload = buffer.ToArray();
        Offset = offset;
    }

    /// <summary>The complete characteristic value that failed to parse.</summary>
    public byte[] Payload { get; }

    /// <summary>Byte offset the reader had reached.</summary>
    public int Offset { get; }
}

/// <summary>
/// Sequential little-endian reader over a characteristic value.
/// <para>
/// Every GATT fitness measurement characteristic is a flags bitmask followed by a variable
/// set of optional fields, so field offsets are only knowable by walking the buffer in bit
/// order. Parsers therefore consume through this reader and never index a constant offset.
/// </para>
/// <para>
/// All multi-octet fields in the GATT Specification Supplement are little-endian
/// (Bluetooth Core Specification, Vol 3, Part G, §3.3.3 — "values are in little-endian").
/// </para>
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    public SpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    /// <summary>Octets not yet consumed.</summary>
    public readonly int Remaining => _buffer.Length - _offset;

    /// <summary>Octets consumed so far.</summary>
    public readonly int Position => _offset;

    private void Require(int count, string field)
    {
        if (Remaining < count)
            throw new RideLinkParseException(
                $"Truncated value: {field} needs {count} more octet(s)", _buffer, _offset);
    }

    public byte ReadUInt8(string field = "uint8")
    {
        Require(1, field);
        return _buffer[_offset++];
    }

    public sbyte ReadInt8(string field = "sint8") => unchecked((sbyte)ReadUInt8(field));

    public ushort ReadUInt16(string field = "uint16")
    {
        Require(2, field);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_offset..]);
        _offset += 2;
        return v;
    }

    public short ReadInt16(string field = "sint16")
    {
        Require(2, field);
        short v = BinaryPrimitives.ReadInt16LittleEndian(_buffer[_offset..]);
        _offset += 2;
        return v;
    }

    /// <summary>
    /// 24-bit unsigned. Used by FTMS "Total Distance" and by the Cross Trainer flags field,
    /// neither of which has a BinaryPrimitives equivalent.
    /// </summary>
    public uint ReadUInt24(string field = "uint24")
    {
        Require(3, field);
        uint v = (uint)(_buffer[_offset] | (_buffer[_offset + 1] << 8) | (_buffer[_offset + 2] << 16));
        _offset += 3;
        return v;
    }

    public uint ReadUInt32(string field = "uint32")
    {
        Require(4, field);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_offset..]);
        _offset += 4;
        return v;
    }

    public int ReadInt32(string field = "sint32")
    {
        Require(4, field);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_buffer[_offset..]);
        _offset += 4;
        return v;
    }

    /// <summary>Consumes and discards, for fields we decode but do not surface.</summary>
    public void Skip(int count, string field = "reserved")
    {
        Require(count, field);
        _offset += count;
    }

    /// <summary>Remaining octets, without consuming them. Used for trailing UTF-8 strings.</summary>
    public readonly ReadOnlySpan<byte> PeekRest() => _buffer[_offset..];
}
