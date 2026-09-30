import { vi } from 'vitest';

export interface RecordedCall {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: unknown;
}

type Handler = (call: RecordedCall) => { status?: number; body?: unknown } | undefined;

/** Replaces global fetch with a scripted fake and records every call. */
export function mockFetch(handler: Handler): RecordedCall[] {
  const calls: RecordedCall[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const call: RecordedCall = {
        url: input,
        method: init?.method ?? 'GET',
        headers: (init?.headers ?? {}) as Record<string, string>,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
      };
      calls.push(call);
      const result = handler(call) ?? { status: 404, body: { errors: ['not scripted'] } };
      const status = result.status ?? 200;
      const text = result.body === undefined ? '' : JSON.stringify(result.body);
      return Promise.resolve(new Response(status === 204 ? null : text, { status }));
    }),
  );
  return calls;
}
