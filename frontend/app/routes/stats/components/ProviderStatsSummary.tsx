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

    const providerSummaries = providerIndexes.map(index => {
        const stats = statsByProvider.get(index);
        const current = currentByProvider.get(index);
        const bytes = bytesByProvider[index] || 0;
        const successful = stats?.operationCounts?.BODY || 0;
        const failed = stats?.operationCounts?.BODY_FAIL || 0;
        const operations = stats?.totalOperations || successful + failed;
        const successRate = operations > 0 ? (successful / operations) * 100 : 0;
        const bandwidthPercent = totalBytes > 0 ? (bytes / totalBytes) * 100 : 0;

        return {
            index,
            stats,
            current,
            bytes,
            operations,
            successRate,
            bandwidthPercent,
            providerName: getProviderName(index),
            maxBandwidthPercent: maxBytes > 0 ? (bytes / maxBytes) * 100 : 0
        };
    });

    return (
        <Card bg="dark" text="white" className="border-secondary mb-4">
            <Card.Body>
                <div className="d-flex flex-wrap justify-content-between align-items-start gap-3 mb-3">
                    <div>
                        <h4 className="m-0">Provider Stats & Bandwidth</h4>
                        <div className="text-muted small">Showing {rangeLabel(range)}</div>
                    </div>
                    <div className="d-flex flex-wrap gap-4 ms-md-auto justify-content-md-end text-md-end text-muted small">
                        <div><span className="text-light fw-semibold">{formatBytes(totalBytes)}</span> downloaded</div>
                        <div><span className="text-light fw-semibold">{formatNumber(providerStats?.totalOperations || 0)}</span> segment operations</div>
                    </div>
                </div>

                <div className="table-responsive d-none d-md-block">
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
                            {providerSummaries.length === 0 ? (
                                <tr>
                                    <td colSpan={6} className="text-center text-muted py-4">No provider stats for this period</td>
                                </tr>
                            ) : providerSummaries.map(provider => {
                                return (
                                    <tr key={provider.index}>
                                        <td style={{ minWidth: 220 }}>
                                            <div className="fw-semibold text-truncate" title={provider.providerName}>
                                                {provider.providerName.replace(/^news\.|^bonus\./, "")}
                                            </div>
                                            <div className="text-muted small">{provider.stats?.providerType || "Provider"}</div>
                                        </td>
                                        <td className="text-end" style={{ minWidth: 180 }}>
                                            <div className="fw-semibold text-info">{formatBytes(provider.bytes)}</div>
                                            <ProgressBar
                                                now={provider.maxBandwidthPercent}
                                                style={{ height: 6, backgroundColor: "rgba(255,255,255,0.1)" }}
                                                className="mt-1 bg-transparent"
                                            />
                                            <div className="text-muted small">{provider.bandwidthPercent.toFixed(1)}%</div>
                                        </td>
                                        <td className="text-end text-info">{formatSpeed(provider.current?.currentSpeed || 0)}</td>
                                        <td className="text-end">{formatNumber(provider.operations)}</td>
                                        <td className="text-end">
                                            <span className={provider.successRate >= 95 ? "text-success" : provider.successRate >= 80 ? "text-warning" : "text-danger"}>
                                                {provider.operations > 0 ? `${provider.successRate.toFixed(1)}%` : "—"}
                                            </span>
                                        </td>
                                        <td className="text-end">{provider.stats ? `${provider.stats.averageSpeedMbps.toFixed(1)} MB/s` : "—"}</td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </Table>
                </div>

                <div className="d-md-none d-grid gap-3">
                    {providerSummaries.length === 0 ? (
                        <div className="text-center text-muted py-4">No provider stats for this period</div>
                    ) : providerSummaries.map(provider => (
                        <div key={provider.index} className="rounded border border-secondary p-3 bg-black bg-opacity-10">
                            <div className="d-flex justify-content-between align-items-start gap-3 mb-2">
                                <div style={{ minWidth: 0 }}>
                                    <div className="fw-semibold text-truncate" title={provider.providerName}>
                                        {provider.providerName.replace(/^news\.|^bonus\./, "")}
                                    </div>
                                    <div className="text-muted small">{provider.stats?.providerType || "Provider"}</div>
                                </div>
                                <div className="text-end flex-shrink-0">
                                    <div className="fw-semibold text-info">{formatBytes(provider.bytes)}</div>
                                    <div className="text-muted small">downloaded</div>
                                </div>
                            </div>
                            <ProgressBar
                                now={provider.maxBandwidthPercent}
                                style={{ height: 6, backgroundColor: "rgba(255,255,255,0.1)" }}
                                className="mb-3 bg-transparent"
                            />
                            <div className="row row-cols-2 g-2 small">
                                <div>
                                    <div className="text-muted text-uppercase">Current</div>
                                    <div className="text-info fw-semibold">{formatSpeed(provider.current?.currentSpeed || 0)}</div>
                                </div>
                                <div className="text-end">
                                    <div className="text-muted text-uppercase">Segments</div>
                                    <div>{formatNumber(provider.operations)}</div>
                                </div>
                                <div>
                                    <div className="text-muted text-uppercase">Success</div>
                                    <div className={provider.successRate >= 95 ? "text-success" : provider.successRate >= 80 ? "text-warning" : "text-danger"}>
                                        {provider.operations > 0 ? `${provider.successRate.toFixed(1)}%` : "—"}
                                    </div>
                                </div>
                                <div className="text-end">
                                    <div className="text-muted text-uppercase">Avg speed</div>
                                    <div>{provider.stats ? `${provider.stats.averageSpeedMbps.toFixed(1)} MB/s` : "—"}</div>
                                </div>
                            </div>
                        </div>
                    ))}
                </div>
            </Card.Body>
        </Card>
    );
}
