import { Card } from 'react-bootstrap';
import type { ProviderBandwidthSnapshot } from '~/types/bandwidth';

type Props = {
    bandwidth: ProviderBandwidthSnapshot[];
};

function formatSpeed(bytesPerSecond: number): string {
    if (bytesPerSecond <= 0) return '0 B/s';
    const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
    const index = Math.min(Math.floor(Math.log(bytesPerSecond) / Math.log(1024)), units.length - 1);
    const value = bytesPerSecond / Math.pow(1024, index);
    return `${value.toFixed(index >= 2 ? 1 : 0)} ${units[index]}`;
}

export function CurrentBandwidth({ bandwidth }: Props) {
    const totalSpeed = bandwidth.reduce((sum, provider) => sum + provider.currentSpeed, 0);
    const activeProviders = bandwidth.filter(provider => provider.currentSpeed > 0).length;

    return (
        <Card bg="dark" text="white" className="border-secondary h-100">
            <Card.Body>
                <div className="text-muted small mb-2">Overall Current Bandwidth</div>
                <div className="display-6 fw-semibold">{formatSpeed(totalSpeed)}</div>
                <div className="text-muted small mt-2">
                    {activeProviders} active provider{activeProviders === 1 ? '' : 's'}
                </div>
            </Card.Body>
        </Card>
    );
}
