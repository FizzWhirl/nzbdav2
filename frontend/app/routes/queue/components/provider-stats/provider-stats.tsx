import { Card } from 'react-bootstrap';
import { useState } from 'react';
import type { ProviderStatsResponse } from '~/clients/backend-client.server';
import styles from './provider-stats.module.css';

export function ProviderStats({ stats: initialStats }: { stats: ProviderStatsResponse | null }) {
    const [stats] = useState(initialStats);

    // Don't render at all if we've never had any stats
    if (!initialStats) {
        return null;
    }

    const formatNumber = (num: number) => {
        return num.toLocaleString();
    };

    const formatBytes = (bytes: number) => {
        if (bytes === 0) return '0 B';
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(1024));
        const value = bytes / Math.pow(1024, i);
        return `${value.toFixed(i > 1 ? 1 : 0)} ${units[i]}`;
    };

    const getTimeAgo = (timestamp: string) => {
        const now = new Date();
        const then = new Date(timestamp);
        const diffMs = now.getTime() - then.getTime();
        const diffMins = Math.floor(diffMs / 60000);

        if (diffMins < 1) return 'just now';
        if (diffMins === 1) return '1 minute ago';
        if (diffMins < 60) return `${diffMins} minutes ago`;

        const diffHours = Math.floor(diffMins / 60);
        if (diffHours === 1) return '1 hour ago';
        return `${diffHours} hours ago`;
    };

    const getSuccessRate = (operationCounts: { [key: string]: number }) => {
        const successful = operationCounts['BODY'] || 0;
        const failed = operationCounts['BODY_FAIL'] || 0;
        const total = successful + failed;
        if (total === 0) return 0;
        return (successful / total) * 100;
    };

    return (
        <Card className={styles.statsCard}>
            <Card.Body>
                <div className={styles.header}>
                    <div>
                        <h5 className={styles.title}>Provider Stats</h5>
                        <span className={styles.subtitle}>Since last reset</span>
                    </div>
                    <div className={styles.headerControls}>
                        {stats && (
                            <span className={styles.updated}>
                                Updated {getTimeAgo(stats.calculatedAt)}
                            </span>
                        )}
                    </div>
                </div>

                {!stats || stats.providers.length === 0 ? (
                    <p className={styles.noData}>
                        No provider stats available
                    </p>
                ) : (
                    <div className={styles.providersGrid}>
                        {stats.providers.map((provider) => {
                            const successful = provider.operationCounts['BODY'] || 0;
                            const failed = provider.operationCounts['BODY_FAIL'] || 0;
                            const successRate = getSuccessRate(provider.operationCounts);

                            return (
                                <div key={provider.providerHost} className={styles.providerCard}>
                                    <div className={styles.providerHeader}>
                                        <span className={styles.providerHost}>{provider.providerHost}</span>
                                        <span className={styles.providerBadge}>
                                            {provider.providerType}
                                        </span>
                                    </div>
                                    <div className={styles.providerStats}>
                                        <div className={styles.totalOps}>
                                            <span className={styles.opsCount}>
                                                {formatNumber(provider.totalOperations)}
                                            </span>
                                            <span className={styles.opsLabel}>segments</span>
                                            <span className={styles.percentage}>
                                                ({provider.percentageOfTotal.toFixed(1)}%)
                                            </span>
                                        </div>
                                        <div className={styles.statsRow}>
                                            <div className={styles.statItem}>
                                                <span className={styles.statLabel}>Successful</span>
                                                <span className={styles.statValueSuccess}>{formatNumber(successful)}</span>
                                            </div>
                                            <div className={styles.statItem}>
                                                <span className={styles.statLabel}>Failed</span>
                                                <span className={styles.statValueFail}>{formatNumber(failed)}</span>
                                            </div>
                                            <div className={styles.statItem}>
                                                <span className={styles.statLabel}>Success rate</span>
                                                <span className={styles.statValue}>{successRate.toFixed(1)}%</span>
                                            </div>
                                        </div>
                                        <div className={styles.statsRow}>
                                            <div className={styles.statItem}>
                                                <span className={styles.statLabel}>Downloaded</span>
                                                <span className={styles.statValue}>{formatBytes(provider.totalBytes)}</span>
                                            </div>
                                            <div className={styles.statItem}>
                                                <span className={styles.statLabel}>Avg speed</span>
                                                <span className={styles.statValue}>{provider.averageSpeedMbps.toFixed(1)} MB/s</span>
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            );
                        })}
                    </div>
                )}
            </Card.Body>
        </Card>
    );
}
