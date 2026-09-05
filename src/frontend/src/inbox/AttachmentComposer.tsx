import { useEffect, useRef, useState } from 'react';
import type { AttachmentsApi, StagedAttachment } from '../api/attachments';
import type { Fetcher } from '../api/client';

export type AttachmentStatus = 'uploading' | 'scanning' | 'ready' | 'rejected' | 'claimed';

export interface ComposerAttachment {
  key: string;
  fileName: string;
  id?: string;
  status: AttachmentStatus;
  error?: string;
}

interface AttachmentComposerProps {
  attachments: AttachmentsApi;
  disabled?: boolean;
  resetKey?: string | number;
  claimSignal?: number;
  put?: Fetcher;
  onSelectionChange(ids: string[], ready: boolean): void;
}

const nextKey = () => (globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`);

function selectionFrom(files: ComposerAttachment[]): { ids: string[]; ready: boolean } {
  const ids = files.filter((file) => file.status === 'ready' && file.id).map((file) => file.id!);
  const ready = files.every((file) => file.status === 'ready' || file.status === 'claimed');
  return { ids, ready };
}

export function AttachmentComposer({ attachments, disabled, resetKey, claimSignal, put = fetch, onSelectionChange }: AttachmentComposerProps) {
  const fileInput = useRef<HTMLInputElement>(null);
  const [files, setFiles] = useState<ComposerAttachment[]>([]);
  const onSelectionChangeRef = useRef(onSelectionChange);
  onSelectionChangeRef.current = onSelectionChange;

  useEffect(() => { setFiles([]); }, [resetKey]);

  useEffect(() => {
    if (!claimSignal) return;
    setFiles((current) => current.map((file) => (file.status === 'ready' ? { ...file, status: 'claimed' as const } : file)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [claimSignal]);

  // Report the derived selection after render so the parent is never updated from inside a state
  // updater (React forbids setState-on-another-component during render).
  useEffect(() => {
    const { ids, ready } = selectionFrom(files);
    onSelectionChangeRef.current(ids, ready);
  }, [files]);

  const putBytes = async (staged: StagedAttachment, file: File) => {
    const response = await put(staged.uploadUrl, { method: 'PUT', headers: { 'Content-Type': staged.contentType }, body: file });
    if (!response.ok) throw new Error(`Upload failed with status ${response.status}.`);
  };

  const uploadOne = async (file: File) => {
    const key = nextKey();
    setFiles((current) => [...current.filter((entry) => entry.status !== 'claimed'), { key, fileName: file.name, status: 'uploading' }]);
    try {
      const staged = await attachments.stage(file.name, file.type || 'application/octet-stream', file.size);
      setFiles((current) => current.map((entry) => (entry.key === key ? { ...entry, status: 'scanning' } : entry)));
      await putBytes(staged, file);
      await attachments.complete(staged.id);
      setFiles((current) => current.map((entry) => (entry.key === key ? { ...entry, id: staged.id, status: 'ready' } : entry)));
    } catch (error) {
      setFiles((current) => current.map((entry) => (entry.key === key ? { ...entry, status: 'rejected', error: error instanceof Error ? error.message : 'The attachment was rejected.' } : entry)));
    }
  };

  const attach = async (selected: FileList | null) => {
    if (!selected || selected.length === 0) return;
    for (const file of Array.from(selected)) void uploadOne(file);
  };

  const remove = (key: string) => {
    setFiles((current) => current.filter((file) => file.key !== key));
  };

  const statusLabel = (status: AttachmentStatus) => status === 'ready' ? 'Ready' : status === 'claimed' ? 'Claimed' : status === 'rejected' ? 'Rejected' : status === 'scanning' ? 'Scanning…' : 'Uploading…';

  return <span className="attachment-composer">
    <button type="button" aria-label="Add attachment" disabled={disabled} onClick={() => fileInput.current?.click()}>Attach</button>
    <input ref={fileInput} type="file" hidden multiple aria-label="Attach files" disabled={disabled} onChange={(event) => { void attach(event.target.files); event.target.value = ''; }} />
    {files.map((file) => <span key={file.key} className={`attachment-item is-${file.status}`}>
      <span>{file.fileName} — <span role="status" aria-label={`${file.fileName} ${statusLabel(file.status)}`}>{statusLabel(file.status)}</span></span>
      {file.status === 'rejected' && file.error && <small role="alert">{file.error}</small>}
      <button type="button" aria-label={`Remove ${file.fileName}`} onClick={() => remove(file.key)}>Remove</button>
    </span>)}
  </span>;
}
