import { Button, Form, InputGroup, Spinner, Alert } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction, useState, useEffect, useCallback, useMemo } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type ProviderConfig = {
    Providers: Array<{ MaxConnections: number; Type: number }>;
};

// Provider types from usenet settings
const ProviderType = {
    Disabled: 0,
    Pooled: 1,        // Primary - connections go into the main pool
    BackupAndStats: 2, // Backup - not counted in main pool
    BackupOnly: 3,     // Backup - not counted in main pool
};

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

type RcloneRcConfig = {
    Url?: string;
    Username?: string;
    Password?: string;
    Enabled: boolean;
    CachePath?: string;
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    const rcloneRcConfig: RcloneRcConfig = JSON.parse(config["rclone.rc"] || "{}");
    const [downloadKey, setDownloadKey] = useState<string>("");
    const [isLoadingKey, setIsLoadingKey] = useState(false);
    const [isRegenerating, setIsRegenerating] = useState(false);
    const [copied, setCopied] = useState(false);

    const fetchDownloadKey = useCallback(async () => {
        setIsLoadingKey(true);
        try {
            const response = await fetch('/api/download-key?action=get');
            const data = await response.json();
            if (data.status && data.downloadKey) {
                setDownloadKey(data.downloadKey);
            }
        } catch (error) {
            console.error('Failed to fetch download key:', error);
        } finally {
            setIsLoadingKey(false);
        }
    }, []);

    useEffect(() => {
        fetchDownloadKey();
    }, [fetchDownloadKey]);

    const handleRegenerateKey = async () => {
        if (!confirm('Are you sure you want to regenerate the download key? All existing download links will stop working.')) {
            return;
        }
        setIsRegenerating(true);
        try {
            const response = await fetch('/api/download-key?action=regenerate');
            const data = await response.json();
            if (data.status && data.downloadKey) {
                setDownloadKey(data.downloadKey);
            }
        } catch (error) {
            console.error('Failed to regenerate download key:', error);
        } finally {
            setIsRegenerating(false);
        }
    };

    const handleCopyKey = async () => {
        try {
            await navigator.clipboard.writeText(downloadKey);
            setCopied(true);
            setTimeout(() => setCopied(false), 2000);
        } catch (error) {
            console.error('Failed to copy:', error);
        }
    };

    const [testConnStatus, setTestConnStatus] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');
    const [testConnMessage, setTestConnMessage] = useState('');

    const updateRcloneConfig = (newRcConfig: Partial<RcloneRcConfig>) => {
        const updated = { ...rcloneRcConfig, ...newRcConfig };
        setNewConfig({ ...config, "rclone.rc": JSON.stringify(updated) });
        setTestConnStatus('idle');
    };

    const testRcloneConnection = async () => {
        setTestConnStatus('testing');
        try {
            const form = new FormData();
            form.append('host', rcloneRcConfig.Url || '');
            if (rcloneRcConfig.Username) form.append('user', rcloneRcConfig.Username);
            if (rcloneRcConfig.Password) form.append('pass', rcloneRcConfig.Password);
            const response = await fetch('/api/rclone/test-connection', { method: 'POST', body: form });
            const data = await response.json();
            if (data.success) {
                setTestConnStatus('success');
                setTestConnMessage(data.version ? `Connected (${data.version})` : 'Connected');
            } else {
                setTestConnStatus('error');
                setTestConnMessage(data.error || 'Connection failed');
            }
        } catch (error) {
            setTestConnStatus('error');
            setTestConnMessage('Request failed');
        }
    };

    return (
        <div className={styles.container}>
            <h4>Authentication</h4>
            <Form.Group>
                <Form.Label htmlFor="webdav-user-input">WebDAV User</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidUser(config["webdav.user"]) && styles.error])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <Form.Text id="webdav-user-help" muted>
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="webdav-pass-input">WebDAV Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    value={config["webdav.pass"]}
                    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                <Form.Text id="webdav-pass-help" muted>
                    Use this password to connect to the webdav.
                </Form.Text>
            </Form.Group>
            <hr />

            <h4>Streaming Connection Pool</h4>
            <Form.Group>
                <Form.Label htmlFor="total-streaming-connections-input">Total Streaming Connections</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidPositiveInt(config["usenet.total-streaming-connections"]) && styles.error])}
                    type="text"
                    id="total-streaming-connections-input"
                    aria-describedby="total-streaming-connections-help"
                    placeholder="20"
                    value={config["usenet.total-streaming-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.total-streaming-connections": e.target.value })} />
                <Form.Text id="total-streaming-connections-help" muted>
                    Global streaming connection budget shared by all active buffered streams. A single stream can use spare capacity; multiple streams contend for permits as segment fetches complete.
                </Form.Text>
                <ConnectionPoolTip config={config} />
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-concurrent-streams-input">Max Concurrent Buffered Streams</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidPositiveInt(config["usenet.max-concurrent-buffered-streams"]) && styles.error])}
                    type="text"
                    id="max-concurrent-streams-input"
                    aria-describedby="max-concurrent-streams-help"
                    placeholder="2"
                    value={config["usenet.max-concurrent-buffered-streams"] || ""}
                    onChange={e => setNewConfig({ ...config, "usenet.max-concurrent-buffered-streams": e.target.value })} />
                <Form.Text id="max-concurrent-streams-help" muted>
                    Maximum number of simultaneously active buffered playback pumps. Each pump draws from the shared streaming pool as demand and permits allow. Higher values can improve multi-client playback but increase memory pressure. (Default: 2)
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="streaming-reserve-input">Streaming Reserve</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidNonNegativeInt(config["usenet.streaming-reserve"]) && styles.error])}
                    type="text"
                    id="streaming-reserve-input"
                    aria-describedby="streaming-reserve-help"
                    placeholder="5"
                    value={config["usenet.streaming-reserve"] || ""}
                    onChange={e => setNewConfig({ ...config, "usenet.streaming-reserve": e.target.value })} />
                <Form.Text id="streaming-reserve-help" muted>
                    Slots protected from lower-priority queue, repair, and analysis work so playback can acquire connections promptly during background activity. Must be lower than Total Streaming Connections. (Default: 5)
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="streaming-priority-input">Streaming Priority Odds (%)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidPriority(config["usenet.streaming-priority"]) && styles.error])}
                    type="text"
                    id="streaming-priority-input"
                    aria-describedby="streaming-priority-help"
                    placeholder="80"
                    value={config["usenet.streaming-priority"] || ""}
                    onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                <Form.Text id="streaming-priority-help" muted>
                    Probability (0&ndash;100) that a streaming request beats a lower-priority queue or repair task when contending for a connection. Higher = more responsive playback during heavy queue activity. (Default: 80)
                </Form.Text>
            </Form.Group>
            <hr />

            <h4>Streaming Behavior</h4>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="use-buffered-streaming"
                    aria-describedby="use-buffered-streaming-help"
                    label={`Use Buffered Streaming Pump (recommended)`}
                    checked={(config["usenet.use-buffered-streaming"] ?? "true") === "true"}
                    onChange={e => setNewConfig({ ...config, "usenet.use-buffered-streaming": "" + e.target.checked })} />
                <Form.Text id="use-buffered-streaming-help" muted>
                    When enabled, GETs go through the buffered/shared streaming pump (smoother playback, segment cache, transcode-restart tolerance). Disabling falls back to direct per-request streams.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="shared-stream-buffer-size-input">Shared Stream Buffer Size (MB)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidPositiveInt(config["usenet.shared-stream-buffer-size"]) && styles.error])}
                    type="text"
                    id="shared-stream-buffer-size-input"
                    aria-describedby="shared-stream-buffer-size-help"
                    placeholder="32"
                    value={config["usenet.shared-stream-buffer-size"] || ""}
                    onChange={e => setNewConfig({ ...config, "usenet.shared-stream-buffer-size": e.target.value })} />
                <Form.Text id="shared-stream-buffer-size-help" muted>
                    Ring-buffer size in MB per shared stream. Larger values smooth simultaneous Plex transcode + direct-play overlap. (Default: 32)
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="shared-stream-grace-input">Shared Stream Grace Period (seconds)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidNonNegativeInt(config["usenet.shared-stream-grace-period"]) && styles.error])}
                    type="text"
                    id="shared-stream-grace-input"
                    aria-describedby="shared-stream-grace-help"
                    placeholder="10"
                    value={config["usenet.shared-stream-grace-period"] || ""}
                    onChange={e => setNewConfig({ ...config, "usenet.shared-stream-grace-period": e.target.value })} />
                <Form.Text id="shared-stream-grace-help" muted>
                    Seconds to keep a shared stream alive after the last reader detaches. Covers Plex transcode-restart and brief client reconnects. (Default: 10)
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="stream-buffer-size-input">Stream Buffer Size (segments)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidStreamBufferSize(config["usenet.stream-buffer-size"]) && styles.error])}
                    type="text"
                    id="stream-buffer-size-input"
                    aria-describedby="stream-buffer-size-help"
                    placeholder="100"
                    value={config["usenet.stream-buffer-size"]}
                    onChange={e => setNewConfig({ ...config, "usenet.stream-buffer-size": e.target.value })} />
                <Form.Text id="stream-buffer-size-help" muted>
                    Number of segments to buffer ahead during streaming. Higher values (50&ndash;200) provide smoother playback but use more memory. Each segment is ~300&ndash;500 KB. Allowed range: 10&ndash;500.
                </Form.Text>
            </Form.Group>
            <hr />

            <h4>WebDAV Behavior</h4>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    label={`Enforce Read-Only`}
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                <Form.Text id="readonly-help" muted>
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    label={`Show hidden files on Dav Explorer`}
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                <Form.Text id="show-hidden-files-help" muted>
                    Hidden files or directories are those whose names are prefixed by a period.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    label={`Preview par2 files on Dav Explorer`}
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                <Form.Text id="preview-par2-files-help" muted>
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </Form.Text>
            </Form.Group>
            <hr />
            <h4>Rclone Remote Control (RC)</h4>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="rclone-rc-enabled"
                    label={`Enable Rclone Server Notifications`}
                    checked={rcloneRcConfig.Enabled ?? false}
                    onChange={e => updateRcloneConfig({ Enabled: e.target.checked })} />
            </Form.Group>
            {rcloneRcConfig.Enabled && (
                <>
                    <Form.Group>
                        <Form.Label htmlFor="rclone-rc-url">Rclone Server Host</Form.Label>
                        <InputGroup>
                            <Form.Control
                                className={styles.input}
                                type="text"
                                id="rclone-rc-url"
                                placeholder="http://localhost:5572"
                                value={rcloneRcConfig.Url || ""}
                                onChange={e => updateRcloneConfig({ Url: e.target.value })} />
                            <Button
                                variant={testConnStatus === 'success' ? 'outline-success' : testConnStatus === 'error' ? 'outline-danger' : 'outline-secondary'}
                                onClick={testRcloneConnection}
                                disabled={testConnStatus === 'testing' || !rcloneRcConfig.Url}
                            >
                                {testConnStatus === 'testing' ? <Spinner size="sm" />
                                    : testConnStatus === 'success' ? 'Connected'
                                    : testConnStatus === 'error' ? 'Failed'
                                    : 'Test'}
                            </Button>
                        </InputGroup>
                        {testConnStatus === 'success' && testConnMessage && (
                            <Form.Text className="text-success">{testConnMessage}</Form.Text>
                        )}
                        {testConnStatus === 'error' && testConnMessage && (
                            <Form.Text className="text-danger">{testConnMessage}</Form.Text>
                        )}
                    </Form.Group>
                    <Form.Group>
                        <Form.Label htmlFor="rclone-rc-user">Rclone Server User</Form.Label>
                        <Form.Control
                            className={styles.input}
                            type="text"
                            id="rclone-rc-user"
                            placeholder="admin"
                            value={rcloneRcConfig.Username || ""}
                            onChange={e => updateRcloneConfig({ Username: e.target.value })} />
                    </Form.Group>
                    <Form.Group>
                        <Form.Label htmlFor="rclone-rc-pass">Rclone Server Password</Form.Label>
                        <Form.Control
                            className={styles.input}
                            type="password"
                            id="rclone-rc-pass"
                            value={rcloneRcConfig.Password || ""}
                            onChange={e => updateRcloneConfig({ Password: e.target.value })} />
                    </Form.Group>
                    <Form.Group>
                        <Form.Label htmlFor="rclone-cache-path">VFS Cache Path</Form.Label>
                        <Form.Control
                            className={styles.input}
                            type="text"
                            id="rclone-cache-path"
                            placeholder="/mnt/nzbdav-cache"
                            value={rcloneRcConfig.CachePath || ""}
                            onChange={e => updateRcloneConfig({ CachePath: e.target.value })} />
                        <Form.Text muted>
                            Optional: Path to rclone VFS cache directory. When flushing cache via RC, files will also be deleted from this path.
                            The structure should be: <code>{"{CachePath}"}/vfs/{"{remote}"}/.ids/...</code>
                        </Form.Text>
                    </Form.Group>
                </>
            )}
            <hr />
            <h4>Static Download Key</h4>
            <Form.Group>
                <Form.Label htmlFor="download-key-input">Download Key</Form.Label>
                <InputGroup>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="download-key-input"
                        readOnly
                        value={isLoadingKey ? "Loading..." : downloadKey}
                        style={{ fontFamily: 'monospace', fontSize: '0.85em' }}
                    />
                    <Button
                        variant="outline-secondary"
                        onClick={handleCopyKey}
                        disabled={isLoadingKey || !downloadKey}
                    >
                        {copied ? "Copied!" : "Copy"}
                    </Button>
                    <Button
                        variant="outline-danger"
                        onClick={handleRegenerateKey}
                        disabled={isLoadingKey || isRegenerating}
                    >
                        {isRegenerating ? <Spinner size="sm" /> : "Regenerate"}
                    </Button>
                </InputGroup>
                <Form.Text id="download-key-help" muted>
                    This key allows direct downloads from the /view/ endpoint without per-path authentication.
                    Use it with: <code>?downloadKey=YOUR_KEY</code>. Regenerating will invalidate all existing links.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.total-streaming-connections"] !== newConfig["usenet.total-streaming-connections"]
        || config["usenet.max-concurrent-buffered-streams"] !== newConfig["usenet.max-concurrent-buffered-streams"]
        || config["usenet.streaming-reserve"] !== newConfig["usenet.streaming-reserve"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.use-buffered-streaming"] !== newConfig["usenet.use-buffered-streaming"]
        || config["usenet.shared-stream-buffer-size"] !== newConfig["usenet.shared-stream-buffer-size"]
        || config["usenet.shared-stream-grace-period"] !== newConfig["usenet.shared-stream-grace-period"]
        || config["usenet.stream-buffer-size"] !== newConfig["usenet.stream-buffer-size"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
        || config["rclone.rc"] !== newConfig["rclone.rc"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    if (!isValidUser(newConfig["webdav.user"])) return false;
    if (!isValidPositiveInt(newConfig["usenet.total-streaming-connections"])) return false;
    if (!isValidPositiveInt(newConfig["usenet.max-concurrent-buffered-streams"])) return false;
    if (!isValidNonNegativeInt(newConfig["usenet.streaming-reserve"])) return false;
    if (!isValidPriority(newConfig["usenet.streaming-priority"])) return false;
    if (!isValidPositiveInt(newConfig["usenet.shared-stream-buffer-size"])) return false;
    if (!isValidNonNegativeInt(newConfig["usenet.shared-stream-grace-period"])) return false;
    if (!isValidStreamBufferSize(newConfig["usenet.stream-buffer-size"])) return false;
    const total = parseInt(newConfig["usenet.total-streaming-connections"]);
    const reserve = parseInt(newConfig["usenet.streaming-reserve"]);
    if (reserve >= total) return false;
    return true;
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidStreamBufferSize(value: string): boolean {
    return isPositiveInteger(value) && parseInt(value) >= 10 && parseInt(value) <= 500;
}

function isValidPositiveInt(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidNonNegativeInt(value: string): boolean {
    if (value === undefined || value === null) return false;
    const trimmed = String(value).trim();
    if (trimmed === "") return false;
    const num = Number(trimmed);
    return Number.isInteger(num) && num >= 0 && trimmed === num.toString();
}

function isValidPriority(value: string): boolean {
    if (!isValidNonNegativeInt(value)) return false;
    const num = parseInt(value);
    return num >= 0 && num <= 100;
}

function ConnectionPoolTip({ config }: { config: Record<string, string> }) {
    const stats = useMemo(() => {
        // Parse provider config to get total connections from primary (pooled) providers only
        // Backup providers are not counted as they're only used for retries/health checks
        let totalProviderConnections = 0;
        try {
            const providerConfig: ProviderConfig = JSON.parse(config["usenet.providers"] || "{}");
            if (providerConfig.Providers) {
                totalProviderConnections = providerConfig.Providers
                    .filter(p => p.Type === ProviderType.Pooled) // Only count primary pooled providers
                    .reduce((sum, p) => sum + (p.MaxConnections || 0), 0);
            }
        } catch { /* ignore parse errors */ }

        const streamingConnections = parseInt(config["usenet.total-streaming-connections"] || "20") || 20;
        const maxStreams = parseInt(config["usenet.max-concurrent-buffered-streams"] || "2") || 2;
        const streamingReserve = parseInt(config["usenet.streaming-reserve"] || "5") || 5;

        const effectiveStreamingCap = Math.min(totalProviderConnections, streamingConnections);

        return {
            totalProviderConnections,
            streamingConnections,
            maxStreams,
            streamingReserve,
            effectiveStreamingCap,
            isOverAllocated: streamingConnections > totalProviderConnections && totalProviderConnections > 0,
            isReserveTooLarge: streamingReserve >= streamingConnections,
        };
    }, [config]);

    if (stats.totalProviderConnections === 0) {
        return null; // No providers configured yet
    }

    const variant = (stats.isOverAllocated || stats.isReserveTooLarge) ? "warning" : "info";

    return (
        <Alert variant={variant} className="mt-2 py-2 px-3" style={{ fontSize: '0.85em' }}>
            <strong>Connection Pool:</strong>{' '}
            {stats.totalProviderConnections} pooled provider connections; streaming cap is{' '}
            <strong>{stats.effectiveStreamingCap}</strong> of {stats.streamingConnections} configured permits across up to {stats.maxStreams} buffered streams.
            {stats.isOverAllocated && (
                <div className="mt-1 text-warning-emphasis">
                    Warning: Total Streaming Connections ({stats.streamingConnections}) exceeds pooled provider capacity ({stats.totalProviderConnections}).
                    The effective streaming cap will be limited by provider connections.
                </div>
            )}
            {stats.isReserveTooLarge && (
                <div className="mt-1 text-warning-emphasis">
                    Warning: Streaming Reserve ({stats.streamingReserve}) must be lower than Total Streaming Connections ({stats.streamingConnections}).
                </div>
            )}
        </Alert>
    );
}
