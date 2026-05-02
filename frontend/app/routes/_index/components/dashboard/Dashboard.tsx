import { useState, useEffect } from 'react';
import { Alert, ButtonGroup, Button, Row, Col } from 'react-bootstrap';
import type { DashboardData } from '~/types/dashboard';
import { TIME_WINDOW_OPTIONS } from '~/types/dashboard';
import type { ConnectionUsageContext } from '~/types/connections';
import { ActiveStreaming } from './ActiveStreaming';
import { TotalDownloaded } from './TotalDownloaded';
import { ProviderHealth } from './ProviderHealth';
import { ProviderUsage } from './ProviderUsage';
import { RecentCompletions } from './RecentCompletions';
import { createWebsocketBackoff, getBrowserWebsocketUrl, receiveMessage } from '~/utils/websocket-util';

type Props = {
    initialData: DashboardData;
    initialConnections: Record<number, ConnectionUsageContext[]>;
};

export function Dashboard({ initialData, initialConnections }: Props) {
    const [data, setData] = useState(initialData);
    const [connections, setConnections] = useState(initialConnections);
    const [selectedHours, setSelectedHours] = useState(initialData.timeWindowHours);
    const [isLoading, setIsLoading] = useState(false);
    const [backendError, setBackendError] = useState<string | null>(null);

    // Build provider name lookup
    const providerNames = data.providerHealth.reduce((acc, p) => {
        acc[p.providerIndex] = p.providerHost.replace(/^news\.|^bonus\./, '');
        return acc;
    }, {} as Record<number, string>);

    // WebSocket for real-time connections
    useEffect(() => {
        let ws: WebSocket | null = null;
        let disposed = false;
        let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
        const backoff = createWebsocketBackoff();

        function scheduleReconnect() {
            if (disposed) return;
            const delay = backoff.nextDelayMs();
            reconnectTimer = setTimeout(() => connect(), delay);
        }

        function connect() {
            ws = new WebSocket(getBrowserWebsocketUrl());

            ws.onopen = () => {
                backoff.reset();
                ws?.send(JSON.stringify({ 'cxs': 'state' }));
            };

            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== 'cxs') return;

                const parts = message.split('|');
                if (parts.length >= 9) {
                    const providerIndex = parseInt(parts[0], 10);
                    if (!Number.isInteger(providerIndex) || providerIndex < 0) return;

                    const connsJson = parts[8];
                    try {
                        const rawConns = JSON.parse(connsJson) as any[];
                        const transformedConns = rawConns.map(c => ({
                            usageType: c.t,
                            details: c.d,
                            jobName: c.jn,
                            isBackup: c.b,
                            isSecondary: c.s,
                            bufferedCount: c.bc,
                            bufferWindowStart: c.ws,
                            bufferWindowEnd: c.we,
                            totalSegments: c.ts,
                            davItemId: c.i,
                            currentBytePosition: c.bp,
                            fileSize: c.fs
                        } as ConnectionUsageContext));

                        setConnections(prev => ({
                            ...prev,
                            [providerIndex]: transformedConns
                        }));
                    } catch (e) {
                        console.error('Failed to parse connections JSON from websocket', e);
                    }
                }
            });

            ws.onclose = scheduleReconnect;

            ws.onerror = () => {
                ws?.close();
            };
        }

        connect();

        return () => {
            disposed = true;
            if (reconnectTimer) clearTimeout(reconnectTimer);
            ws?.close();
        };
    }, []);

    // Fetch data when time window changes
    useEffect(() => {
        if (selectedHours === initialData.timeWindowHours) {
            setData(initialData);
            setBackendError(null);
            return;
        }

        setIsLoading(true);
        setBackendError(null);
        fetch(`/dashboard-proxy?hours=${selectedHours}`)
            .then(async res => {
                const body = await res.json();
                if (!res.ok) {
                    throw new Error(body.details || body.error || `Backend returned ${res.status}`);
                }
                return body;
            })
            .then(newData => {
                setData(newData);
                setIsLoading(false);
            })
            .catch(err => {
                console.error('Failed to fetch dashboard data:', err);
                setBackendError(err instanceof Error ? err.message : String(err));
                setIsLoading(false);
            });
    }, [selectedHours, initialData]);

    const timeWindowLabel = TIME_WINDOW_OPTIONS.find(o => o.value === selectedHours)?.label || `${selectedHours}h`;

    return (
        <div className="p-4">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h2 className="m-0">System Dashboard</h2>
                <ButtonGroup>
                    {TIME_WINDOW_OPTIONS.map(option => (
                        <Button
                            key={option.value}
                            variant={selectedHours === option.value ? 'primary' : 'outline-secondary'}
                            size="sm"
                            onClick={() => setSelectedHours(option.value)}
                            disabled={isLoading}
                        >
                            {option.label}
                        </Button>
                    ))}
                </ButtonGroup>
            </div>

            {backendError &&
                <Alert variant="warning">
                    Dashboard data could not be refreshed from the backend. Showing the last loaded data. {backendError}
                </Alert>
            }

            {/* Active Streaming - Full Width */}
            <ActiveStreaming connections={connections} providerNames={providerNames} />

            {/* Total Downloaded + Provider Health */}
            <Row className="mb-4 gy-4">
                <Col lg={4}>
                    <h6 className="text-muted mb-2">Total Downloaded</h6>
                    <TotalDownloaded data={data.totalDownloaded} timeWindowLabel={timeWindowLabel} />
                </Col>
                <Col lg={8}>
                    <h6 className="text-muted mb-2 mt-2 mt-lg-0">Provider Health</h6>
                    <ProviderHealth providers={data.providerHealth} />
                </Col>
            </Row>

            {/* Provider Usage - Full Width */}
            <ProviderUsage providers={data.providerUsage} />

            {/* Recent Completions - Full Width */}
            <RecentCompletions completions={data.recentCompletions} />
        </div>
    );
}
