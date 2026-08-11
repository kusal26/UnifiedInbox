export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
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
    if (typeof record.message === 'string') return record.message;
    if (typeof record.error === 'string') return record.error;
    if (typeof record.title === 'string') return record.title;
  }
  return fallback;
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
  const headers: Record<string, string> = options.headers instanceof Headers
    ? Object.fromEntries(options.headers.entries())
    : Array.isArray(options.headers)
      ? Object.fromEntries(options.headers)
      : { ...options.headers };
  const init: RequestInit = { ...options, headers };

  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(options.body);
  }

  const response = await fetcher(url, init);
  const payload = await readPayload(response);
  if (!response.ok) throw new ApiError(response.status, messageFromPayload(payload, response.statusText));
  return payload as T;
}
