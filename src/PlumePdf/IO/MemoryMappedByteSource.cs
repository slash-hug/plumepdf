using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace PlumePdf.IO;

/// <summary>
/// A <see cref="ByteSource"/> backed by a read-only <see cref="MemoryMappedFile"/> — the
/// default for <c>PdfDocument.Open(path)</c>. Construction fails with a coded
/// <see cref="PlumePdfException"/> (range 1000-1999, IO) for conditions a caller should
/// react to by falling back to <see cref="StreamByteSource"/> or setting
/// <see cref="PdfOptions.PreferStreamIo"/>: a missing file, an empty file (mapping a
/// zero-length file is not supported by the platform), or a failure to create the mapping
/// (e.g. another process holds an incompatible lock).
/// </summary>
/// <remarks>
/// The underlying file handle is opened with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/> — wider sharing than <see cref="MemoryMappedFile"/>'s own
/// path-based convenience constructor uses (plain <see cref="FileShare.Read"/>), which is
/// what the save-to-open-path contract needs on Windows: a document opened via mmap must
/// still be replaceable by <c>PdfDocument.Save</c>'s temp-file-then-atomic-<see cref="File.Move(string,string,bool)"/>
/// and appendable-to by <c>SaveIncremental</c>'s independent write handle, both while this
/// mapping is still open, exactly as POSIX already allows by default. Read-only access means
/// nothing about the mapped bytes themselves changes underneath a reader that keeps using
/// this same <see cref="MemoryMappedByteSource"/> instance after such a replace/append — it
/// keeps seeing the file as it was at open time (or, on Windows, whatever the OS defers to
/// after a delete-while-mapped) — so this is purely a sharing-mode widening, not a
/// read-consistency change.
/// </remarks>
internal sealed class MemoryMappedByteSource : ByteSource
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _length;
    private readonly object _detachLock = new();
    private volatile byte[]? _memoryCopy;
    private bool _disposed;

    /// <summary>Opens <paramref name="path"/> as a read-only memory-mapped byte source.</summary>
    public MemoryMappedByteSource(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PlumePdfException("PLUME1001", $"Could not access '{path}'.", ex);
        }

        if (!info.Exists)
        {
            throw new PlumePdfException("PLUME1002", $"File not found: '{path}'.");
        }

        _length = info.Length;
        if (_length == 0)
        {
            throw new PlumePdfException("PLUME1003", $"'{path}' is empty; there is nothing to memory-map. Retry with PdfOptions.PreferStreamIo if the file is expected to grow.");
        }

        FileStream? fileStream = null;
        try
        {
            // Opened directly (rather than via MemoryMappedFile.CreateFromFile(path, ...),
            // which only shares FileShare.Read) so this mapping doesn't itself block a later
            // Save/SaveIncremental to the same path from opening its own handle (see remarks).
            fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _file = MemoryMappedFile.CreateFromFile(fileStream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            fileStream = null; // ownership transferred - the MemoryMappedFile now disposes it
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlumePdfException("PLUME1004", $"Could not memory-map '{path}'; retry with PdfOptions.PreferStreamIo.", ex);
        }
        finally
        {
            fileStream?.Dispose();
        }

        try
        {
            _accessor = _file.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file.Dispose();
            throw new PlumePdfException("PLUME1004", $"Could not create a view over '{path}'; retry with PdfOptions.PreferStreamIo.", ex);
        }
    }

    /// <inheritdoc/>
    public override long Length => _length;

    /// <summary>
    /// Buffers the whole file into memory and releases the mapping and its OS file handle,
    /// keeping this instance (and every reader holding it) fully functional. Required before
    /// <c>PdfDocument.Save</c> can replace the currently-open file on Windows: a live mapped
    /// section keeps the file object locked against replace-by-rename no matter how wide the
    /// original handle's sharing flags were (proven in CI; the sharing-mode widening in the
    /// class remarks is necessary for SaveIncremental's append handle but not sufficient for
    /// replace). Idempotent; throws <c>PLUME1006</c> for sources past the single-array limit.
    /// </summary>
    public void DetachFromFile()
    {
        if (_memoryCopy is not null)
        {
            return;
        }

        lock (_detachLock)
        {
            if (_memoryCopy is not null)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_length > int.MaxValue)
            {
                throw new PlumePdfException("PLUME1006", $"Cannot buffer this {_length}-byte memory-mapped source to release its file handle (exceeds the 2 GiB single-array limit); save to a different path instead of over the currently-open file.");
            }

            var copy = new byte[_length];
            var read = _accessor.ReadArray(0, copy, 0, (int)_length);
            if (read != _length)
            {
                throw new PlumePdfException("PLUME1005", $"Short read while buffering the memory-mapped source: expected {_length} bytes, got {read}.");
            }

            _memoryCopy = copy;
            _accessor.Dispose();
            _file.Dispose();
        }
    }

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

        if (_memoryCopy is { } copy)
        {
            copy.AsSpan((int)position, toRead).CopyTo(destination);
            return toRead;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            var read = _accessor.ReadArray(position, buffer, 0, toRead);
            buffer.AsSpan(0, read).CopyTo(destination);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

        if (_memoryCopy is null)
        {
            _accessor.Dispose();
            _file.Dispose();
        }

        _memoryCopy = null;
    }
}
