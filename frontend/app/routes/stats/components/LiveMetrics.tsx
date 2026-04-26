import { useEffect, useState, useRef, useMemo } from "react";
import { Table, Badge } from "react-bootstrap";
import {
    Area,
    AreaChart,
    CartesianGrid,
    ResponsiveContainer,
    Tooltip,
    XAxis,
    YAxis,
    Legend,
} from "recharts";

/**
 * Parsed Prometheus exposition-format sample.
 */
type Sample = {
    name: string;
    labels: Record<string, string>;
    value: number;
};

/**
 * Time-series point for the connection/throughput chart.
 */
type TrendPoint = {
    t: string;
    active: number;
    live: number;
    max: number;
    speedMBps: number;
    freeSlots: number;
};

type SharedStreamDelta = {
    hits: number;
    misses: number;
    ratioPct: number | null;
};

type ProviderBandwidthSnapshot = {
    providerIndex: number;
    totalBytes: number;
    currentSpeed: number;
    averageLatency: number;
    host?: string;
};

const POLL_MS = 3000;
const HISTORY_LEN = 60; // last 3 minutes at 3s cadence

/**
 * Minimal Prometheus text-format parser. Handles the subset our backend emits:
 *   metric_name{label="value",...} 1.234
 *   metric_name 1.234
 *
 * Skips comment / # HELP / # TYPE lines and bucket-bound float labels are kept
 * verbatim. No support for timestamps or exemplars (we don't emit any).
 */
function parsePrometheus(text: string): Sample[] {
    const out: Sample[] = [];
    for (const rawLine of text.split("\n")) {
        const line = rawLine.trim();
        if (!line || line.startsWith("#")) continue;
        const labelStart = line.indexOf("{");
        let name: string;
        let labels: Record<string, string> = {};
        let valueStr: string;
        if (labelStart === -1) {
            const sp = line.indexOf(" ");
            if (sp === -1) continue;
            name = line.slice(0, sp);
            valueStr = line.slice(sp + 1);
        } else {
            name = line.slice(0, labelStart);
            const labelEnd = line.indexOf("}", labelStart);
            if (labelEnd === -1) continue;
            const labelBody = line.slice(labelStart + 1, labelEnd);
            // simple regex-free split — labels are key="value", separated by ,
            // values may contain escaped commas/quotes, but our backend doesn't emit those.
            for (const pair of labelBody.split(",")) {
                const eq = pair.indexOf("=");
                if (eq === -1) continue;
                const k = pair.slice(0, eq).trim();
                let v = pair.slice(eq + 1).trim();
                if (v.startsWith('"') && v.endsWith('"')) v = v.slice(1, -1);
                labels[k] = v;
            }
            valueStr = line.slice(labelEnd + 1).trim();
        }
        const value = Number(valueStr.split(" ")[0]);
        if (!Number.isFinite(value)) continue;
        out.push({ name, labels, value });
    }
    return out;
}

function findOne(samples: Sample[], name: string, labelMatch?: Record<string, string>): Sample | undefined {
    return samples.find((s) => s.name === name && labelMatches(s.labels, labelMatch));
}
function findAll(samples: Sample[], name: string): Sample[] {
    return samples.filter((s) => s.name === name);
}
function labelMatches(actual: Record<string, string>, expected?: Record<string, string>): boolean {
    if (!expected) return true;
    for (const k of Object.keys(expected)) {
        if (actual[k] !== expected[k]) return false;
    }
    return true;
}

/**
 * Compute approximate quantile from a Prometheus histogram by linear
 * interpolation across the bucket containing the target rank. Returns
 * seconds. Returns null if no observations.
 */
function quantileFromHistogram(samples: Sample[], baseName: string, kindLabel: string, q: number): number | null {
    const buckets = samples
        .filter((s) => s.name === `${baseName}_bucket` && s.labels.kind === kindLabel)
        .map((s) => ({ le: Number(s.labels.le), v: s.value }))
        .filter((b) => Number.isFinite(b.le) || b.le === Infinity)
        .sort((a, b) => a.le - b.le);
    if (buckets.length === 0) return null;
    const total = buckets[buckets.length - 1].v;
    if (total <= 0) return null;
    const target = total * q;
    let prevLe = 0;
    let prevV = 0;
    for (const b of buckets) {
        if (target <= b.v) {
            const range = b.le - prevLe;
            const denom = b.v - prevV;
            if (denom <= 0 || !Number.isFinite(range)) return prevLe;
            return prevLe + ((target - prevV) / denom) * range;
        }
        prevLe = b.le;
        prevV = b.v;
    }
    return prevLe;
}

function fmtSeconds(s: number | null): string {
    if (s == null) return "—";
    if (s < 0.001) return `${(s * 1_000_000).toFixed(0)} µs`;
    if (s < 1) return `${(s * 1000).toFixed(1)} ms`;
    if (s < 60) return `${s.toFixed(2)} s`;
    return `${(s / 60).toFixed(1)} min`;
}

function fmtSpeed(bytesPerSecond: number): string {
    if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) return "0 B/s";
    const units = ["B/s", "KB/s", "MB/s", "GB/s"];
    let value = bytesPerSecond;
    let idx = 0;
    while (value >= 1024 && idx < units.length - 1) {
        value /= 1024;
        idx++;
    }
    const precision = idx <= 1 ? 0 : 1;
    return `${value.toFixed(precision)} ${units[idx]}`;
}

function sumMetric(samples: Sample[], name: string): number {
    return findAll(samples, name).reduce((acc, sample) => acc + sample.value, 0);
}

export function LiveMetrics() {
    const [samples, setSamples] = useState<Sample[]>([]);
    const [bandwidth, setBandwidth] = useState<ProviderBandwidthSnapshot[]>([]);
    const [error, setError] = useState<string | null>(null);
    const [history, setHistory] = useState<TrendPoint[]>([]);
    const [sharedDelta, setSharedDelta] = useState<SharedStreamDelta | null>(null);
    const lastHits = useRef<number>(0);
    const lastMisses = useRef<number>(0);
    const lastTs = useRef<number>(0);

    useEffect(() => {
        let cancelled = false;
        const tick = async () => {
            try {
                const res = await fetch("/metrics", { headers: { Accept: "text/plain" } });
                if (!res.ok) {
                    setError(`HTTP ${res.status}`);
                    return;
                }
                const txt = await res.text();
                let bandwidthSnapshot: ProviderBandwidthSnapshot[] = [];
                try {
                    const bwRes = await fetch("/api/stats/bandwidth/current", { headers: { Accept: "application/json" } });
                    if (bwRes.ok) {
                        bandwidthSnapshot = await bwRes.json() as ProviderBandwidthSnapshot[];
                    }
                } catch {
                    // Keep metrics usable even if the bandwidth endpoint is temporarily unavailable.
                }
                if (cancelled) return;
                const parsed = parsePrometheus(txt);
                setSamples(parsed);
                setBandwidth(bandwidthSnapshot);
                setError(null);

                // Track shared stream reuse deltas for explanatory cards.
                const hits = findOne(parsed, "nzbdav_shared_stream_hits_total")?.value ?? 0;
                const missTotal = findAll(parsed, "nzbdav_shared_stream_misses_total")
                    .reduce((acc, s) => acc + s.value, 0);
                const now = Date.now();
                if (lastTs.current > 0) {
                    const dHits = Math.max(0, hits - lastHits.current);
                    const dMiss = Math.max(0, missTotal - lastMisses.current);
                    const total = dHits + dMiss;
                    const ratioPct = total > 0 ? (dHits / total) * 100 : null;
                    setSharedDelta({
                        hits: dHits,
                        misses: dMiss,
                        ratioPct: ratioPct == null ? null : Number(ratioPct.toFixed(1)),
                    });

                    const speedBytesPerSecond = bandwidthSnapshot.reduce((acc, provider) => acc + (provider.currentSpeed || 0), 0);
                    setHistory((prev) => {
                        const next = [
                            ...prev,
                            {
                                t: new Date(now).toLocaleTimeString(),
                                active: sumMetric(parsed, "nzbdav_pool_active_connections"),
                                live: sumMetric(parsed, "nzbdav_pool_live_connections"),
                                max: sumMetric(parsed, "nzbdav_pool_max_connections"),
                                speedMBps: Number((speedBytesPerSecond / 1024 / 1024).toFixed(2)),
                                freeSlots: sumMetric(parsed, "nzbdav_pool_remaining_semaphore_slots"),
                            },
                        ];
                        return next.slice(-HISTORY_LEN);
                    });
                }
                lastHits.current = hits;
                lastMisses.current = missTotal;
                lastTs.current = now;
            } catch (e: any) {
                if (cancelled) return;
                setError(e?.message ?? String(e));
            }
        };
        void tick();
        const id = setInterval(tick, POLL_MS);
        return () => {
            cancelled = true;
            clearInterval(id);
        };
    }, []);

    // ------------------------------------------------------------------
    // Derived values
    // ------------------------------------------------------------------

    const sharedHits = findOne(samples, "nzbdav_shared_stream_hits_total")?.value ?? 0;
    const sharedMissesByReason = useMemo(
        () =>
            findAll(samples, "nzbdav_shared_stream_misses_total").map((s) => ({
                reason: s.labels.reason || "unknown",
                value: s.value,
            })),
        [samples],
    );
    const sharedMissesTotal = sharedMissesByReason.reduce((acc, m) => acc + m.value, 0);
    const sharedTotal = sharedHits + sharedMissesTotal;
    const overallRatioPct = sharedTotal > 0 ? (sharedHits / sharedTotal) * 100 : null;
    const activeEntries = findOne(samples, "nzbdav_shared_stream_active_entries")?.value ?? 0;
    const activeReaders = findOne(samples, "nzbdav_shared_stream_active_readers")?.value ?? 0;

    // Pool stats — group by `pool` label
    const poolNames = useMemo(() => {
        const set = new Set<string>();
        for (const s of samples) {
            if (s.name.startsWith("nzbdav_pool_") && s.labels.pool) set.add(s.labels.pool);
        }
        return Array.from(set).sort();
    }, [samples]);

    const pools = useMemo(
        () =>
            poolNames.map((pool) => {
                const get = (n: string) => findOne(samples, n, { pool })?.value ?? 0;
                return {
                    pool,
                    live: get("nzbdav_pool_live_connections"),
                    idle: get("nzbdav_pool_idle_connections"),
                    active: get("nzbdav_pool_active_connections"),
                    max: get("nzbdav_pool_max_connections"),
                    remaining: get("nzbdav_pool_remaining_semaphore_slots"),
                    failures: get("nzbdav_pool_consecutive_connection_failures"),
                    tripped: get("nzbdav_pool_circuit_breaker_tripped") > 0,
                };
            }),
        [samples, poolNames],
    );

    const totalActiveConnections = pools.reduce((acc, pool) => acc + pool.active, 0);
    const totalLiveConnections = pools.reduce((acc, pool) => acc + pool.live, 0);
    const totalMaxConnections = pools.reduce((acc, pool) => acc + pool.max, 0);
    const totalFreeSlots = pools.reduce((acc, pool) => acc + pool.remaining, 0);
    const totalSpeed = bandwidth.reduce((acc, provider) => acc + (provider.currentSpeed || 0), 0);
    const poolUtilizationPct = totalMaxConnections > 0 ? (totalActiveConnections / totalMaxConnections) * 100 : null;
    const busiestProvider = [...bandwidth]
        .sort((a, b) => (b.currentSpeed || 0) - (a.currentSpeed || 0))[0];

    const insights = useMemo(() => {
        const items: Array<{ variant: "success" | "info" | "warning" | "danger"; text: string }> = [];

        const trippedPools = pools.filter((pool) => pool.tripped);
        if (trippedPools.length > 0) {
            items.push({
                variant: "danger",
                text: `${trippedPools.length} connection pool${trippedPools.length === 1 ? " is" : "s are"} circuit-breaker tripped: ${trippedPools.map((p) => p.pool).join(", ")}.`,
            });
        }

        const failingPools = pools.filter((pool) => pool.failures > 0 && !pool.tripped);
        if (failingPools.length > 0) {
            items.push({
                variant: "warning",
                text: `${failingPools.length} pool${failingPools.length === 1 ? " has" : "s have"} recent connection failures. Check provider health if this grows.`,
            });
        }

        if (totalMaxConnections === 0) {
            items.push({ variant: "info", text: "No connection-pool metrics yet. Start a stream or queue task to populate this view." });
        } else if (totalActiveConnections === 0) {
            items.push({ variant: "info", text: "No active NNTP reads right now; pools are idle." });
        } else if (poolUtilizationPct != null && poolUtilizationPct >= 85) {
            items.push({ variant: "warning", text: `Connection usage is high (${poolUtilizationPct.toFixed(0)}%). Playback may wait for permits if another stream starts.` });
        } else {
            items.push({ variant: "success", text: `Connection usage is healthy (${poolUtilizationPct?.toFixed(0) ?? "0"}% active, ${Math.round(totalFreeSlots)} free slots).` });
        }

        if (totalSpeed > 0) {
            items.push({ variant: "info", text: `Current measured provider throughput is ${fmtSpeed(totalSpeed)}${busiestProvider ? `; busiest provider is ${busiestProvider.host || `Provider ${busiestProvider.providerIndex}`} at ${fmtSpeed(busiestProvider.currentSpeed)}.` : "."}` });
        }

        if (sharedDelta && sharedDelta.hits + sharedDelta.misses > 0) {
            items.push({ variant: "info", text: `Shared-stream reuse in the last sample: ${sharedDelta.hits} hit${sharedDelta.hits === 1 ? "" : "s"}, ${sharedDelta.misses} new stream${sharedDelta.misses === 1 ? "" : "s"}.` });
        }

        return items.slice(0, 5);
    }, [pools, totalMaxConnections, totalActiveConnections, poolUtilizationPct, totalFreeSlots, totalSpeed, busiestProvider, sharedDelta]);

    // Seek latency quantiles per kind
    const seekKinds = useMemo(() => {
        const set = new Set<string>();
        for (const s of samples) {
            if (s.name === "nzbdav_seek_latency_seconds_bucket" && s.labels.kind) set.add(s.labels.kind);
        }
        return Array.from(set).sort();
    }, [samples]);
    const seekStats = useMemo(
        () =>
            seekKinds.map((kind) => {
                const total =
                    findOne(samples, "nzbdav_seek_latency_seconds_count", { kind })?.value ?? 0;
                return {
                    kind,
                    count: total,
                    p50: quantileFromHistogram(samples, "nzbdav_seek_latency_seconds", kind, 0.5),
                    p95: quantileFromHistogram(samples, "nzbdav_seek_latency_seconds", kind, 0.95),
                    p99: quantileFromHistogram(samples, "nzbdav_seek_latency_seconds", kind, 0.99),
                };
            }),
        [samples, seekKinds],
    );

    return (
        <div>
            {error && (
                <div className="p-4 rounded-lg bg-danger bg-opacity-25 mb-4">
                    <strong>Failed to load /metrics:</strong> {error}
                </div>
            )}

            {/* ------------- Overview / Insights ------------- */}
            <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
                <div className="d-flex justify-content-between align-items-center mb-3">
                    <h4 className="m-0">Live Streaming Insights</h4>
                    <Badge bg="secondary">refresh: {POLL_MS / 1000}s</Badge>
                </div>
                <div className="row g-3 mb-3">
                    <StatCard
                        label="Current provider speed"
                        value={fmtSpeed(totalSpeed)}
                        sub={busiestProvider ? `Top: ${busiestProvider.host || `Provider ${busiestProvider.providerIndex}`} (${fmtSpeed(busiestProvider.currentSpeed)})` : "No bandwidth samples yet"}
                    />
                    <StatCard
                        label="Active / live connections"
                        value={`${Math.round(totalActiveConnections)} / ${Math.round(totalLiveConnections)}`}
                        sub={totalMaxConnections > 0 ? `${poolUtilizationPct?.toFixed(0) ?? "0"}% of ${Math.round(totalMaxConnections)} max active` : "No pools registered"}
                    />
                    <StatCard
                        label="Free connection slots"
                        value={Math.round(totalFreeSlots).toLocaleString()}
                        sub="Before new NNTP operations queue for permits"
                    />
                </div>

                {insights.length > 0 && (
                    <div className="d-flex flex-column gap-2 mb-3">
                        {insights.map((insight, index) => (
                            <div key={`${insight.variant}-${index}`} className={`border-start border-4 border-${insight.variant} ps-3 py-1 bg-black bg-opacity-25 rounded-end`}>
                                {insight.text}
                            </div>
                        ))}
                    </div>
                )}

                <h6 className="text-secondary mb-2">Connections and provider speed over time</h6>
                <div className="text-secondary mb-2" style={{ fontSize: "0.85rem" }}>
                    Active/live connections come from pool gauges. Speed comes from the provider bandwidth sampler and is summed across providers.
                </div>

                <div style={{ height: 220 }}>
                    <ResponsiveContainer width="100%" height="100%">
                        <AreaChart data={history} margin={{ top: 8, right: 16, bottom: 4, left: 0 }}>
                            <CartesianGrid strokeDasharray="3 3" stroke="#444" />
                            <XAxis dataKey="t" stroke="#aaa" tick={{ fontSize: 11 }} />
                            <YAxis yAxisId="connections" stroke="#aaa" tick={{ fontSize: 11 }} allowDecimals={false} />
                            <YAxis yAxisId="speed" orientation="right" stroke="#aaa" tick={{ fontSize: 11 }} tickFormatter={(v) => `${v} MB/s`} />
                            <Tooltip contentStyle={{ background: "#222", border: "1px solid #444" }} />
                            <Legend />
                            <Area
                                yAxisId="connections"
                                type="monotone"
                                dataKey="active"
                                name="Active connections"
                                stroke="#82ca9d"
                                fill="#82ca9d"
                                fillOpacity={0.3}
                            />
                            <Area
                                yAxisId="connections"
                                type="monotone"
                                dataKey="live"
                                name="Live connections"
                                stroke="#8884d8"
                                fill="#8884d8"
                                fillOpacity={0.18}
                            />
                            <Area
                                yAxisId="speed"
                                type="monotone"
                                dataKey="speedMBps"
                                name="Speed MB/s"
                                stroke="#ffc658"
                                fill="#ffc658"
                                fillOpacity={0.22}
                            />
                        </AreaChart>
                    </ResponsiveContainer>
                </div>
            </div>

            {/* ------------- Shared stream reuse ------------- */}
            <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
                <h4 className="m-0 mb-2">Streaming Reuse</h4>
                <div className="text-secondary mb-3" style={{ fontSize: "0.9rem" }}>
                    Shared streams are reusable buffered pumps for the same WebDAV item. They are mainly useful when a client reconnects, seeks near the current buffer, or overlaps direct-play/transcode readers. It is normal for this section to stay quiet during simple single-client playback.
                </div>
                <div className="row g-3 mb-3">
                    <StatCard label="Active shared streams" value={Math.round(activeEntries).toLocaleString()} sub={`${Math.round(activeReaders).toLocaleString()} attached reader${Math.round(activeReaders) === 1 ? "" : "s"}`} />
                    <StatCard
                        label="Reuse ratio (cumulative)"
                        value={overallRatioPct == null ? "—" : `${overallRatioPct.toFixed(1)} %`}
                        sub={`${sharedHits.toLocaleString()} reused / ${sharedMissesTotal.toLocaleString()} new`}
                    />
                    <StatCard
                        label="Reuse in last sample"
                        value={sharedDelta?.ratioPct == null ? "No events" : `${sharedDelta.ratioPct.toFixed(1)} %`}
                        sub={sharedDelta ? `${sharedDelta.hits} reused / ${sharedDelta.misses} new` : "Waiting for the next sample"}
                    />
                </div>

                {sharedMissesByReason.length > 0 && (
                    <div className="mt-3">
                        <strong className="d-block mb-2">Misses by reason</strong>
                        <Table variant="dark" striped size="sm" className="mb-0">
                            <thead>
                                <tr>
                                    <th>Reason</th>
                                    <th className="text-end">Count</th>
                                </tr>
                            </thead>
                            <tbody>
                                {sharedMissesByReason
                                    .slice()
                                    .sort((a, b) => b.value - a.value)
                                    .map((m) => (
                                        <tr key={m.reason}>
                                            <td>{m.reason}</td>
                                            <td className="text-end">
                                                {Math.round(m.value).toLocaleString()}
                                            </td>
                                        </tr>
                                    ))}
                            </tbody>
                        </Table>
                    </div>
                )}
            </div>

            {/* ------------- Connection pools ------------- */}
            <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
                <h4 className="m-0 mb-3">Connection Pools</h4>
                {pools.length === 0 ? (
                    <em>No pool metrics yet.</em>
                ) : (
                    <Table variant="dark" striped hover size="sm" className="mb-0">
                        <thead>
                            <tr>
                                <th>Pool</th>
                                <th className="text-end">Active</th>
                                <th className="text-end">Idle</th>
                                <th className="text-end">Live / Max</th>
                                <th className="text-end">Free slots</th>
                                <th className="text-end">Consec. fails</th>
                                <th>Circuit breaker</th>
                            </tr>
                        </thead>
                        <tbody>
                            {pools.map((p) => (
                                <tr key={p.pool}>
                                    <td><code>{p.pool}</code></td>
                                    <td className="text-end">{p.active}</td>
                                    <td className="text-end">{p.idle}</td>
                                    <td className="text-end">
                                        {p.live} / {p.max}
                                    </td>
                                    <td className="text-end">{p.remaining}</td>
                                    <td className="text-end">
                                        {p.failures > 0 ? (
                                            <Badge bg={p.failures > 5 ? "danger" : "warning"}>
                                                {p.failures}
                                            </Badge>
                                        ) : (
                                            "0"
                                        )}
                                    </td>
                                    <td>
                                        {p.tripped ? (
                                            <Badge bg="danger">TRIPPED</Badge>
                                        ) : (
                                            <Badge bg="success">closed</Badge>
                                        )}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </Table>
                )}
            </div>

            {/* ------------- Seek latency ------------- */}
            <div className="p-4 rounded-lg bg-black bg-opacity-20 mb-4">
                <h4 className="m-0 mb-3">Seek Latency</h4>
                <div className="text-secondary mb-2" style={{ fontSize: "0.85rem" }}>
                    <code>cold</code> = the previous inner stream had to be discarded (will need a refetch).&nbsp;
                    <code>warm</code> = position landed inside the current buffer.&nbsp;
                    <code>noop</code> = seek to current position (no work).&nbsp;
                    <code>fresh</code> = first seek before any inner stream was opened.
                </div>
                {seekStats.length === 0 ? (
                    <em>No seek samples yet.</em>
                ) : (
                    <Table variant="dark" striped hover size="sm" className="mb-0">
                        <thead>
                            <tr>
                                <th>Kind</th>
                                <th className="text-end">Count</th>
                                <th className="text-end">p50</th>
                                <th className="text-end">p95</th>
                                <th className="text-end">p99</th>
                            </tr>
                        </thead>
                        <tbody>
                            {seekStats.map((s) => (
                                <tr key={s.kind}>
                                    <td><code>{s.kind}</code></td>
                                    <td className="text-end">{Math.round(s.count).toLocaleString()}</td>
                                    <td className="text-end">{fmtSeconds(s.p50)}</td>
                                    <td className="text-end">{fmtSeconds(s.p95)}</td>
                                    <td className="text-end">{fmtSeconds(s.p99)}</td>
                                </tr>
                            ))}
                        </tbody>
                    </Table>
                )}
            </div>

            <div className="text-secondary" style={{ fontSize: "0.8rem" }}>
                Raw exposition format also available at{" "}
                <a href="/metrics" target="_blank" rel="noreferrer">
                    /metrics
                </a>
                .
            </div>
        </div>
    );
}

function StatCard({ label, value, sub }: { label: string; value: string; sub?: string }) {
    return (
        <div className="col-12 col-md-4">
            <div className="p-3 rounded-lg bg-black bg-opacity-50 h-100">
                <div className="text-secondary" style={{ fontSize: "0.8rem" }}>
                    {label}
                </div>
                <div style={{ fontSize: "1.75rem", fontWeight: 600 }}>{value}</div>
                {sub && (
                    <div className="text-secondary" style={{ fontSize: "0.8rem" }}>
                        {sub}
                    </div>
                )}
            </div>
        </div>
    );
}
