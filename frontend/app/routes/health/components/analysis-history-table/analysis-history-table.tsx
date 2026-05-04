import { Table, Button, Form, Pagination, Badge } from "react-bootstrap";
import { formatDistanceToNow } from "date-fns";
import type { AnalysisHistoryItem } from "~/types/backend";
import { formatDateTime } from "~/utils/datetime";

interface Props {
    items: AnalysisHistoryItem[];
    page: number;
    search: string;
    showFailedOnly: boolean;
    showActionNeededOnly: boolean;
    typeFilter: string;
    onPageChange: (page: number) => void;
    onSearchChange: (search: string) => void;
    onShowFailedOnlyChange: (showFailedOnly: boolean) => void;
    onShowActionNeededOnlyChange: (showActionNeededOnly: boolean) => void;
    onTypeFilterChange: (typeFilter: string) => void;
    onAnalyze: (id: string) => void;
    onItemClick: (item: AnalysisHistoryItem) => void;
}

export function AnalysisHistoryTable({ items, page, search, showFailedOnly, showActionNeededOnly, typeFilter, onPageChange, onSearchChange, onShowFailedOnlyChange, onShowActionNeededOnlyChange, onTypeFilterChange, onAnalyze, onItemClick }: Props) {
    return (
        <div>
            <div className="d-flex justify-content-between align-items-center mb-3">
                <div className="d-flex gap-3 align-items-center flex-wrap">
                    <Form.Control
                        type="text"
                        placeholder="Search history..."
                        value={search}
                        onChange={(e) => onSearchChange(e.target.value)}
                        style={{ maxWidth: "300px" }}
                    />
                    <Form.Select
                        value={typeFilter}
                        onChange={(e) => onTypeFilterChange(e.target.value)}
                        aria-label="Filter analysis history by type"
                        style={{ maxWidth: "220px" }}
                    >
                        <option value="all">All Types</option>
                        <option value="Health Check">Health Check</option>
                        <option value="Analysis">Analysis</option>
                        <option value="Media Analysis">Media Analysis</option>
                        <option value="NZB Analysis">NZB Analysis</option>
                    </Form.Select>
                    <Form.Check
                        type="checkbox"
                        id="show-failed-only"
                        label="Show Failed Only"
                        checked={showFailedOnly}
                        onChange={(e) => onShowFailedOnlyChange(e.target.checked)}
                        style={{ whiteSpace: "nowrap", minWidth: "max-content" }}
                    />
                    <Form.Check
                        type="checkbox"
                        id="show-action-needed-only"
                        label="Action Needed Only"
                        checked={showActionNeededOnly}
                        onChange={(e) => onShowActionNeededOnlyChange(e.target.checked)}
                        style={{ whiteSpace: "nowrap", minWidth: "max-content" }}
                    />
                </div>
                <div className="d-flex gap-2">
                    <Button
                        variant="secondary"
                        disabled={page === 0}
                        onClick={() => onPageChange(Math.max(0, page - 1))}
                    >
                        Previous
                    </Button>
                    <Button
                        variant="secondary"
                        disabled={items.length < 100} // Assuming 100 page size
                        onClick={() => onPageChange(page + 1)}
                    >
                        Next
                    </Button>
                </div>
            </div>

            <Table striped bordered hover variant="dark" responsive className="align-middle">
                <thead>
                    <tr>
                        <th>Date</th>
                        <th>Type</th>
                        <th>File Name</th>
                        <th>Job Name</th>
                        <th>Result</th>
                        <th>Failure Reason / Details</th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {items.length === 0 ? (
                        <tr>
                            <td colSpan={7} className="text-center text-muted fst-italic py-4">
                                No history found
                            </td>
                        </tr>
                    ) : (
                        items.map((item) => (
                            <tr
                                key={item.id}
                                onClick={() => onItemClick(item)}
                                style={{ cursor: "pointer" }}
                            >
                                <td style={{ whiteSpace: "nowrap" }}>
                                    <span title={formatDateTime(item.createdAt)}>
                                        {formatDistanceToNow(new Date(item.createdAt), { addSuffix: true })}
                                    </span>
                                </td>
                                <td>
                                    <Badge bg={getHistoryTypeColor(item)}>
                                        {getHistoryType(item)}
                                    </Badge>
                                </td>
                                <td className="text-break" style={{ maxWidth: "250px" }} title={item.fileName}>
                                    {item.fileName}
                                </td>
                                <td className="text-break" style={{ maxWidth: "200px" }} title={item.jobName || ""}>
                                    {item.jobName || "-"}
                                </td>
                                <td>
                                    <Badge bg={getResultColor(item.result)}>
                                        {item.result}
                                    </Badge>
                                </td>
                                <td className="small text-muted text-break" style={{ maxWidth: "300px" }}>
                                    {item.details || "-"}
                                </td>
                                <td>
                                    {!item.isRemoved && (
                                        <Button
                                            variant="outline-primary"
                                            size="sm"
                                            onClick={(e) => { e.stopPropagation(); onAnalyze(item.davItemId); }}
                                        >
                                            Re-Analyze
                                        </Button>
                                    )}
                                </td>
                            </tr>
                        ))
                    )}
                </tbody>
            </Table>
        </div>
    );
}

function getHistoryType(item: AnalysisHistoryItem) {
    if (item.type) return item.type;

    const details = (item.details || "").toLowerCase();
    if (details.startsWith("health check")) return "Health Check";
    if (details.startsWith("analysis")) return "Analysis";
    if (details.includes("ffprobe") || details.startsWith("media analysis") || details.startsWith("media integrity")) return "Media Analysis";
    if (details.includes("segment")) return "NZB Analysis";
    return "Analysis";
}

function getHistoryTypeColor(item: AnalysisHistoryItem) {
    switch (getHistoryType(item)) {
        case "Health Check": return "primary";
        case "Media Analysis": return "info";
        case "NZB Analysis": return "secondary";
        default: return "dark";
    }
}

function getResultColor(result: string) {
    switch (result) {
        case "Success": return "success";
        case "Pending": return "warning";
        case "Skipped": return "info";
        default: return "danger";
    }
}
