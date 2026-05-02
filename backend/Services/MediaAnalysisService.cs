using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
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

        // 2. Construct URL for ffprobe (still needed for metadata)
        var encodedPath = string.Join("/", item.Path.Split('/').Select(Uri.EscapeDataString));
        var webDavUrl = $"http://localhost:8080{encodedPath}";

        // 3. Run ffprobe with analysis mode header (retry once on failure for transient issues)
        var analysisRunId = $"{davItemId:N}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        Log.Information("[MediaAnalysis] Running ffprobe on {Name} ({Id}) via HTTP with analysis mode (RunId={RunId})", item.Name, davItemId, analysisRunId);
        var (result, timedOut) = await RunFfprobeAsync(webDavUrl, analysisRunId, "ffprobe-metadata-attempt1", ct).ConfigureAwait(false);

        if (!timedOut && string.IsNullOrWhiteSpace(result))
        {
            Log.Warning("[MediaAnalysis] ffprobe failed for {Name} — retrying in 3s (transient provider issue?)", item.Name);
            await Task.Delay(3000, ct).ConfigureAwait(false);
            (result, timedOut) = await RunFfprobeAsync(webDavUrl, analysisRunId, "ffprobe-metadata-attempt2", ct).ConfigureAwait(false);
        }

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
             // Decode-integrity sampling at 10%/90% has been removed — ffprobe metadata is the only check.
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
    private async Task<(string? output, bool timedOut)> RunFfprobeAsync(string url, string analysisRunId, string analysisPass, CancellationToken ct)
    {
        try
        {
            var headers = BuildAnalysisHeaders(analysisRunId, analysisPass);
            var start = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveExecutablePath("FFPROBE_PATH", "ffprobe"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-headers");
            process.StartInfo.ArgumentList.Add(headers);
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-print_format");
            process.StartInfo.ArgumentList.Add("json");
            process.StartInfo.ArgumentList.Add("-show_format");
            process.StartInfo.ArgumentList.Add("-show_streams");
            process.StartInfo.ArgumentList.Add("-probesize");
            process.StartInfo.ArgumentList.Add("5000000");
            process.StartInfo.ArgumentList.Add("-analyzeduration");
            process.StartInfo.ArgumentList.Add("5000000");
            process.StartInfo.ArgumentList.Add(url);

            process.Start();

            // Read output
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            // Wait for exit with timeout (90 seconds)
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

            // ffprobe can exit 0 while printing real errors to stderr (e.g. 401 Unauthorized, corrupt streams)
            if (!string.IsNullOrWhiteSpace(error))
            {
                Log.Warning("[MediaAnalysis] ffprobe exited 0 but had stderr: {Error}", error.Trim());
                // Still return the output if we got valid JSON — stderr warnings don't always mean failure
                // But if output is empty/invalid, treat as failure
                if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("{"))
                {
                    return (null, timedOut: false);
                }
            }

            return (output, timedOut: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MediaAnalysis] Failed to run ffprobe");
            return (null, timedOut: false);
        }
    }

    private static string BuildAnalysisHeaders(string analysisRunId, string analysisPass)
    {
        var token = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
        return $"X-Analysis-Mode: true\r\nX-Internal-Analysis-Auth: {token}\r\nX-Analysis-Run-Id: {analysisRunId}\r\nX-Analysis-Pass: {analysisPass}\r\n";
    }

    private static string ResolveExecutablePath(string envName, string defaultCommand)
    {
        var configured = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(configured) ? defaultCommand : configured;
    }
}
