import pageStyles from "../../route.module.css"
import { ActionButton } from "../action-button/action-button"
import { PageRow, PageTable } from "../page-table/page-table"
import { useCallback, useRef, useState } from "react"
import { ConfirmModal } from "../confirm-modal/confirm-modal"
import type { PresentationQueueSlot } from "../../route"
import type { TriCheckboxState } from "../tri-checkbox/tri-checkbox"
import { useFetcher } from "react-router"
import { Alert, Button } from "react-bootstrap"

export type QueueTableProps = {
    queueSlots: PresentationQueueSlot[],
    onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void,
    onRemoved: (nzo_ids: Set<string>) => void,
}

export function QueueTable({ queueSlots, onIsSelectedChanged, onIsRemovingChanged, onRemoved }: QueueTableProps) {
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
        const fetcher = useFetcher();
        const formRef = useRef<HTMLFormElement>(null);
        const inputRef = useRef<HTMLInputElement>(null);
        const isUploading = fetcher.state === 'submitting';

        const onUploadClick = useCallback(() => {
            inputRef.current?.click();
        }, []);

        const onFileChange = useCallback(() => {
            if (inputRef.current?.files?.length) {
                fetcher.submit(formRef.current);
            }
        }, [fetcher]);

    var selectedCount = queueSlots.filter(x => !!x.isSelected).length;
    var headerCheckboxState: TriCheckboxState = selectedCount === 0 ? 'none' : selectedCount === queueSlots.length ? 'all' : 'some';

    const onSelectAll = useCallback((isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>(queueSlots.map(x => x.nzo_id)), isSelected);
    }, [queueSlots, onIsSelectedChanged]);

    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async () => {
        var nzo_ids = new Set<string>(queueSlots.filter(x => !!x.isSelected).map(x => x.nzo_id));
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(nzo_ids, true);
        try {
            const url = `/api?mode=queue&name=delete`;
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json;charset=UTF-8',
                },
                body: JSON.stringify({ nzo_ids: Array.from(nzo_ids) }),
            });
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(nzo_ids);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(nzo_ids, false);
    }, [queueSlots, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    return (
        <>
            <div className={pageStyles["section-title"]}>
                <h3>Queue</h3>
                    {fetcher.data?.error && (
                        <Alert variant="danger" style={{ margin: 0, padding: '0.25rem 0.75rem', fontSize: '0.875rem' }}>
                            {fetcher.data.error}
                        </Alert>
                    )}
                    <fetcher.Form ref={formRef} method="POST" encType="multipart/form-data" style={{ display: 'inline' }}>
                        <input ref={inputRef} name="nzbFile" type="file" accept=".nzb" style={{ display: 'none' }} onChange={onFileChange} />
                    </fetcher.Form>
                    <Button variant="outline-secondary" size="sm" onClick={onUploadClick} disabled={isUploading} style={{ marginLeft: '0.5rem' }}>
                        {isUploading ? 'Uploading...' : '+ Add NZB'}
                    </Button>
                {headerCheckboxState !== 'none' &&
                    <ActionButton type="delete" onClick={onRemove} />
                }
            </div>
            <div style={{ minHeight: "300px" }}>
                <PageTable headerCheckboxState={headerCheckboxState} onHeaderCheckboxChange={onSelectAll} striped>
                    {queueSlots.map(slot =>
                        <QueueRow
                            key={slot.nzo_id}
                            slot={slot}
                            onIsSelectedChanged={(id, isSelected) => onIsSelectedChanged(new Set<string>([id]), isSelected)}
                            onIsRemovingChanged={(id, isRemoving) => onIsRemovingChanged(new Set<string>([id]), isRemoving)}
                            onRemoved={(id) => onRemoved(new Set([id]))}
                        />
                    )}
                </PageTable>
            </div>

            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From Queue?"
                message={`${selectedCount} item(s) will be removed`}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </>
    );
}

type QueueRowProps = {
    slot: PresentationQueueSlot
    onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void,
    onRemoved: (nzo_id: string) => void
}

export function QueueRow({ slot, onIsSelectedChanged, onIsRemovingChanged, onRemoved }: QueueRowProps) {
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);

    // events
    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async () => {
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(slot.nzo_id, true);
        try {
            const url = '/api?mode=queue&name=delete'
                + `&value=${encodeURIComponent(slot.nzo_id)}`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(slot.nzo_id);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    const onMoveToTop = useCallback(async () => {
        try {
            const url = '/api?mode=queue&name=priority'
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&value2=top`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    // Queue will be updated via WebSocket
                    return;
                }
            }
        } catch { }
    }, [slot.nzo_id]);

    const onMoveToBottom = useCallback(async () => {
        try {
            const url = '/api?mode=queue&name=priority'
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&value2=bottom`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    // Queue will be updated via WebSocket
                    return;
                }
            }
        } catch { }
    }, [slot.nzo_id]);

    // view
    return (
        <>
            <PageRow
                isSelected={!!slot.isSelected}
                isRemoving={!!slot.isRemoving}
                name={slot.filename}
                category={slot.cat}
                status={slot.status}
                percentage={slot.true_percentage}
                fileSizeBytes={Number(slot.mb) * 1024 * 1024}
                actions={
                    <>
                        <ActionButton type="move-top" disabled={!!slot.isRemoving} onClick={onMoveToTop} />
                        <ActionButton type="move-bottom" disabled={!!slot.isRemoving} onClick={onMoveToBottom} />
                        <ActionButton type="delete" disabled={!!slot.isRemoving} onClick={onRemove} />
                    </>
                }
                onRowSelectionChanged={isSelected => onIsSelectedChanged(slot.nzo_id, isSelected)}
            />
            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From Queue?"
                message={slot.filename}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </>
    )
}