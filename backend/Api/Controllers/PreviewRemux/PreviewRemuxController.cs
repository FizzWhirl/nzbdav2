using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Preview;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.Api.Controllers.PreviewRemux;

[ApiController]
[Route("api/preview/remux")]
public class PreviewRemuxController(DavDatabaseClient dbClient, DatabaseStore store) : ControllerBase
{
    [HttpGet("{davItemId:guid}")]
    public async Task<IActionResult> Handle(Guid davItemId, [FromQuery] int? start = null)
    {
        var item = await dbClient.GetFileById(davItemId.ToString()).ConfigureAwait(false);
        if (item == null)
            return NotFound("File not found.");

        if (!IsPreviewableItemType(item.Type))
            return BadRequest("Preview requires a file DavItemId (not a directory/root item).");

        var startSeconds = Math.Max(0, start ?? 0);
        using var previewProcessSlot = await PreviewProcessLimiter.AcquireAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // Use direct store stream to avoid nested HTTP/proxy edge cases while remuxing.
        HttpContext.Items["PreviewMode"] = true;
        var storeItem = await store.GetItemAsync(item.Path, HttpContext.RequestAborted).ConfigureAwait(false);
        if (storeItem is null || storeItem is IStoreCollection)
            return NotFound("File stream not found.");

        await using var sourceStream = await storeItem.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-fflags", "+genpts"
        };

        arguments.AddRange([
            "-i", "pipe:0",
            // Post-input seek keeps container parsing reliable for streamed sources.
            "-ss", startSeconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            // Force a browser-safe video profile in fallback mode.
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",
            "-profile:v", "high",
            "-level:v", "4.1",
            // AAC gives the browser a much better chance of decoding audio tracks from non-native containers.
            "-c:a", "aac",
            "-profile:a", "aac_low",
            // Keep preview audio stereo for maximum browser/device compatibility.
            "-ac", "2",
            "-ar", "48000",
            "-b:a", "192k",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof+faststart",
            "-f", "mp4",
            "pipe:1"
        ]);

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
            return StatusCode(500, "Failed to start remux process.");

        Log.Debug("[PreviewRemux] Started ffmpeg remux for DavItemId={DavItemId}, start={StartSeconds}s (preview ffmpeg slots max={MaxConcurrent})",
            davItemId, startSeconds, PreviewProcessLimiter.MaxConcurrent);

        var stdinTask = Task.Run(async () =>
        {
            try
            {
                await sourceStream.CopyToAsync(process.StandardInput.BaseStream, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on disconnect/cancel.
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { /* best effort */ }
            }
        });

        var stderrTask = StreamStderrAsync(
            process,
            line => Log.Debug("[PreviewRemux] ffmpeg stderr for DavItemId={DavItemId}: {StderrLine}", davItemId, line),
            HttpContext.RequestAborted);

        using var abortRegistration = HttpContext.RequestAborted.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort on disconnect.
            }
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
                    Response.ContentType = "video/mp4";
                    Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                    Response.Headers["Pragma"] = "no-cache";
                    Response.Headers["Accept-Ranges"] = "none";
                    hadOutput = true;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted).ConfigureAwait(false);
            }

            await stdinTask.ConfigureAwait(false);
            await process.WaitForExitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Log.Warning("[PreviewRemux] ffmpeg exited with code {ExitCode} for DavItemId={DavItemId}", process.ExitCode, davItemId);
                if (!hadOutput)
                    return StatusCode(502, "Failed to generate remux preview.");
            }

            if (!hadOutput)
                return StatusCode(502, "Remux preview generation produced no output.");
        }
        catch (OperationCanceledException)
        {
            // Request canceled/disconnected; kill is handled by registration.
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
            Log.Debug("[PreviewRemux] stderr stream closed early: {Message}", ex.Message);
        }
    }
}
