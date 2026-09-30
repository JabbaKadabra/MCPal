export interface Me {
  email: string;
  companyId: string;
  companyName: string;
}

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  disabled: boolean;
}

export interface CreatedApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  expiresAt: string | null;
  key: string;
}

export interface Connection {
  agentName: string;
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
