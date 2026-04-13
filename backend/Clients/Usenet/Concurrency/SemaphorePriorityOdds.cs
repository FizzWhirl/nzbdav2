// backend/Clients/Usenet/Concurrency/SemaphorePriorityOdds.cs
namespace NzbWebDAV.Clients.Usenet.Concurrency;

/// <summary>
/// Configures the odds of high-priority vs low-priority waiters winning contention.
/// HighPriorityOdds of 80 means streaming wins 80% of the time when both are waiting.
/// </summary>
public class SemaphorePriorityOdds
{
    private int _highPriorityOdds = 100;
    public int HighPriorityOdds
    {
        get => _highPriorityOdds;
        set => _highPriorityOdds = Math.Clamp(value, 1, 99);
    }
    public int LowPriorityOdds => 100 - HighPriorityOdds;
}
