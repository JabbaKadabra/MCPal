export type Role = 'owner' | 'member';

export interface Me {
  email: string;
  companyId: string;
  companyName: string;
  role: Role;
  displayName?: string | null;
}

export interface TeamMember {
  id: string;
  email: string;
  displayName?: string | null;
  role: Role;
  emailConfirmed: boolean;
  disabled: boolean;
  /** Groups the user was added to; `Everyone` is implicit and not listed. */
  groups: string[];
}

export interface Invitation {
  id: string;
  email: string;
  role: Role;
  expiresAt: string;
  createdAt: string;
  invitedBy: string | null;
}

export interface Team {
  users: TeamMember[];
  invitations: Invitation[];
}

export interface InvitationPreview {
  companyName: string;
  email: string;
  role: Role;
}

/** A personal access token acts as its user; a bridge key belongs to the company and only opens tunnels. */
export type KeyPurpose = 'personal' | 'bridge';

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  disabled: boolean;
  purpose: KeyPurpose;
  /** The user a personal access token acts as; null for bridge keys. */
  userEmail?: string | null;
  /** Email of the user who created the key; null for keys from before users had roles. */
  createdBy?: string | null;
}

export interface NewApiKey {
  purpose: KeyPurpose;
  /** ISO 8601 instant; omitted for a key that never expires. */
  expiresAt?: string;
}

export interface CreatedApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  expiresAt: string | null;
  key: string;
  purpose: KeyPurpose;
}

export interface Connection {
  bridgeName: string;
  bridgeVersion: string;
  updateAvailable: boolean;
  latestBridgeVersion: string | null;
  connectedAt: string;
  apiKeyName: string | null;
  /** True for bridges of protocol 1.2 or newer: they hand the caller to local servers. */
  supportsUserContext: boolean;
  /** `audience` is the `aud` claim of caller tokens for this server. */
  servers: { name: string; tools: string[]; audience: string }[];
  rejected: { server: string; reason: string }[];
  rejectedTools: { server: string; tool: string; reason: string }[];
}

export interface ConnectInfo {
  mcpUrl: string;
  issuer: string;
  claudeCodeCommand: string;
  claudeCodeCommandWithHeader: string;
}

export interface BridgeDownload {
  rid: string;
  os: string;
  fileName: string;
  url: string;
}

export interface Setup {
  mcpalUrl: string;
  /** Version of the newest bridge release; null when the server does not know it. */
  bridgeVersion: string | null;
  /** Empty when the version is unknown: link to `releasesUrl` then. */
  downloads: BridgeDownload[];
  releasesUrl: string;
  checksumsUrl: string | null;
  /** The bridge container image with tag: the newest version, or `latest` when the server does not know it. */
  imageReference: string;
  /** The pre-filled bridge config: server URL, no key, no servers. */
  configJson: string;
  /** A first mcp.json: an echo server for owners who have no MCP config yet. */
  sampleMcpJson: string;
  /** True once a bridge connected at least once. */
  hasConnectedBridge: boolean;
}

export interface AuthorizeContext {
  clientName: string;
  redirectHost: string;
  signedInEmail: string | null;
  signedInCompany: string | null;
}

export type AuditOutcome = 'ok' | 'tool_error' | 'timeout' | 'offline' | 'relay_error' | 'cancelled';

export interface AuditEntry {
  id: string;
  occurredAt: string;
  durationMs: number;
  authKind: 'apikey' | 'pat' | 'oauth';
  /** Null for rows from before calls were tied to users. */
  userId: string | null;
  /** Null when the user was removed since. */
  userEmail: string | null;
  apiKeyId: string | null;
  apiKeyName: string | null;
  oauthClientId: string | null;
  bridgeName: string;
  serverName: string;
  toolName: string;
  publicName: string;
  outcome: AuditOutcome;
  errorMessage: string | null;
}

export interface AuditPage {
  items: AuditEntry[];
  nextCursor: string | null;
}

/** Empty strings mean "no filter". Dates are ISO 8601 strings. */
export interface AuditFilters {
  tool: string;
  outcome: string;
  keyId: string;
  userId: string;
  from: string;
  to: string;
}

export interface Grant {
  id: string;
  serverPattern: string;
  toolPatterns: string[];
}

export interface Group {
  id: string;
  name: string;
  /** All active users belong to it implicitly; it cannot be renamed, deleted or given members. */
  isEveryone: boolean;
  externalId?: string | null;
  memberIds: string[];
  grants: Grant[];
}

export interface VisibleTool {
  server: string;
  tool: string;
  publicName: string;
}

export interface UserAccess {
  userId: string;
  email: string;
  role: Role;
  disabled: boolean;
  /** Owners may use every tool regardless of grants. */
  allTools: boolean;
  groups: string[];
  tools: VisibleTool[];
}
