import { Table, Badge, Pagination, Form, Button } from "react-bootstrap";
import type { HealthCheckQueueItem } from "~/types/backend";
import styles from "./health-table.module.css";
import { Truncate } from "~/routes/queue/components/truncate/truncate";
import { ProgressBadge } from "~/routes/queue/components/status-badge/status-badge";
import { useCallback, useEffect, useState } from "react";
import { Activity, Play, RotateCcw, Search, Wrench, Zap } from "lucide-react";

export type HealthTableProps = {
    isEnabled: boolean,
    healthCheckItems: HealthCheckQueueItem[],
    totalCount: number,
    page: number,
    pageSize: number,
    search: string,
    showAll: boolean,
    showFailed: boolean,
    showUnhealthy: boolean,
    onPageChange: (page: number) => void,
    onSearchChange: (search: string) => void,
    onShowAllChange: (showAll: boolean) => void,
    onShowFailedChange: (showFailed: boolean) => void,
    onShowUnhealthyChange: (showUnhealthy: boolean) => void,
    onRunHealthCheck: (id: string) => void,
    onRunHeadHealthCheck?: (ids: string[]) => void,
    onRepair?: (ids: string[]) => void,
    onResetHealthStatus?: (ids: string[]) => void,
    onItemClick: (id: string) => void,
}

export function HealthTable({
    isEnabled,
    healthCheckItems,
    totalCount,
    page,
    pageSize,
    search,
    showAll,
    showFailed,
    showUnhealthy,
    onPageChange,
    onSearchChange,
    onShowAllChange,
    onShowFailedChange,
    onShowUnhealthyChange,
    onRunHealthCheck,
    onRunHeadHealthCheck,
    onRepair,
    onResetHealthStatus,
    onItemClick
}: HealthTableProps) {

    const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
    const [localSearch, setLocalSearch] = useState(search);
    const totalPages = Math.ceil(totalCount / pageSize);

    useEffect(() => {
        setLocalSearch(search);
    }, [search]);

    const handleSearchKeyDown = useCallback((e: React.KeyboardEvent<HTMLInputElement>) => {
        if (e.key === 'Enter') {
            onSearchChange(localSearch);
        }
    }, [localSearch, onSearchChange]);

    const toggleSelection = (id: string) => {
        setSelectedIds(prev => {
            const newSet = new Set(prev);
            if (newSet.has(id)) {
                newSet.delete(id);
            } else {
                newSet.add(id);
            }
            return newSet;
        });
    };

    const toggleSelectAll = () => {
        if (selectedIds.size === healthCheckItems.length) {
            setSelectedIds(new Set());
        } else {
            setSelectedIds(new Set(healthCheckItems.map(item => item.id)));
        }
    };

    const handleBulkRepair = () => {
        if (onRepair && selectedIds.size > 0) {
            onRepair(Array.from(selectedIds));
            setSelectedIds(new Set());
        }
    };

    const handleBulkHeadCheck = () => {
        if (onRunHeadHealthCheck && selectedIds.size > 0) {
            onRunHeadHealthCheck(Array.from(selectedIds));
            setSelectedIds(new Set());
        }
    };

    const handleBulkReset = () => {
        if (onResetHealthStatus && selectedIds.size > 0) {
            onResetHealthStatus(Array.from(selectedIds));
            setSelectedIds(new Set());
        }
    };

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Health Check Queue</h3>
                <div className={styles.count}>
                    {totalCount} items
                </div>
            </div>

            <div className={styles.controls}>
                <div className={styles.searchContainer}>
                    <input
                        className={styles.searchInput}
                        type="text"
                        placeholder="Search health queue..."
                        value={localSearch}
                        onChange={(e) => setLocalSearch(e.target.value)}
                        onKeyDown={handleSearchKeyDown}
                    />
                </div>
                <div className={styles.filterContainer} style={{ display: 'flex', gap: '1rem', alignItems: 'center' }}>
                    {selectedIds.size > 0 && (
                        <>
                            <Badge bg="primary">{selectedIds.size} selected</Badge>
                            <Button
                                variant="danger"
                                size="sm"
                                onClick={handleBulkRepair}
                                disabled={!onRepair}
                            >
                                <Wrench size={14} className={styles.inlineIcon} />
                                Repair Selected
                            </Button>
                            <Button
                                variant="warning"
                                size="sm"
                                onClick={handleBulkHeadCheck}
                                disabled={!onRunHeadHealthCheck}
                            >
                                <Zap size={14} className={styles.inlineIcon} />
                                HEAD Check Selected
                            </Button>
                            <Button
                                variant="secondary"
                                size="sm"
                                onClick={handleBulkReset}
                                disabled={!onResetHealthStatus}
                            >
                                <RotateCcw size={14} className={styles.inlineIcon} />
                                Reset Status Selected
                            </Button>
                        </>
                    )}
                    <Form.Check
                        type="checkbox"
                        id="show-unhealthy-checkbox"
                        label="Unhealthy Only"
                        checked={showUnhealthy}
                        onChange={(e) => onShowUnhealthyChange(e.target.checked)}
                    />
                    <Form.Check
                        type="checkbox"
                        id="show-failed-checkbox"
                        label="Corrupted Only"
                        checked={showFailed}
                        onChange={(e) => onShowFailedChange(e.target.checked)}
                    />
                    <Form.Check
                        type="checkbox"
                        id="show-all-checkbox"
                        label="Show All Files"
                        checked={showAll}
                        onChange={(e) => onShowAllChange(e.target.checked)}
                    />
                </div>
            </div>

            {!isEnabled ? (
                <div className={styles.emptyState}>
                    <Activity className={styles.emptyIcon} aria-hidden />
                    <div className={styles.emptyTitle}>Enable Repairs In Settings</div>
                    <div className={styles.emptyDescription}>
                        Once you enable repairs, all mounted usenet files will be queued for continuous health monitoring
                    </div>
                </div>
            ) : healthCheckItems.length === 0 ? (
                <div className={styles.emptyState}>
                    {search ? <Search className={styles.emptyIcon} aria-hidden /> : <Activity className={styles.emptyIcon} aria-hidden />}
                    <div className={styles.emptyTitle}>
                        {search ? "No Results Found" : "No Items To Health-Check"}
                    </div>
                    <div className={styles.emptyDescription}>
                        {search 
                            ? `No items matching "${search}" were found in the queue.` 
                            : "Once you begin processing nzbs, the mounted usenet files will be queued for continuous health monitoring"}
                    </div>
                </div>
            ) : (
                <>
                    <div className={styles.tableContainer}>
                        <Table className={styles.table} responsive>
                            <thead className={styles.desktop}>
                                <tr>
                                    <th style={{ width: '40px' }}>
                                        <Form.Check
                                            type="checkbox"
                                            checked={selectedIds.size === healthCheckItems.length && healthCheckItems.length > 0}
                                            onChange={toggleSelectAll}
                                        />
                                    </th>
                                    <th>Name</th>
                                    <th className={styles.desktop}>Status</th>
                                    <th className={styles.desktop}>Next</th>
                                    <th style={{ width: '70px' }}></th>
                                </tr>
                            </thead>
                            <tbody>
                                {healthCheckItems.map(item => (
                                    <tr
                                        key={item.id}
                                        className={styles.tableRow}
                                        onClick={() => onItemClick(item.id)}
                                        style={{ cursor: 'pointer' }}
                                    >
                                        <td onClick={(e) => e.stopPropagation()} style={{ width: '40px' }}>
                                            <Form.Check
                                                type="checkbox"
                                                checked={selectedIds.has(item.id)}
                                                onChange={() => toggleSelection(item.id)}
                                            />
                                        </td>
                                        <td className={styles.nameCell}>
                                            <div className={styles.nameContainer}>
                                                {item.jobName && (
                                                    <div className={styles.jobName}><Truncate>{item.jobName}</Truncate></div>
                                                )}
                                                <div className={item.jobName ? styles.fileNameSmall : styles.name}><Truncate>{item.name}</Truncate></div>
                                                <div className={styles.metaRow}>
                                                    <span className={styles.path}><Truncate>{item.path}</Truncate></span>
                                                    <span className={styles.createdDate}>Added {formatDate(item.releaseDate, 'Unknown')}</span>
                                                </div>
                                                <div className={styles.mobile}>
                                                    <DateDetailsTable item={item} onRunHealthCheck={onRunHealthCheck} onResetHealthStatus={onResetHealthStatus} />
                                                </div>
                                            </div>
                                        </td>
                                        <td className={`${styles.statusCell} ${styles.desktop}`}>
                                            <div className={styles.statusContainer}>
                                                {item.progress !== 0 ? (
                                                    <Badge bg="primary" className={styles.resultBadge}>Checking</Badge>
                                                ) : item.latestResult ? (
                                                    <Badge bg={item.latestResult === 'Healthy' ? 'success' : item.latestResult === 'Skipped' ? 'info' : 'danger'} className={styles.resultBadge}>
                                                        {item.latestResult}
                                                    </Badge>
                                                ) : (
                                                    <Badge bg="secondary" className={styles.resultBadge}>Pending</Badge>
                                                )}
                                                <span className={styles.lastCheckTime}>
                                                    {item.lastHealthCheck ? formatDate(item.lastHealthCheck, 'Never') : 'Never checked'}
                                                </span>
                                            </div>
                                        </td>
                                        <td className={`${styles.nextCell} ${styles.desktop}`}>
                                            <div className={styles.nextContainer}>
                                                {item.progress > 0
                                                    ? <ProgressBadge className={styles.progressBadge} color={"#333"} percentNum={100 + item.progress}>{item.progress}%</ProgressBadge>
                                                    : item.progress < 0
                                                        ? <span className={styles.nextTime}>Checking now</span>
                                                    : <span className={styles.nextTime}>{formatDate(item.nextHealthCheck, 'ASAP')}</span>
                                                }
                                                <Badge bg={item.operationType === 'HEAD' ? 'danger' : 'secondary'} className={styles.operationBadge}>
                                                    {item.operationType}
                                                </Badge>
                                            </div>
                                        </td>
                                        <td className={styles.actionsCell}>
                                            <div className={styles.actionButtons}>
                                                <div
                                                    className={styles.actionButton}
                                                    onClick={(e) => { e.stopPropagation(); onRunHealthCheck(item.id); }}
                                                    title="Run Health Check Now"
                                                    role="button"
                                                >
                                                    <Play size={17} aria-hidden />
                                                </div>
                                                <div
                                                    className={styles.actionButton}
                                                    onClick={(e) => { e.stopPropagation(); onResetHealthStatus?.([item.id]); }}
                                                    title="Reset Health Status"
                                                    role="button"
                                                >
                                                    <RotateCcw size={17} aria-hidden />
                                                </div>
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </Table>
                    </div>

                    {totalPages > 1 && (
                        <div className="d-flex justify-content-center mt-3">
                            <Pagination>
                                <Pagination.First onClick={() => onPageChange(0)} disabled={page === 0} />
                                <Pagination.Prev onClick={() => onPageChange(page - 1)} disabled={page === 0} />
                                
                                <Pagination.Item active>{page + 1}</Pagination.Item>
                                
                                <Pagination.Next onClick={() => onPageChange(page + 1)} disabled={page >= totalPages - 1} />
                                <Pagination.Last onClick={() => onPageChange(totalPages - 1)} disabled={page >= totalPages - 1} />
                            </Pagination>
                        </div>
                    )}
                </>
            )}
        </div>
    );
}

function DateDetailsTable({ item, onRunHealthCheck, onResetHealthStatus }: {
    item: HealthCheckQueueItem,
    onRunHealthCheck: (id: string) => void,
    onResetHealthStatus?: (ids: string[]) => void
}) {
    return (
        <div className={styles.dateDetailsTable}>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Status</div>
                <div className={styles.dateDetailsValue}>
                    <div className={styles.mobileStatusRow}>
                        {item.progress !== 0 ? (
                            <Badge bg="primary" className={styles.resultBadge}>Checking</Badge>
                        ) : item.latestResult ? (
                            <Badge bg={item.latestResult === 'Healthy' ? 'success' : item.latestResult === 'Skipped' ? 'info' : 'danger'} className={styles.resultBadge}>
                                {item.latestResult}
                            </Badge>
                        ) : (
                            <Badge bg="secondary" className={styles.resultBadge}>Pending</Badge>
                        )}
                        <span className={styles.mobileCheckTime}>
                            {item.lastHealthCheck ? formatDate(item.lastHealthCheck, 'Never') : 'Never'}
                        </span>
                    </div>
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Next</div>
                <div className={styles.dateDetailsValue}>
                    <div className={styles.mobileNextRow}>
                        {item.progress > 0
                            ? <ProgressBadge className={styles.progressBadge} color={"#333"} percentNum={100 + item.progress}>{item.progress}%</ProgressBadge>
                            : item.progress < 0
                                ? <span>Checking now</span>
                            : <span>{formatDate(item.nextHealthCheck, 'ASAP')}</span>
                        }
                        <Badge bg={item.operationType === 'HEAD' ? 'danger' : 'secondary'} className={styles.operationBadge}>
                            {item.operationType}
                        </Badge>
                    </div>
                </div>
            </div>
            <div className={styles.dateDetailsRow}>
                <div className={styles.dateDetailsLabel}>Actions</div>
                <div className={styles.dateDetailsValue}>
                    <div className={styles.actionButtons}>
                        <div
                            className={styles.actionButton}
                            onClick={(e) => { e.stopPropagation(); onRunHealthCheck(item.id); }}
                            title="Run Health Check Now"
                            role="button"
                        >
                            <Play size={17} aria-hidden />
                        </div>
                        <div
                            className={styles.actionButton}
                            onClick={(e) => { e.stopPropagation(); onResetHealthStatus?.([item.id]); }}
                            title="Reset Health Status"
                            role="button"
                        >
                            <RotateCcw size={17} aria-hidden />
                        </div>
                    </div>
                </div>
            </div>
        </div>
    );
}

function formatDate(dateString: string | null, fallback: string) {
    try {
        if (!dateString) return fallback;
        const now = new Date();
        const datetime = new Date(dateString);
        return isSameDate(datetime, now)
            ? datetime.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
            : datetime.toLocaleDateString();
    } catch {
        return 'Unknown';
    }
};

function formatDateBadge(dateString: string | null, fallback: string, variant: 'info' | 'warning' | 'success') {
    const dateText = formatDate(dateString, fallback);
    return <Badge bg={variant} className={styles.dateBadge}>{dateText}</Badge>;
};

function isSameDate(one: Date, two: Date) {
    return (
        one.getFullYear() === two.getFullYear() &&
        one.getMonth() === two.getMonth() &&
        one.getDate() === two.getDate()
    );
}