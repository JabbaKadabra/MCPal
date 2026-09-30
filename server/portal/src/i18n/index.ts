import { en, type MessageKey } from './en';

/** Looks up a UI string and fills `{name}` placeholders. Every user-facing string goes through here. */
export function t(key: MessageKey, params: Record<string, string> = {}): string {
  return en[key].replace(/\{(\w+)\}/g, (match, name: string) => params[name] ?? match);
}

export type { MessageKey };
