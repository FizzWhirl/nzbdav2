using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// StreamingConnectionLimiter's constructor claims a process-wide static instance, and
/// BufferedSegmentStream reads it directly. Any test that installs one changes the behaviour of
/// every other test that builds a BufferedSegmentStream, so those classes must not run in parallel.
/// </summary>
[CollectionDefinition(BufferedStreamCollection.Name, DisableParallelization = true)]
public class BufferedStreamCollection
{
    public const string Name = "BufferedSegmentStream";
}
