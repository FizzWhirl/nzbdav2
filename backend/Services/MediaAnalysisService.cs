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
             // Detect moov-at-end MP4/MOV layout so the BufferedSegmentStream graceful-degradation
             // tier resolver can hard-cap these as Unknown (cap=0) — losing any segment in a
             // moov-at-end file risks losing the moov box, which makes the whole file unplayable.
             // We mutate the ffprobe JSON in place to add a top-level "__nzbdav_mp4_layout" field
             // (no DB migration required). This is a no-op for non-MP4 containers.
             try
             {
                 var augmented = await TryAddMp4LayoutAnnotationAsync(result, webDavUrl, analysisRunId, ct).ConfigureAwait(false);
                 if (augmented != null) result = augmented;
             }
             catch (Exception probeEx)
             {
                 Log.Debug(probeEx, "[MediaAnalysis] MP4 layout probe failed for {Name}; treating as faststart by default.", item.Name);
             }

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

    /// <summary>
    /// Static HttpClient for the MP4 layout probe. Reused across calls (HttpClient is designed
    /// for reuse). Short timeout because the probe must not block the analysis pipeline.
    /// </summary>
    private static readonly HttpClient s_layoutProbeClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// If <paramref name="ffprobeJson"/> describes an MP4 / MOV file, fetches the first
    /// 512 bytes via a Range request to <paramref name="webDavUrl"/>, parses the MP4 box
    /// atoms, and returns a copy of the JSON with an extra top-level field:
    ///   "__nzbdav_mp4_layout": "faststart" | "moov-at-end" | "unknown"
    /// Returns null for non-MP4 containers or if anything goes wrong (caller keeps the
    /// original JSON unchanged).
    /// </summary>
    private static async Task<string?> TryAddMp4LayoutAnnotationAsync(string ffprobeJson, string webDavUrl, string analysisRunId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffprobeJson)) return null;

        // Fast path: check format_name without full JSON parse.
        var lower = ffprobeJson.ToLowerInvariant();
        var fmtIdx = lower.IndexOf("\"format_name\"", StringComparison.Ordinal);
        if (fmtIdx < 0) return null;
        var colonIdx = lower.IndexOf(':', fmtIdx);
        var quoteEnd = colonIdx > 0 ? lower.IndexOf('"', colonIdx + 2) : -1;
        var quoteEndEnd = quoteEnd > 0 ? lower.IndexOf('"', quoteEnd + 1) : -1;
        if (quoteEnd < 0 || quoteEndEnd < 0) return null;
        var formatName = lower.Substring(quoteEnd + 1, quoteEndEnd - quoteEnd - 1);
        var isMp4Like = formatName.Contains("mp4") || formatName.Contains("mov")
                        || formatName.Contains("m4a") || formatName.Contains("3gp");
        if (!isMp4Like) return null;

        // Fetch the first 512 bytes — enough to clear the ftyp box (typically 24-32 bytes)
        // and inspect the type of the second top-level box. We keep it small so this never
        // turns into a full-file read on a sparse/streaming backend.
        byte[]? headerBytes = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, webDavUrl);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 511);
            req.Headers.TryAddWithoutValidation("X-Analysis-Mode", "true");
            req.Headers.TryAddWithoutValidation("X-Internal-Analysis-Auth", EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"));
            req.Headers.TryAddWithoutValidation("X-Analysis-Run-Id", analysisRunId);
            req.Headers.TryAddWithoutValidation("X-Analysis-Pass", "mp4-layout-probe");
            using var resp = await s_layoutProbeClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            headerBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (headerBytes == null || headerBytes.Length < 16) return null;

        // Walk top-level MP4 boxes: [size:4 BE][type:4 ASCII][...]. Sizes ==0 mean
        // "extends to end of file" (not relevant here); ==1 means 64-bit size in next 8 bytes.
        // We only care which top-level box appears AFTER the initial ftyp.
        string layout = "unknown";
        try
        {
            int offset = 0;
            int boxesSeen = 0;
            while (offset + 8 <= headerBytes.Length && boxesSeen < 8)
            {
                long size = ((long)headerBytes[offset] << 24) | ((long)headerBytes[offset + 1] << 16)
                            | ((long)headerBytes[offset + 2] << 8) | headerBytes[offset + 3];
                string type = System.Text.Encoding.ASCII.GetString(headerBytes, offset + 4, 4);
                int headerLen = 8;
                if (size == 1)
                {
                    if (offset + 16 > headerBytes.Length) break;
                    size = ((long)headerBytes[offset + 8] << 56) | ((long)headerBytes[offset + 9] << 48)
                           | ((long)headerBytes[offset + 10] << 40) | ((long)headerBytes[offset + 11] << 32)
                           | ((long)headerBytes[offset + 12] << 24) | ((long)headerBytes[offset + 13] << 16)
                           | ((long)headerBytes[offset + 14] << 8) | headerBytes[offset + 15];
                    headerLen = 16;
                }

                if (boxesSeen == 0 && type != "ftyp")
                {
                    // Not an MP4 family file at all (or the first box isn't ftyp); leave layout=unknown.
                    break;
                }

                if (boxesSeen >= 1)
                {
                    // The box after ftyp tells us the layout.
                    if (type == "moov") { layout = "faststart"; break; }
                    if (type == "moof") { layout = "fragmented"; break; }
                    if (type == "mdat") { layout = "moov-at-end"; break; }
                    if (type == "free" || type == "skip" || type == "wide")
                    {
                        // Padding box — keep walking.
                    }
                    else
                    {
                        // Unknown intermediate box — don't guess.
                        break;
                    }
                }

                if (size <= 0 || size > headerBytes.Length - offset) break;
                offset += (int)size;
                boxesSeen++;
            }
        }
        catch
        {
            return null;
        }

        // Inject "__nzbdav_mp4_layout" as a top-level field. Use JsonNode to preserve
        // the rest of the document exactly as ffprobe emitted it.
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(ffprobeJson);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["__nzbdav_mp4_layout"] = layout;
                Log.Information("[MediaAnalysis] MP4 layout probe: {Url} → {Layout}", webDavUrl, layout);
                return obj.ToJsonString();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[MediaAnalysis] Failed to inject __nzbdav_mp4_layout into ffprobe JSON.");
        }
        return null;
    }
}
