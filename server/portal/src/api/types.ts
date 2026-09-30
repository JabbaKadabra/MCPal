export type Role = 'owner' | 'member';

export interface Me {
  email: string;
  companyId: string;
  companyName: string;
  role: Role;
}

export interface TeamMember {
  id: string;
  email: string;
  role: Role;
  emailConfirmed: boolean;
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

export type KeyPurpose = 'any' | 'bridge' | 'client';

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  disabled: boolean;
  purpose: KeyPurpose;
  /** Empty means all servers. */
  allowedServers: string[];
  /** Email of the user who created the key; null for keys from before users had roles. */
  createdBy?: string | null;
}

export interface NewApiKey {
  purpose: KeyPurpose;
  allowedServers: string[];
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
  allowedServers: string[];
}

export interface Connection {
  bridgeName: string;
  bridgeVersion: string;
  updateAvailable: boolean;
  latestBridgeVersion: string | null;
  connectedAt: string;
  apiKeyName: string | null;
  servers: { name: string; tools: string[] }[];
  rejected: { server: string; reason: string }[];
  rejectedTools: { server: string; tool: string; reason: string }[];
}

export interface ConnectInfo {
  mcpUrl: string;
  issuer: string;
  claudeCodeCommand: string;
  claudeCodeCommandWithHeader: string;
}

export interface AuthorizeContext {
  clientName: string;
  redirectHost: string;
  signedInCompany: string | null;
}

export type AuditOutcome = 'ok' | 'tool_error' | 'timeout' | 'offline' | 'relay_error' | 'cancelled';

export interface AuditEntry {
  id: string;
  occurredAt: string;
  durationMs: number;
  authKind: 'apikey' | 'oauth';
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
  from: string;
  to: string;
}
