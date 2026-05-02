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
            ["severity"] = logEvent.Level.ToString(),
            ["message"] = logEvent.RenderMessage(),
            ["messageTemplate"] = logEvent.MessageTemplate.Text
        };

        if (logEvent.Exception != null)
        {
            payload["exception"] = logEvent.Exception.ToString();
        }

        if (logEvent.Properties.Count > 0)
        {
            payload["properties"] = logEvent.Properties.ToDictionary(
                property => property.Key,
                property => Simplify(property.Value));
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

    private static object? Simplify(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => SimplifyScalar(scalar.Value),
        SequenceValue sequence => sequence.Elements.Select(Simplify).ToArray(),
        StructureValue structure => structure.Properties.ToDictionary(
            property => property.Name,
            property => Simplify(property.Value)),
        DictionaryValue dictionary => dictionary.Elements.ToDictionary(
            element => element.Key.Value?.ToString() ?? string.Empty,
            element => Simplify(element.Value)),
        _ => value.ToString()
    };

    private static object? SimplifyScalar(object? value) => value switch
    {
        null => null,
        string => value,
        bool => value,
        byte => value,
        sbyte => value,
        short => value,
        ushort => value,
        int => value,
        uint => value,
        long => value,
        ulong => value,
        float => value,
        double => value,
        decimal => value,
        DateTime => value,
        DateTimeOffset => value,
        Guid => value,
        _ => value.ToString()
    };
}