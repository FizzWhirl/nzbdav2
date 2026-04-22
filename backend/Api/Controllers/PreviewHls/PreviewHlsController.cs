using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Preview;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.PreviewHls;

[ApiController]
[Route("api/preview/hls")]
public class PreviewHlsController(DavDatabaseClient dbClient, DatabaseStore store) : ControllerBase
{
    private const int SegmentDurationSeconds = 12;

    [HttpGet("{davItemId:guid}/index.m3u8")]
    public async Task<IActionResult> GetPlaylist(Guid davItemId)
    {
        var item = await dbClient.GetFileById(davItemId.ToString()).ConfigureAwait(false);
        if (item == null)
            return NotFound("File not found.");

        if (!IsPreviewableItemType(item.Type))
            return BadRequest("Preview requires a file DavItemId (not a directory/root item).");

        var duration = ParseDurationSeconds(item.MediaInfo);
        if (duration <= 0)
        {
            // Preview-only lean fallback: probe duration on demand so app preview does
            // not have to wait for full analysis/decode integrity checks.
            duration = await ProbeDurationSecondsAsync(item.Path, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        if (duration <= 0)
            return BadRequest("Media duration is unknown. Preview cannot be generated yet.");

        var segCount = (int)Math.Ceiling(duration / (double)SegmentDurationSeconds);

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{SegmentDurationSeconds}");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

        for (var i = 0; i < segCount; i++)
        {
            var segActualDur = Math.Min(SegmentDurationSeconds, duration - i * SegmentDurationSeconds);
            sb.AppendLine($"#EXTINF:{segActualDur.ToString("F3", CultureInfo.InvariantCulture)},");
            sb.AppendLine($"/api/preview/hls/{davItemId}/segment/{i}.ts");
        }

        sb.AppendLine("#EXT-X-ENDLIST");

        return Content(sb.ToString(), "application/vnd.apple.mpegurl");
    }

    [HttpGet("{davItemId:guid}/segment/{segIndex}.ts")]
    public async Task<IActionResult> GetSegment(Guid davItemId, int segIndex)
    {
        if (segIndex < 0)
            return BadRequest("Invalid segment index.");

        var item = await dbClient.GetFileById(davItemId.ToString()).ConfigureAwait(false);
        if (item == null)
            return NotFound("File not found.");

        if (!IsPreviewableItemType(item.Type))
            return BadRequest("Preview requires a file DavItemId (not a directory/root item).");

        var startSeconds = segIndex * SegmentDurationSeconds;
        var inputUrl = BuildInternalViewUrl(item.Path);
        using var previewProcessSlot = await PreviewProcessLimiter.AcquireAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            // Keep probe windows bounded to reduce per-segment startup latency.
            "-probesize", "8M",
            "-analyzeduration", "8M",
            "-fflags", "+genpts",
            // Use HTTP input so ffmpeg can perform real demux-level seeks via range requests.
            "-ss", startSeconds.ToString(CultureInfo.InvariantCulture),
            "-i", inputUrl,
            "-t", SegmentDurationSeconds.ToString(CultureInfo.InvariantCulture),
            // Shift output PTS to match the playlist timeline position so HLS.js
            // sees monotonically increasing timestamps across segments.
            "-output_ts_offset", startSeconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            // Always transcode for preview HLS. Copy mode seeks to nearest keyframe and
            // can emit >12s segments that overlap adjacent playlist entries, causing
            // apparent backward jumps/replayed chunks in browser playback.
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-pix_fmt", "yuv420p",
            "-profile:v", "high",
            "-level:v", "4.1",
            "-g", "48",
            "-keyint_min", "48",
            "-sc_threshold", "0",
            "-c:a", "aac",
            "-profile:a", "aac_low",
            // Keep preview audio stereo for maximum browser/device compatibility.
            "-ac", "2",
            "-ar", "48000",
            "-b:a", "192k",
            // MPEG-TS output — required for HLS segments
            "-f", "mpegts",
            "pipe:1"
        };

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveExecutablePath("FFMPEG_PATH", "ffmpeg"),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        if (!process.Start())
            return StatusCode(500, "Failed to start ffmpeg process.");

        Log.Debug("[PreviewHls] Segment {SegIndex} (start={StartSeconds}s) for DavItemId={DavItemId} (preview ffmpeg slots max={MaxConcurrent})",
            segIndex, startSeconds, davItemId, PreviewProcessLimiter.MaxConcurrent);

        var stderrTask = StreamStderrAsync(
            process,
            line => Log.Debug("[PreviewHls] ffmpeg stderr for segment {SegIndex} DavItemId={DavItemId}: {StderrLine}", segIndex, davItemId, line),
            HttpContext.RequestAborted);

        using var abortRegistration = HttpContext.RequestAborted.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        });

        try
        {
            var hadOutput = false;
            var buffer = new byte[64 * 1024];

            while (true)
            {
                var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, HttpContext.RequestAborted).ConfigureAwait(false);
                if (bytesRead <= 0)
                    break;

                if (!hadOutput)
                {
                    Response.StatusCode = 200;
                    Response.ContentType = "video/mp2t";
                    Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                    Response.Headers["Pragma"] = "no-cache";
                    // Important: tell HLS.js the segment has bytes (no Accept-Ranges)
                    Response.Headers["Accept-Ranges"] = "none";
                    hadOutput = true;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Log.Warning("[PreviewHls] ffmpeg exited with code {ExitCode} for segment {SegIndex} DavItemId={DavItemId}", process.ExitCode, segIndex, davItemId);
                if (!hadOutput)
                    return StatusCode(502, "Failed to generate preview segment.");
            }

            if (!hadOutput)
                return StatusCode(502, "Preview segment generation produced no output.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
        }
        finally
        {
            process.Dispose();
        }

        return new EmptyResult();
    }

    private static bool IsPreviewableItemType(DavItem.ItemType type)
    {
        return type is DavItem.ItemType.NzbFile
            or DavItem.ItemType.RarFile
            or DavItem.ItemType.MultipartFile;
    }

    private static string BuildInternalViewUrl(string itemPath)
    {
        var normalizedPath = itemPath.TrimStart('/');
        var encodedPath = "/" + string.Join('/', normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var apiKey = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(apiKey, normalizedPath);
        return $"http://127.0.0.1:8080/view{encodedPath}?downloadKey={downloadKey}&preview=true&previewhls=true";
    }

    private static double ParseDurationSeconds(string? mediaInfoJson)
    {
        if (string.IsNullOrWhiteSpace(mediaInfoJson))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(mediaInfoJson);
            var root = doc.RootElement;
            // Try format.duration first (most reliable)
            if (root.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var durEl))
            {
                if (durEl.ValueKind == JsonValueKind.Number && durEl.TryGetDouble(out var d))
                    return d;
                if (durEl.ValueKind == JsonValueKind.String &&
                    double.TryParse(durEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                    return ds;
            }
            // Fall back to first stream with a duration
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("duration", out var sdur)) continue;
                    if (sdur.ValueKind == JsonValueKind.Number && sdur.TryGetDouble(out var sd))
                        return sd;
                    if (sdur.ValueKind == JsonValueKind.String &&
                        double.TryParse(sdur.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sds))
                        return sds;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[PreviewHls] Failed to parse mediaInfo duration: {Message}", ex.Message);
        }
        return 0;
    }

    private static async Task<double> ProbeDurationSecondsAsync(string itemPath, CancellationToken cancellationToken)
    {
        try
        {
            var inputUrl = BuildInternalViewUrl(itemPath);
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

            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-show_entries");
            process.StartInfo.ArgumentList.Add("format=duration");
            process.StartInfo.ArgumentList.Add("-of");
            process.StartInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            process.StartInfo.ArgumentList.Add(inputUrl);

            if (!process.Start())
                return 0;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return 0;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stdout))
                return 0;

            if (double.TryParse(stdout.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var duration))
                return duration;
        }
        catch (Exception ex)
        {
            Log.Debug("[PreviewHls] ProbeDuration failed: {Message}", ex.Message);
        }

        return 0;
    }

    private static string ResolveExecutablePath(string envName, string defaultCommand)
    {
        var configured = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(configured) ? defaultCommand : configured;
    }

    private static async Task StreamStderrAsync(Process process, Action<string> onLine, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;

                if (!string.IsNullOrWhiteSpace(line))
                    onLine(line);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[PreviewHls] stderr stream closed early: {Message}", ex.Message);
        }
    }
}
