using System;
using System.Buffers;
using System.Text;
using Slon.Buffers;

namespace Slon;

// 'Unsafe' helper struct that pairs a string with some encoding's bytecount.
// It's up to the user to make sure these values match and that the encoding used to write out the string is the expected one.
readonly struct SizedString
{
    readonly string _value;
    readonly int _byteCount;

    public SizedString(string value)
    {
        _value = value;
        _byteCount = value.Length is 0 ? 0 : -1;
    }

    public int? ByteCount
    {
        get => _byteCount == -1 ? null : _byteCount;
        init
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value));
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            _byteCount = value.GetValueOrDefault();
        }
    }

    public string Value => _value;
    public bool IsDefault => _value is null;

    public SizedString WithEncoding(Encoding encoding)
        => this with { ByteCount = encoding.GetByteCount(_value) };

    public SizedString EnsureByteCount(Encoding encoding)
        => ByteCount is null ? WithEncoding(encoding) : this;

    public static SizedString Empty => new(string.Empty);
    public static implicit operator SizedString(string value) => new(value);
}

static class BufferWriterExtensions
{
    public static void WriteEncoded<T>(ref this BufferWriter<T> buffer, SizedString value, Encoding encoding) where T : IBufferWriter<byte>
        => buffer.WriteEncoded(value.Value.AsSpan(), encoding, value.ByteCount);

    public static void WriteCString<T>(ref this BufferWriter<T> buffer, SizedString value, Encoding encoding) where T : IBufferWriter<byte>
        => buffer.WriteCString(value.Value.AsSpan(), encoding, value.ByteCount);
}

static class StreamingWriterExtensions
{
    public static void WriteEncoded<T>(ref this StreamingWriter<T> writer, SizedString value, Encoding encoding) where T : IStreamingWriter<byte>
        => writer.WriteEncoded(value.Value.AsSpan(), encoding, value.ByteCount);

    public static void WriteCString<T>(ref this StreamingWriter<T> writer, SizedString value, Encoding encoding) where T : IStreamingWriter<byte>
        => writer.WriteCString(value.Value.AsSpan(), encoding, value.ByteCount);
}
