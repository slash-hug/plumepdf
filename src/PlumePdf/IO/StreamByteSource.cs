using System.Buffers;

namespace PlumePdf.IO;

/// <summary>
/// A <see cref="ByteSource"/> backed by a seekable <see cref="Stream"/> or an in-memory
/// <see cref="ReadOnlyMemory{T}"/> buffer — used for <c>PdfDocument.Open(Stream)</c>,
/// <c>Open(ReadOnlyMemory&lt;byte&gt;)</c>, and as the <see cref="PdfOptions.PreferStreamIo"/>
/// fallback for path-based opens. Stream-backed reads are served from a small
/// <see cref="ArrayPool{T}"/>-rented read-ahead window rather than issuing a syscall per
/// tokenizer read; a non-seekable stream must be buffered into a
/// <see cref="ReadOnlyMemory{T}"/> by the caller before construction (the async open path
/// documents this).
/// </summary>
internal sealed class StreamByteSource : ByteSource
{
    private const int BufferSize = 64 * 1024;

    private readonly Stream? _stream;
    private readonly bool _ownsStream;
    private readonly ReadOnlyMemory<byte>? _memory;
    private readonly byte[]? _buffer;
    private readonly object _gate = new();
    private readonly long _length;
    private long _bufferStart = -1;
    private int _bufferLength;
    private bool _disposed;

    /// <summary>Wraps a seekable, readable <paramref name="stream"/>.</summary>
    /// <param name="stream">The stream to read from. Must support <see cref="Stream.CanSeek"/> and <see cref="Stream.CanRead"/>.</param>
    /// <param name="ownsStream">When <see langword="true"/>, <see cref="Dispose"/> also disposes <paramref name="stream"/>.</param>
    public StreamByteSource(Stream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable; buffer a non-seekable source into memory first.", nameof(stream));
        }

        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _length = stream.Length;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>Wraps an in-memory buffer directly, with no streaming or buffering involved.</summary>
    public StreamByteSource(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
        _length = memory.Length;
    }

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override int Read(long position, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        if (position >= _length || destination.IsEmpty)
        {
            return 0;
        }

        var toRead = (int)Math.Min(destination.Length, _length - position);

        if (_memory is { } memory)
        {
            memory.Span.Slice((int)position, toRead).CopyTo(destination);
            return toRead;
        }

        lock (_gate)
        {
            var written = 0;
            while (written < toRead)
            {
                if (position < _bufferStart || position >= _bufferStart + _bufferLength)
                {
                    RefillBuffer(position);
                    if (_bufferLength == 0)
                    {
                        break;
                    }
                }

                var offsetInBuffer = (int)(position - _bufferStart);
                var available = _bufferLength - offsetInBuffer;
                var chunk = Math.Min(available, toRead - written);
                _buffer.AsSpan(offsetInBuffer, chunk).CopyTo(destination.Slice(written, chunk));
                written += chunk;
                position += chunk;
            }

            return written;
        }
    }

    private void RefillBuffer(long position)
    {
        _stream!.Position = position;
        _bufferStart = position;
        _bufferLength = 0;
        while (_bufferLength < _buffer!.Length)
        {
            var read = _stream.Read(_buffer, _bufferLength, _buffer.Length - _bufferLength);
            if (read == 0)
            {
                break;
            }

            _bufferLength += read;
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        if (_ownsStream)
        {
            _stream?.Dispose();
        }
    }
}
