import { Alert, Button, Form } from "react-bootstrap";
import { useCallback, useState } from "react";

const defaultExportPath = "/config/infinidysk-export";

type ExportResponse = {
    status?: boolean;
    error?: string;
    outputDirectory?: string;
    sqlFilePath?: string;
    bytes?: number;
    tableRows?: Record<string, number>;
    nzbBlobCount?: number;
    fileBlobCount?: number;
};

function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB"];
    const index = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
    return `${(bytes / Math.pow(1024, index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

export function InfiniDyskExport() {
    const [path, setPath] = useState<string>(defaultExportPath);
    const [isExporting, setIsExporting] = useState<boolean>(false);
    const [result, setResult] = useState<ExportResponse | null>(null);
    const [error, setError] = useState<string | null>(null);

    const onExport = useCallback(async () => {
        setIsExporting(true);
        setResult(null);
        setError(null);
        try {
            const response = await fetch("/api/export-infini-dysk", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ path }),
            });

            const payload = await response.json().catch(() => null) as ExportResponse | null;
            if (!response.ok || payload?.status === false) {
                setError(payload?.error || `Export failed (${response.status} ${response.statusText}).`);
                return;
            }
            setResult(payload);
        } catch (e) {
            setError(e instanceof Error ? e.message : "Export failed.");
        } finally {
            setIsExporting(false);
        }
    }, [path]);

    const rows = result?.tableRows ?? {};
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
                    disabled={isExporting}
                    onChange={(event) => setPath(event.target.value)}
                    placeholder={defaultExportPath}
                />
                <Form.Text className="text-muted">
                    Must be an absolute path that is mounted from the host, otherwise the file is
                    only reachable from inside the container.
                </Form.Text>
            </Form.Group>

            <Button
                variant={isExporting ? "secondary" : "success"}
                disabled={isExporting || path.trim().length === 0}
                onClick={onExport}
            >
                {isExporting ? "\u231B Exporting..." : "\u25B6 Export database"}
            </Button>

            {error && (
                <Alert variant="danger" className="mt-3 mb-0">
                    {error}
                </Alert>
            )}

            {result && (
                <Alert variant="success" className="mt-3 mb-0">
                    <div>
                        Wrote <code>{result.sqlFilePath}</code> ({formatBytes(result.bytes ?? 0)}).
                    </div>
                    {rowSummary && <div className="mt-1">Rows: {rowSummary}.</div>}
                    <div className="mt-1">
                        Blob files: {result.nzbBlobCount ?? 0} NZB, {result.fileBlobCount ?? 0} file
                        metadata.
                    </div>
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
