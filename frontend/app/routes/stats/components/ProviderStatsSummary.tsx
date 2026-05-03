import { Card, ProgressBar, Table } from "react-bootstrap";
import type { BandwidthSample, ProviderBandwidthSnapshot } from "~/types/bandwidth";
import type { ProviderStatsResponse } from "~/types/provider-stats";

interface Props {
    providerStats: ProviderStatsResponse | null;
    bandwidthHistory: BandwidthSample[];
    currentBandwidth: ProviderBandwidthSnapshot[];
    range: string;
}

function formatBytes(bytes: number) {
    if (bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    const value = bytes / Math.pow(1024, index);
    return `${value.toFixed(index >= 2 ? 1 : 0)} ${units[index]}`;
}

function formatSpeed(bytesPerSecond: number) {
    if (bytesPerSecond <= 0) return "0 B/s";
    const units = ["B/s", "KB/s", "MB/s", "GB/s"];
    const index = Math.min(Math.floor(Math.log(bytesPerSecond) / Math.log(1024)), units.length - 1);
    const value = bytesPerSecond / Math.pow(1024, index);
    return `${value.toFixed(index >= 2 ? 1 : 0)} ${units[index]}`;
}

function formatNumber(value: number) {
    return value.toLocaleString();
}

function rangeLabel(range: string) {
    switch (range) {
        case "1h": return "1 hour";
        case "24h": return "24 hours";
        case "30d": return "30 days";
        case "all": return "all time";
        default: return range;
    }
}

export function ProviderStatsSummary({ providerStats, bandwidthHistory, currentBandwidth, range }: Props) {
    const bytesByProvider = bandwidthHistory.reduce((acc, sample) => {
        acc[sample.providerIndex] = (acc[sample.providerIndex] || 0) + sample.bytes;
        return acc;
    }, {} as Record<number, number>);

    const statsByProvider = new Map((providerStats?.providers || []).map(provider => [provider.providerIndex, provider]));
    const currentByProvider = new Map(currentBandwidth.map(provider => [provider.providerIndex, provider]));
    const providerIndexes = Array.from(new Set([
        ...Object.keys(bytesByProvider).map(Number),
        ...Array.from(statsByProvider.keys()),
        ...Array.from(currentByProvider.keys()),
    ])).sort((a, b) => {
        const bytesDiff = (bytesByProvider[b] || 0) - (bytesByProvider[a] || 0);
        if (bytesDiff !== 0) return bytesDiff;
        return a - b;
    });

    const totalBytes = Object.values(bytesByProvider).reduce((sum, bytes) => sum + bytes, 0);
    const maxBytes = Math.max(0, ...providerIndexes.map(index => bytesByProvider[index] || 0));

    const getProviderName = (index: number) => {
        const stats = statsByProvider.get(index);
        const current = currentByProvider.get(index);
        return stats?.providerHost || current?.host || `Provider ${index + 1}`;
    };

    return (
        <Card bg="dark" text="white" className="border-secondary mb-4">
            <Card.Body>
                <div className="d-flex flex-wrap justify-content-between align-items-start gap-3 mb-3">
                    <div>
                        <h4 className="m-0">Provider Stats & Bandwidth</h4>
                        <div className="text-muted small">Showing {rangeLabel(range)}</div>
                    </div>
                </div>

                <div className="d-flex flex-wrap gap-4 mb-3 text-muted small">
                    <div><span className="text-light fw-semibold">{formatBytes(totalBytes)}</span> downloaded</div>
                    <div><span className="text-light fw-semibold">{formatNumber(providerStats?.totalOperations || 0)}</span> segment operations</div>
                </div>

                <div className="table-responsive">
                    <Table variant="dark" hover className="mb-0 align-middle">
                        <thead>
                            <tr>
                                <th>Provider</th>
                                <th className="text-end">Downloaded</th>
                                <th className="text-end">Current</th>
                                <th className="text-end">Segments</th>
                                <th className="text-end">Success</th>
                                <th className="text-end">Avg speed</th>
                            </tr>
                        </thead>
                        <tbody>
                            {providerIndexes.length === 0 ? (
                                <tr>
                                    <td colSpan={6} className="text-center text-muted py-4">No provider stats for this period</td>
                                </tr>
                            ) : providerIndexes.map(index => {
                                const stats = statsByProvider.get(index);
                                const current = currentByProvider.get(index);
                                const bytes = bytesByProvider[index] || 0;
                                const successful = stats?.operationCounts?.BODY || 0;
                                const failed = stats?.operationCounts?.BODY_FAIL || 0;
                                const operations = stats?.totalOperations || successful + failed;
                                const successRate = operations > 0 ? (successful / operations) * 100 : 0;
                                const bandwidthPercent = totalBytes > 0 ? (bytes / totalBytes) * 100 : 0;

                                return (
                                    <tr key={index}>
                                        <td style={{ minWidth: 220 }}>
                                            <div className="fw-semibold text-truncate" title={getProviderName(index)}>
                                                {getProviderName(index).replace(/^news\.|^bonus\./, "")}
                                            </div>
                                            <div className="text-muted small">{stats?.providerType || "Provider"}</div>
                                        </td>
                                        <td className="text-end" style={{ minWidth: 180 }}>
                                            <div className="fw-semibold text-info">{formatBytes(bytes)}</div>
                                            <ProgressBar
                                                now={maxBytes > 0 ? (bytes / maxBytes) * 100 : 0}
                                                style={{ height: 6, backgroundColor: "rgba(255,255,255,0.1)" }}
                                                className="mt-1 bg-transparent"
                                            />
                                            <div className="text-muted small">{bandwidthPercent.toFixed(1)}%</div>
                                        </td>
                                        <td className="text-end text-info">{formatSpeed(current?.currentSpeed || 0)}</td>
                                        <td className="text-end">{formatNumber(operations)}</td>
                                        <td className="text-end">
                                            <span className={successRate >= 95 ? "text-success" : successRate >= 80 ? "text-warning" : "text-danger"}>
                                                {operations > 0 ? `${successRate.toFixed(1)}%` : "—"}
                                            </span>
                                        </td>
                                        <td className="text-end">{stats ? `${stats.averageSpeedMbps.toFixed(1)} MB/s` : "—"}</td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </Table>
                </div>
            </Card.Body>
        </Card>
    );
}
