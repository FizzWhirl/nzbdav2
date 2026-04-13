// backend/Clients/Usenet/Concurrency/SemaphorePriorityOdds.cs
namespace NzbWebDAV.Clients.Usenet.Concurrency;

/// <summary>
/// Configures the odds of high-priority vs low-priority waiters winning contention.
/// HighPriorityOdds of 80 means streaming wins 80% of the time when both are waiting.
/// </summary>
public class SemaphorePriorityOdds
{
    public int HighPriorityOdds { get; set; } = 100;
    public int LowPriorityOdds => 100 - HighPriorityOdds;
}
