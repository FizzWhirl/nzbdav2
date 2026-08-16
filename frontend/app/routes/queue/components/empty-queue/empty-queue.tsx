import pageStyles from "../../route.module.css"
import { useCallback, useRef } from "react";
import styles from "./empty-queue.module.css"
import { useDropzone, type FileWithPath } from 'react-dropzone'
import { className } from "~/utils/styling";
import { useFetcher } from "react-router";
import { Alert } from "react-bootstrap";

export type NzbUploadProps = {
    // when true, renders a slim upload bar instead of the
    // full-height placeholder shown when the queue is empty.
    compact?: boolean,
}

export function EmptyQueue({ compact = false }: NzbUploadProps) {
    const fetcher = useFetcher();
    const formRef = useRef<HTMLFormElement>(null);
    const inputRef = useRef<HTMLInputElement>(null);
    const isSubmitting = (fetcher.state === 'submitting');

    const { getRootProps, getInputProps, isDragActive } = useDropzone({
        accept: { 'application/x-nzb': ['.nzb'] },
        multiple: true,
        onDrop: useCallback((acceptedFiles: FileWithPath[]) => {
            const dataTransfer = new DataTransfer();
            acceptedFiles.forEach((file) => {
                const newFile = new File([file], file.name, {
                    type: 'application/x-nzb',
                    lastModified: file.lastModified,
                });
                dataTransfer.items.add(newFile);
            });
            if (inputRef?.current) {
                inputRef.current.files = dataTransfer.files;
                fetcher.submit(formRef.current);
            }
        }, [])
    });

    return (
        <fetcher.Form ref={formRef} method="POST" encType="multipart/form-data">
            {!compact &&
                <div className={pageStyles["section-title"]}>
                    <div className={pageStyles["section-heading"]}>
                        <h3>Queue</h3>
                    </div>
                </div>
            }
            {fetcher.data?.error && (
                <Alert variant="danger">{fetcher.data.error}</Alert>
            )}
            <div {...className([
                styles.container,
                compact && styles.compact,
                isDragActive && styles["drag-active"],
            ])} {...getRootProps()}>
                <input {...getInputProps()} />
                <input ref={inputRef} name="nzbFile" type="file" multiple style={{ display: 'none' }} />

                {isSubmitting && <>
                    <div>Uploading...</div>
                </>}

                {/* default view */}
                {!isSubmitting && !isDragActive && <>
                    <div className={styles["upload-icon"]}></div>
                    {!compact && <br />}
                    {!compact && <div>Queue is empty.</div>}
                    <div>Upload one or more *.nzb files</div>
                </>}

                {/* when dragging files */}
                {!isSubmitting && isDragActive && <>
                    <div className={styles["drop-icon"]}></div>
                    {!compact && <br />}
                    <div>Drop your *.nzb files</div>
                </>}
            </div>
        </fetcher.Form>
    );
}
