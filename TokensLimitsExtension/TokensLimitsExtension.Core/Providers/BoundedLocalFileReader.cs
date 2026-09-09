using System.Buffers;
using System.Text;

namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Reads provider-owned local text files with a hard byte budget. The reader is
/// intentionally opt-in at each call site so unrelated settings files keep their
/// existing behavior.
/// </summary>
internal static class BoundedLocalFileReader
{
    public const int DefaultMaxBytes = 1024 * 1024;

    public static async Task<string> ReadTextAsync(
        string path,
        CancellationToken cancellationToken,
        int maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists && fileInfo.Length > maxBytes)
        {
            throw CreateLimitException(maxBytes);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream(capacity: Math.Min(maxBytes, checked((int)Math.Max(0, stream.Length))));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (bytes.Length > maxBytes - read)
                {
                    throw CreateLimitException(maxBytes);
                }

                await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static UsageProviderRequestException CreateLimitException(int maxBytes)
        => new(
            $"Local provider file exceeds the maximum size of {maxBytes} bytes.",
            failureKind: UsageProviderFailureKind.UnsupportedResponse);
}
