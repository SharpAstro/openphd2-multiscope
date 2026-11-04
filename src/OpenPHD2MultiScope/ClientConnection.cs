using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TianWen.OpenPHD2MultiScope;

/// <summary>
/// One connected imaging program. Replies and broadcast events are written from different
/// tasks, so every write takes <see cref="_writeGate"/>: two unguarded writes interleave their
/// bytes and corrupt both lines. A write that does not finish within the write timeout drops the
/// client, so one stalled program cannot hold up the events every other one is waiting for.
/// </summary>
internal sealed class ClientConnection(int id, TcpClient tcpClient, TimeSpan writeTimeout, ILogger logger) : IAsyncDisposable
{
    private readonly NetworkStream _stream = tcpClient.GetStream();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _closed;

    public int Id { get; } = id;

    public EndPoint? RemoteEndPoint { get; } = tcpClient.Client.RemoteEndPoint;

    public Stream Stream => _stream;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Writes one complete line. Returns <see langword="false"/> (and closes) if the client cannot take it.</summary>
    public async ValueTask<bool> SendAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(writeTimeout);
        try
        {
            await _writeGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(line, timeout.Token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Client {ClientId} ({RemoteEndPoint}) did not take a write within {WriteTimeout}, dropping it", Id, RemoteEndPoint, writeTimeout);
            Close();
            return false;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            logger.LogInformation("Client {ClientId} ({RemoteEndPoint}) went away while writing: {Reason}", Id, RemoteEndPoint, ex.Message);
            Close();
            return false;
        }
    }

    /// <summary>Closes the socket, which also ends a pending read. Safe to call more than once, from any thread.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            tcpClient.Close();
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        tcpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"#{Id} {RemoteEndPoint}";
}
