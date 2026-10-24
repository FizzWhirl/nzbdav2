import { Alert, Button, Form } from "react-bootstrap";
import { useCallback, useEffect, useState } from "react";
import { createWebsocketBackoff, getBrowserWebsocketUrl, receiveMessage } from "~/utils/websocket-util";

const defaultExportPath = "/config/infinidysk-export";

/** Topic published by InfiniDyskExportRunner; a cached State topic, so it replays on connect. */
const exportTopic = { "iep": "state" };

type ExportState = "idle" | "running" | "completed" | "failed";

type ExportResponse = {
    status?: boolean;
    error?: string;
    state?: ExportState;
    message?: string;
    outputDirectory?: string;
    completedAtUtc?: string;
    bytes?: number;
    tableRows?: Record<string, number>;
    nzbBlobCount?: number;
    fileBlobCount?: number;
    inspectable?: boolean;
    warning?: string | null;
};

function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB"];
    const index = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
    return `${(bytes / Math.pow(1024, index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function parseStatus(raw: string): ExportResponse | null {
    try {
        const parsed = JSON.parse(raw);
        if (!parsed || typeof parsed !== "object") return null;
        return parsed as ExportResponse;
    } catch {
        return null;
    }
}

export function InfiniDyskExport() {
    const [path, setPath] = useState<string>(defaultExportPath);
    const [compact, setCompact] = useState<boolean>(false);
    const [status, setStatus] = useState<ExportResponse | null>(null);
    const [error, setError] = useState<string | null>(null);

    const state = status?.state ?? "idle";
    const isRunning = state === "running";

    // Live progress. The backend runs the export detached from the request and publishes
    // stages on a cached State topic, so reconnecting (or reloading the page) replays the
    // latest stage instead of losing the outcome.
    useEffect(() => {
        let ws: WebSocket | undefined;
        let disposed = false;
        let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
        const backoff = createWebsocketBackoff();

        function scheduleReconnect() {
            if (disposed) return;
            reconnectTimer = setTimeout(connect, backoff.nextDelayMs());
        }

        function connect() {
            ws = new WebSocket(getBrowserWebsocketUrl());
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== "iep") return;
                const parsed = parseStatus(message);
                if (parsed) setStatus(parsed);
            });
            ws.onopen = () => {
                backoff.reset();
                ws?.send(JSON.stringify(exportTopic));
            };
            ws.onclose = () => scheduleReconnect();
            ws.onerror = () => ws?.close();
        }

        connect();
        return () => {
            disposed = true;
            if (reconnectTimer) clearTimeout(reconnectTimer);
            ws?.close();
        };
    }, []);

    // Recover status after a page reload or an app restart: the runner has no in-memory record
    // in the latter case, so ask the backend to inspect the folder on disk.
    useEffect(() => {
        let cancelled = false;
        (async () => {
            try {
                const response = await fetch(`/api/export-infini-dysk?path=${encodeURIComponent(defaultExportPath)}`);
                if (!response.ok) return;
                const payload = await response.json() as ExportResponse;
                if (!cancelled && payload?.state) setStatus(payload);
            } catch {
                // Status is advisory; the websocket will fill it in.
            }
        })();
        return () => { cancelled = true; };
    }, []);

    const onExport = useCallback(async () => {
        setError(null);
        setStatus({ state: "running", message: "Starting export..." });
        try {
            const response = await fetch("/api/export-infini-dysk", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ path, compact }),
            });

            const payload = await response.json().catch(() => null) as ExportResponse | null;
            if (!response.ok || payload?.status === false) {
                setError(payload?.error || `Could not start the export (${response.status} ${response.statusText}).`);
                // Keep showing `running` when the conflict came from a job already in flight.
                if (payload?.state) setStatus(payload);
                else setStatus(null);
                return;
            }

            if (payload) setStatus(payload);
        } catch (e) {
            setError(e instanceof Error ? e.message : "Could not start the export.");
            setStatus(null);
        }
    }, [path, compact]);

    const rows = status?.tableRows ?? {};
    const rowSummary = Object.entries(rows)
        .filter(([, count]) => count > 0)
        .map(([table, count]) => `${table}: ${count}`)
        .join(", ");

    return (
        <div>
            <p>
                Writes a <code>db.sql</code> dump plus a <code>blobs/</code> folder into the path below.
                The dump is written against InfiniDysk&apos;s current schema and imports through
                <strong> Settings &rarr; Backup &amp; Restore &rarr; Upload</strong>; copy
                <code>blobs/</code> into InfiniDysk&apos;s config volume afterwards.
            </p>

            <Form.Group className="mb-3">
                <Form.Label>Export folder (inside the container)</Form.Label>
                <Form.Control
                    type="text"
                    value={path}
                    disabled={isRunning}
                    onChange={(event) => setPath(event.target.value)}
                    placeholder={defaultExportPath}
                />
                <Form.Text className="text-muted">
                    Must be an absolute path that is mounted from the host, otherwise the file is
                    only reachable from inside the container.
                </Form.Text>
            </Form.Group>

            <Form.Group className="mb-3">
                <Form.Check
                    type="checkbox"
                    id="infinidysk-export-compact"
                    checked={compact}
                    disabled={isRunning}
                    onChange={(event) => setCompact(event.target.checked)}
                    label="Compact — skip health-check history (smaller file)"
                />
                <Form.Text className="text-muted">
                    Health-check results and stats are a large share of a mature library&apos;s
                    dump but are not needed for migration; InfiniDysk re-checks files itself.
                    Use this if the upload is rejected as too large.
                </Form.Text>
            </Form.Group>

            <Button
                variant={isRunning ? "secondary" : "success"}
                disabled={isRunning || path.trim().length === 0}
                onClick={onExport}
            >
                {isRunning ? "\u231B Exporting..." : "\u25B6 Export database"}
            </Button>

            <div className="mt-2 text-muted" style={{ fontSize: "0.875rem" }}>
                {isRunning
                    ? "Running in the background — you can close or reload this page and it will keep going."
                    : "Runs in the background; closing this page does not cancel it."}
            </div>

            {error && (
                <Alert variant="danger" className="mt-3 mb-0">
                    {error}
                </Alert>
            )}

            {isRunning && (
                <Alert variant="info" className="mt-3 mb-0">
                    {status?.message || "Working..."}
                    {status?.outputDirectory && (
                        <div className="text-muted mt-1">Writing to <code>{status.outputDirectory}</code></div>
                    )}
                </Alert>
            )}

            {state === "failed" && !error && (
                <Alert variant="danger" className="mt-3 mb-0">
                    {status?.message || "The export failed."}
                </Alert>
            )}

            {state === "completed" && (
                <Alert variant={status?.warning ? "warning" : "success"} className="mt-3 mb-0">
                    <div>
                        {status?.inspectable
                            ? status?.message
                            : <>Wrote <code>{status?.outputDirectory}</code>.</>}
                    </div>
                    {typeof status?.bytes === "number" && status.bytes > 0 && (
                        <div className="mt-1"><code>db.sql</code> is {formatBytes(status.bytes)}.</div>
                    )}
                    {status?.warning && (
                        <div className="mt-2"><strong>{status.warning}</strong></div>
                    )}
                    {rowSummary && <div className="mt-1">Rows: {rowSummary}.</div>}
                    {(status?.nzbBlobCount != null || status?.fileBlobCount != null) && (
                        <div className="mt-1">
                            Blob files: {status?.nzbBlobCount ?? 0} NZB, {status?.fileBlobCount ?? 0} file metadata.
                        </div>
                    )}
                    <div className="mt-2">
                        Upload <code>db.sql</code> in InfiniDysk, and copy this folder&apos;s
                        <code>blobs/</code> into its config volume so NZB download, PAR2 repair,
                        and health-check identity work for migrated items.
                    </div>
                    <div className="mt-2">
                        Not carried over: the active queue, user accounts, indexer/*Arr/rclone
                        settings, and fork-only statistics. Usenet providers are included —
                        re-check their order and create the admin account after restoring.
                    </div>
                </Alert>
            )}
        </div>
    );
}
