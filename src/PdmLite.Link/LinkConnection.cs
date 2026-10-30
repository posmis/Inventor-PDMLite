using System.IO.Pipes;
using System.Text;

namespace PdmLite.Link;

/// <summary>Чтение и запись строк JSON в канал. Запись потокобезопасна.</summary>
internal sealed class LinkConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly PipeStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LinkConnection(PipeStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public bool IsConnected => _pipe.IsConnected;

    public async Task<LinkMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return null; // канал закрыт
            if (line.Length > 0)
                return LinkMessage.Deserialize(line);
        }
    }

    public async Task WriteAsync(LinkMessage message, CancellationToken ct)
    {
        var line = message.Serialize();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _reader.Dispose();
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // канал уже разорван — дописывать некуда
        }
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
