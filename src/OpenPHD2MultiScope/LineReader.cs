using System.Buffers;
using System.Text;

namespace TianWen.OpenPHD2MultiScope;

/// <summary>
/// Reads LF- or CRLF-terminated UTF-8 lines, refusing one longer than a limit. A
/// <see cref="StreamReader"/> would buffer a newline-free stream without bound; a JSON-RPC
/// request to PHD2 is a few hundred bytes, so anything near the limit is not a client.
/// </summary>
internal sealed class LineReader(Stream stream, int maxLineBytes = LineReader.DefaultMaxLineBytes) : IDisposable
{
    public const int DefaultMaxLineBytes = 64 * 1024;

    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(maxLineBytes + 1);
    private int _start;
    private int _end;

    /// <summary>The next line without its terminator, or <see langword="null"/> at end of stream.</summary>
    /// <exception cref="InvalidDataException">The line exceeds the limit.</exception>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == '\r')
                {
                    length--;
                }
                var line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start = newline + 1;
                return line;
            }

            if (_end - _start >= maxLineBytes)
            {
                throw new InvalidDataException($"A line exceeded {maxLineBytes} bytes without a newline");
            }

            // Compact, then fill.
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end, Math.Min(_buffer.Length, maxLineBytes + 1) - _end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // A final unterminated line is still a line.
                if (_end > _start)
                {
                    var tail = Encoding.UTF8.GetString(_buffer, _start, _end - _start);
                    _start = _end;
                    return tail;
                }
                return null;
            }
            _end += read;
        }
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
