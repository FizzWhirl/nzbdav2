using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace NzbWebDAV.Logging;

public sealed class DozzleJsonConsoleFormatter : ITextFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var payload = new Dictionary<string, object?>
        {
            ["timestamp"] = logEvent.Timestamp.ToString("O"),
            ["level"] = ToDozzleLevel(logEvent.Level),
            ["source"] = "backend",
            ["message"] = logEvent.RenderMessage()
        };

        if (logEvent.Exception != null)
        {
            payload["exception"] = logEvent.Exception.ToString();
        }

        output.Write(JsonSerializer.Serialize(payload, JsonOptions));
        output.WriteLine();
    }

    private static string ToDozzleLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "trace",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        LogEventLevel.Error => "error",
        LogEventLevel.Fatal => "fatal",
        _ => level.ToString().ToLowerInvariant()
    };
}