import { request, type Fetcher } from './client';

export interface StagedAttachment {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  expiresAt: string;
  objectKey: string;
  uploadUrl: string;
}

export interface AttachmentDownload { downloadUrl: string; contentType: string; fileName: string; expiresAt: string }

export function createAttachmentsApi(getToken: () => string | null, fetcher: Fetcher = fetch) {
  const headers = (): HeadersInit => {
    const token = getToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  };
  const endpoint = (path: string) => `/api/v1${path}`;
  return {
    stage: (fileName: string, contentType: string, size: number) =>
      request<StagedAttachment>(fetcher, endpoint('/attachments/staging'), { method: 'POST', headers: headers(), body: { fileName, contentType, size } }),
    complete: (id: string) => request<{ completed: boolean }>(fetcher, endpoint(`/attachments/${id}/complete`), { method: 'POST', headers: headers() }),
    download: (id: string) => request<AttachmentDownload>(fetcher, endpoint(`/attachments/${id}/download`), { headers: headers() }),
    /**
     * Direct-to-storage upload: bytes go straight to the presigned URL, the API
     * only stages metadata and verifies the completed upload afterwards.
     */
    upload: async (file: File, put: Fetcher = fetch): Promise<string> => {
      const api = {
        stage: (fileName: string, contentType: string, size: number) =>
          request<StagedAttachment>(fetcher, endpoint('/attachments/staging'), { method: 'POST', headers: headers(), body: { fileName, contentType, size } }),
        complete: (id: string) => request<{ completed: boolean }>(fetcher, endpoint(`/attachments/${id}/complete`), { method: 'POST', headers: headers() }),
      };
      const staged = await api.stage(file.name, file.type || 'application/octet-stream', file.size);
      const putResponse = await put(staged.uploadUrl, { method: 'PUT', headers: { 'Content-Type': staged.contentType }, body: file });
      if (!putResponse.ok) throw new Error(`Upload failed with status ${putResponse.status}.`);
      await api.complete(staged.id);
      return staged.id;
    },
  };
}

export type AttachmentsApi = ReturnType<typeof createAttachmentsApi>;
