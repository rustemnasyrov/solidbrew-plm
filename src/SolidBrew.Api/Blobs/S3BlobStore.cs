using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace SolidBrew.Api.Blobs;

/// <summary>Архив в S3-совместимом хранилище (MinIO): бакет <c>pdm-objects</c>, ключ <c>objects/&lt;2 символа&gt;/&lt;sha256&gt;</c>.</summary>
public sealed class S3BlobStore : IBlobStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3BlobStore(PdmOptions options)
    {
        _bucket = options.S3Bucket;
        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(options.S3AccessKey, options.S3SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.S3Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, Key(sha256), ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task PutAsync(string sha256, Stream content, long length, CancellationToken ct)
    {
        if (await ExistsAsync(sha256, ct)) return;
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Key(sha256),
            InputStream = content,
            ContentType = "application/octet-stream",
            AutoCloseStream = false,
            UseChunkEncoding = false,
        }, ct);
    }

    public async Task<BlobContent?> OpenReadAsync(string sha256, CancellationToken ct)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, Key(sha256), ct);
            return new BlobContent(new OwnedStream(response), response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _s3.Dispose();

    private static string Key(string sha256) => $"objects/{sha256[..2]}/{sha256}";

    /// <summary>Поток ответа S3, при закрытии освобождающий сам ответ.</summary>
    private sealed class OwnedStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
    }
}
