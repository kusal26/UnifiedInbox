export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly code?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export type Fetcher = typeof fetch;

type RequestOptions = Omit<RequestInit, 'body' | 'headers'> & {
  body?: unknown;
  headers?: HeadersInit;
};

function messageFromPayload(payload: unknown, fallback: string): string {
  if (typeof payload === 'string') return payload;
  if (payload && typeof payload === 'object') {
    const record = payload as Record<string, unknown>;
    if (typeof record.detail === 'string') return record.detail;
    if (typeof record.message === 'string') return record.message;
    if (typeof record.error === 'string') return record.error;
    if (typeof record.title === 'string') return record.title;
  }
  return fallback;
}

function codeFromPayload(payload: unknown): string | undefined {
  if (payload && typeof payload === 'object') {
    const record = payload as Record<string, unknown>;
    return typeof record.code === 'string' ? record.code : undefined;
  }
  return undefined;
}

async function readPayload(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) return undefined;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export async function request<T>(
  fetcher: Fetcher,
  url: string,
  options: RequestOptions = {},
): Promise<T> {
  const { body, headers: optionHeaders, ...requestOptions } = options;
  const headers: Record<string, string> = optionHeaders instanceof Headers
    ? Object.fromEntries(optionHeaders.entries())
    : Array.isArray(optionHeaders)
      ? Object.fromEntries(optionHeaders)
      : { ...optionHeaders };
  const init: RequestInit = { ...requestOptions, headers };

  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }

  const response = await fetcher(url, init);
  const payload = await readPayload(response);
  if (!response.ok) throw new ApiError(response.status, messageFromPayload(payload, response.statusText), codeFromPayload(payload));
  return payload as T;
}
