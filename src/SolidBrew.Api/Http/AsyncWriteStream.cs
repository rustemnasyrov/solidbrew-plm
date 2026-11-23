namespace SolidBrew.Api.Http;

/// <summary>
/// Обёртка над телом ответа для <see cref="System.IO.Compression.ZipArchive"/>: асинхронные записи идут напрямую,
/// а редкие синхронные (хвост DeflateStream при закрытии элемента) копятся в небольшом буфере и отправляются
/// следующей асинхронной записью или <see cref="FlushAsync(CancellationToken)"/>. Поток ASP.NET Core не блокируется.
/// </summary>
internal sealed class AsyncWriteStream(Stream inner) : Stream
{
    private readonly MemoryStream _pending = new();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await SendPendingAsync(cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        // Синхронная отправка запрещена — данные уйдут при следующей асинхронной операции.
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await SendPendingAsync(cancellationToken);
        await inner.FlushAsync(cancellationToken);
    }

    private async ValueTask SendPendingAsync(CancellationToken ct)
    {
        if (_pending.Length == 0) return;
        await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
        _pending.SetLength(0);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
