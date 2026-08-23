using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private readonly string _providerName;
    private readonly object _lock = new();

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private TimeSpan _currentCooldown = InitialCooldown;

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            var trippedUntil = Volatile.Read(ref _trippedUntilMs);
            if (trippedUntil == 0) return false;
            return Environment.TickCount64 < trippedUntil;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _currentCooldown = InitialCooldown;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures < FailureThreshold) return;

            // Capture whether the provider was already tripped before extending the
            // cooldown. While tripped, further failures still extend the skip window
            // (backoff behavior is unchanged), but we must not re-emit the warning:
            // a single outage would otherwise log one "tripped" line per failed
            // request. The warning is emitted only on the transition into the
            // tripped state (or on a re-trip after the cooldown expires).
            var wasTripped = IsTripped;

            _trippedUntilMs = Environment.TickCount64 + (long)_currentCooldown.TotalMilliseconds;

            if (!wasTripped)
            {
                Log.Warning(
                    "Provider {Provider} tripped after {Failures} consecutive failures. " +
                    "Skipping for {Cooldown}s.",
                    _providerName, _consecutiveFailures, _currentCooldown.TotalSeconds);
            }

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
        }
    }
}
