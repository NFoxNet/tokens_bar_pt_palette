using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TokensLimitsExtension.Core.Providers;

/// <summary>Result of a bounded external provider CLI invocation.</summary>
public sealed record UsageProviderProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs provider CLIs with a deadline, bounded stdout/stderr and process-tree cleanup.
/// Keeping this boundary injectable lets provider parsing tests stay deterministic.
/// </summary>
public interface IUsageProviderProcessRunner
{
    Task<UsageProviderProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class BoundedUsageProviderProcessRunner : IUsageProviderProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    public const int DefaultMaxOutputCharacters = 262_144;

    private readonly TimeSpan _timeout;
    private readonly int _maxOutputCharacters;

    public BoundedUsageProviderProcessRunner(
        TimeSpan? timeout = null,
        int maxOutputCharacters = DefaultMaxOutputCharacters)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputCharacters);
        _maxOutputCharacters = maxOutputCharacters;
    }

    public async Task<UsageProviderProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new UsageProviderConfigurationException($"Не удалось запустить {fileName}.");
            }
        }
        catch (Win32Exception)
        {
            throw new UsageProviderConfigurationException(
                $"Не найден {fileName}. Установите CLI или укажите путь к нему в настройках.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        var token = timeoutCts.Token;
        using var cancellationRegistration = token.Register(() => KillProcessTree(process));
        var standardOutput = ReadBoundedOutputAsync(process.StandardOutput, token);
        var standardError = ReadBoundedOutputAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            return new UsageProviderProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
        }
        catch
        {
            KillProcessTree(process);
            try { await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task<string> ReadBoundedOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToString();
            }

            if (output.Length > _maxOutputCharacters - read)
            {
                throw new UsageProviderRequestException("Provider CLI output exceeds the safe size limit.");
            }

            output.Append(buffer, 0, read);
        }
    }
}
