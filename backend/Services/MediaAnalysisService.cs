using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public enum MediaAnalysisResult
{
    Success,
    Failed,
    Timeout
}

public class MediaAnalysisService(
    IServiceScopeFactory scopeFactory
)
{
    public async Task<MediaAnalysisResult> AnalyzeMediaAsync(Guid davItemId, CancellationToken ct = default)
    {
        // 1. Get Item
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var item = await dbContext.Items.FindAsync([davItemId], ct).ConfigureAwait(false);
        if (item == null) return MediaAnalysisResult.Failed;

        // 2. Construct URL — use the WebDAV HTTP endpoint with X-Analysis-Mode header.
        // The header tells DatabaseStoreNzbFile to use limited workers (2) instead of full pool (50),
        // preventing OOM while still allowing ffprobe to seek via HTTP range requests (needed for moov atoms).
        var encodedPath = string.Join("/", item.Path.Split('/').Select(Uri.EscapeDataString));
        var webDavUrl = $"http://localhost:8080{encodedPath}";

        // 3. Run ffprobe with analysis mode header
        Log.Information("[MediaAnalysis] Running ffprobe on {Name} ({Id}) via HTTP with analysis mode", item.Name, davItemId);
        var (result, timedOut) = await RunFfprobeAsync(webDavUrl, ct).ConfigureAwait(false);

        // 4. Update DB
        MediaAnalysisResult analysisResult;
        if (timedOut)
        {
             Log.Warning("[MediaAnalysis] ffprobe timed out for {Name}", item.Name);
             analysisResult = MediaAnalysisResult.Timeout;
        }
        else if (string.IsNullOrWhiteSpace(result))
        {
             Log.Warning("[MediaAnalysis] ffprobe failed or returned empty result for {Name}", item.Name);
             item.MediaInfo = "{\"error\": \"ffprobe failed (file may be corrupt or incomplete)\", \"streams\": []}";
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis (ffprobe) failed - possible corrupt file.";
             analysisResult = MediaAnalysisResult.Failed;
        }
        else if (result.Contains("\"error\":"))
        {
             Log.Warning("[MediaAnalysis] ffprobe reported error for {Name}: {Result}", item.Name, result);
             item.MediaInfo = result;
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis reported stream errors.";
             analysisResult = MediaAnalysisResult.Failed;
        }
        else
        {
             item.MediaInfo = result;
             item.IsCorrupted = false;
             item.CorruptionReason = null;
             analysisResult = MediaAnalysisResult.Success;
        }

        if (analysisResult != MediaAnalysisResult.Timeout)
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        Log.Information("[MediaAnalysis] Media analysis complete for {Name}. Result: {Result}", item.Name, analysisResult);
        return analysisResult;
    }

    /// <summary>
    /// Runs ffprobe on the given WebDAV URL with X-Analysis-Mode header to limit resource usage.
    /// </summary>
    private async Task<(string? output, bool timedOut)> RunFfprobeAsync(string url, CancellationToken ct)
    {
        try
        {
            var start = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-headers \"X-Analysis-Mode: true\r\n\" -v quiet -print_format json -show_format -show_streams -probesize 5000000 -analyzeduration 5000000 \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read output
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            // Wait for exit with timeout (90 seconds — allows time for moov atom seeks on large files)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("[MediaAnalysis] ffprobe timed out after 90 seconds for {Url}", url);
                try { process.Kill(); } catch { /* already exited */ }
                return (null, timedOut: true);
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            start.Stop();
            Log.Debug("[MediaAnalysis] ffprobe took {Duration}ms", start.ElapsedMilliseconds);

            if (process.ExitCode != 0)
            {
                Log.Warning("[MediaAnalysis] ffprobe exited with code {Code}: {Error}", process.ExitCode, error);
                return (null, timedOut: false);
            }

            return (output, timedOut: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MediaAnalysis] Failed to run ffprobe");
            return (null, timedOut: false);
        }
    }
}
