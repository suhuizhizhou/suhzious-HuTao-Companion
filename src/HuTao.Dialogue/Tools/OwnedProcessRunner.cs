using System.Diagnostics;
using System.Text;

namespace HuTao.Dialogue.Tools;

public sealed record OwnedProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated);

/// <summary>Drains bounded output while owning only the process it starts and that process's children.</summary>
public static class OwnedProcessRunner
{
    public static async Task<OwnedProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout,
        int maxOutputCharacters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new IOException("process_start_failed");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var drains = new CancellationTokenSource();
        var stdout = DrainAsync(process.StandardOutput, maxOutputCharacters, drains.Token);
        var stderr = DrainAsync(process.StandardError, maxOutputCharacters, drains.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // A child can inherit the handles; keep the same deadline while draining both streams.
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, stdout.Result.Text, stderr.Result.Text, stdout.Result.Truncated || stderr.Result.Truncated);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            throw;
        }
        finally
        {
            drains.Cancel();
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            var kept = Math.Min(count, Math.Max(0, limit - text.Length));
            text.Append(buffer, 0, kept);
            truncated |= kept < count;
        }
        return (text.ToString(), truncated);
    }
}
