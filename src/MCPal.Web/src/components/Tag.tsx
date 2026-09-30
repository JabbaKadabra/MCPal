import type { ReactNode } from 'react';

export type TagTone = 'agent' | 'claude' | 'any' | 'ok' | 'warn' | 'bad' | 'off' | 'ink' | 'plain';

/** A small bordered label whose fill carries meaning: cable colours for key purposes, signal colours for states. */
export function Tag({ tone, children }: { tone: TagTone; children: ReactNode }) {
  return <span className={`tag tag-${tone}`}>{children}</span>;
}
