import { Card } from 'react-bootstrap';
import type { ConnectionUsageContext } from '~/types/connections';
import type { ProviderBandwidthSnapshot } from '~/types/bandwidth';

type Props = {
    connections: Record<number, ConnectionUsageContext[]>;
    providerNames: Record<number, string>;
    bandwidth: ProviderBandwidthSnapshot[];
};

function formatSpeed(bytesPerSecond: number): string {
    if (bytesPerSecond <= 0) return '0 B/s';
    const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
    const index = Math.min(Math.floor(Math.log(bytesPerSecond) / Math.log(1024)), units.length - 1);
    const value = bytesPerSecond / Math.pow(1024, index);
    return `${value.toFixed(index >= 2 ? 1 : 0)} ${units[index]}`;
}

export function ActiveStreaming({ connections, providerNames, bandwidth }: Props) {
    const totalCurrentSpeed = bandwidth.reduce((sum, provider) => sum + provider.currentSpeed, 0);
    const providerIndices = Array.from(new Set([
        ...Object.keys(providerNames).map(Number),
        ...bandwidth.map(provider => provider.providerIndex),
        ...Object.keys(connections).map(Number)
    ])).sort((a, b) => a - b);

    const header = (
        <div className="d-flex flex-wrap align-items-baseline gap-2 mb-3">
            <h6 className="text-muted m-0">Active Streaming</h6>
            <span className="fs-5 fw-semibold text-info">{formatSpeed(totalCurrentSpeed)}</span>
        </div>
    );

    return (
        <Card bg="dark" text="white" className="border-secondary mb-4">
            <Card.Body>
                {header}
                <div className="d-flex flex-wrap gap-3">
                    {providerIndices.length === 0 ? (
                        <div className="text-center text-muted py-3 w-100">No providers configured</div>
                    ) : providerIndices
                        .map(providerIndex => (
                            <ProviderGroup
                                key={providerIndex}
                                providerName={providerNames[providerIndex] || bandwidth.find(x => x.providerIndex === providerIndex)?.host || `Provider ${providerIndex + 1}`}
                                currentSpeed={bandwidth.find(x => x.providerIndex === providerIndex)?.currentSpeed || 0}
                                connections={connections[providerIndex] || []}
                            />
                        ))}
                </div>
            </Card.Body>
        </Card>
    );
}

function ProviderGroup({ providerName, currentSpeed, connections }: { providerName: string; currentSpeed: number; connections: ConnectionUsageContext[] }) {
    const renderStreams = (limit: number) => (
        <>
            {connections.slice(0, limit).map((conn, idx) => (
                <StreamItem key={idx} connection={conn} />
            ))}
            {connections.length > limit && (
                <small className="text-muted">+{connections.length - limit} more</small>
            )}
        </>
    );

    return (
        <div className="bg-black bg-opacity-25 rounded p-3 flex-grow-1" style={{ minWidth: '200px', flexBasis: '220px', minHeight: '118px' }}>
            <div className="d-flex justify-content-between align-items-center mb-2">
                <span className="fw-bold text-truncate" title={providerName}>{providerName}</span>
                <span className={`badge ${connections.length > 0 ? 'bg-success' : 'bg-secondary'}`}>{connections.length}</span>
            </div>
            <div className="text-info small fw-semibold mb-2">{formatSpeed(currentSpeed)}</div>
            {connections.length === 0 ? (
                <div className="text-muted fst-italic small">Nothing streaming</div>
            ) : (
                <>
                    <div className="d-none d-md-flex flex-column gap-1">{renderStreams(5)}</div>
                    <div className="d-flex d-md-none flex-column gap-1">{renderStreams(2)}</div>
                </>
            )}
        </div>
    );
}

function StreamItem({ connection }: { connection: ConnectionUsageContext }) {
    const fileName = connection.details?.split('/').pop() || 'Unknown';
    const shortName = fileName.length > 25 ? fileName.substring(0, 22) + '...' : fileName;

    // Calculate progress if we have byte position and file size
    const progress = connection.currentBytePosition && connection.fileSize
        ? Math.round((connection.currentBytePosition / connection.fileSize) * 100)
        : null;

    return (
        <div className="d-flex justify-content-between align-items-center small">
            <span className="text-truncate" style={{ maxWidth: '150px' }} title={fileName}>
                {shortName}
            </span>
            {progress !== null && (
                <span className="text-muted ms-2">{progress}%</span>
            )}
        </div>
    );
}
