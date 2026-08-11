export type ActivityPayload<T> = { items: T[]; nextCursor: string | null } | T[];

export function normalizeActivity<T>(payload: ActivityPayload<T>): T[] {
  return Array.isArray(payload) ? payload : payload.items;
}
