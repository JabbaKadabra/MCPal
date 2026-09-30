/**
 * Glob match with the wildcards `*` (any run of characters) and `?` (exactly one), like the server's access grants.
 * Everything else is literal. Server patterns ignore case, tool patterns do not.
 */
export function globMatches(pattern: string, name: string, ignoreCase: boolean): boolean {
  const p = ignoreCase ? pattern.toLowerCase() : pattern;
  const n = ignoreCase ? name.toLowerCase() : name;
  let pi = 0;
  let ni = 0;
  let starIndex = -1;
  let matchIndex = 0;
  while (ni < n.length) {
    const pc = p[pi];
    if (pi < p.length && (pc === '?' || pc === n[ni])) {
      pi++;
      ni++;
    } else if (pi < p.length && pc === '*') {
      starIndex = pi;
      matchIndex = ni;
      pi++;
    } else if (starIndex !== -1) {
      pi = starIndex + 1;
      matchIndex++;
      ni = matchIndex;
    } else {
      return false;
    }
  }
  while (pi < p.length && p[pi] === '*') {
    pi++;
  }
  return pi === p.length;
}

/** A tool pattern is 1 to 128 characters of letters, digits, `_ - .` and the wildcards `*` and `?`. */
export function isValidToolPattern(pattern: string): boolean {
  return pattern.length >= 1 && pattern.length <= 128 && /^[A-Za-z0-9_\-.*?]+$/.test(pattern);
}

export interface OnlineTool {
  server: string;
  tool: string;
}

/** The online tools a grant would allow: what the editor previews before the owner saves. */
export function previewGrant(serverPattern: string, toolPatterns: string[], online: OnlineTool[]): OnlineTool[] {
  return online.filter(
    ({ server, tool }) => globMatches(serverPattern, server, true) && toolPatterns.some((pattern) => globMatches(pattern, tool, false)),
  );
}

/** Splits the comma separated text of a tool pattern field. */
export function parsePatterns(text: string): string[] {
  return [...new Set(text.split(',').map((pattern) => pattern.trim()).filter((pattern) => pattern.length > 0))];
}
