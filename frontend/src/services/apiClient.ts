import { config } from '../config';
import { acquireAccessToken } from './authConfig';

/** A problem details response (RFC 9457) as the API returns it. */
export interface ApiProblem {
  status: number;
  title: string;
  detail?: string;
  correlationId?: string;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ApiProblem,
  ) {
    super(problem.title);
    this.name = 'ApiError';
  }
}

export type FileContentType = 'html' | 'markdown';

export interface FileSummary {
  id: string;
  displayName: string;
  contentType: FileContentType;
  sizeBytes: number;
  ownerId: string;
  ownerDisplayName: string;
  uploadedAt: string;
  expiresAt: string;
  maxExpiresAt: string;
}

export interface FileDetail extends FileSummary {
  /** Carries a freshly minted preview token. Treat it as short-lived and never persist it. */
  previewUrl: string;
  accessScopeNotice: string;
  retentionNotice: string;
  isOwner: boolean;
}

/**
 * The normalized text projection.
 *
 * This is the anchoring surface. The preview iframe is sandboxed cross-origin and its DOM is
 * unreachable by design, so selection and anchoring happen against this instead — the same
 * artifact the server resolves against and agents read (research.md R2).
 */
export interface FileContent {
  fileId: string;
  displayName: string;
  contentType: FileContentType;
  renderVersion: string;
  text: string;
  expiresAt: string;
}

export interface QuotaStatus {
  liveFiles: number;
  maxLiveFiles: number;
}

/** The result of moving a file's expiry. */
export interface RetentionUpdate {
  fileId: string;
  /** What the expiry was before this change, so the owner can see what they actually did. */
  previousExpiresAt: string;
  expiresAt: string;
  /** The 30-day ceiling. Used to bound the picker; the server is what enforces it. */
  maxExpiresAt: string;
  retentionNotice: string;
}

export interface FileListResponse {
  files: FileSummary[];
  quota: QuotaStatus;
}

export type AnchorKind = 'text' | 'region';
export type AnchorState = 'anchored' | 'orphaned';

export interface Anchor {
  kind: AnchorKind;
  exact: string;
  prefix: string;
  suffix: string;
  start: number;
  end: number;
  containerPath?: string | null;
  fractionalRect?: { x: number; y: number; width: number; height: number } | null;
  renderVersion: string;
}

export interface Comment {
  id: string;
  fileId: string;
  threadId: string;
  parentId: string | null;
  body: string;
  authorId: string;
  authorDisplayName: string;
  actingAgentId: string | null;
  createdAt: string;
  editedAt: string | null;
  anchor: Anchor;
  anchorState: AnchorState;
  deletedAt: string | null;
}

export interface UserPreferences {
  notificationsEnabled: boolean;
}

/**
 * The single place the SPA talks to the API.
 *
 * A correlation id is generated per request and sent as a header. It comes back on the response
 * and on any problem detail, which is what lets a user report "something went wrong" with an
 * identifier an operator can actually find in the logs (Principle V).
 */
class ApiClient {
  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const token = await acquireAccessToken();
    const correlationId = crypto.randomUUID().replace(/-/g, '');

    const response = await fetch(`${config.apiOrigin}${path}`, {
      ...init,
      headers: {
        ...init.headers,
        Authorization: `Bearer ${token}`,
        'X-Correlation-Id': correlationId,
      },
    });

    if (!response.ok) {
      throw new ApiError(response.status, await this.readProblem(response));
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  private async readProblem(response: Response): Promise<ApiProblem> {
    try {
      const body = (await response.json()) as ApiProblem;
      return { ...body, status: response.status };
    } catch {
      return { status: response.status, title: 'The request failed.' };
    }
  }

  async listFiles(): Promise<FileListResponse> {
    return this.request<FileListResponse>('/api/files');
  }

  async getFile(fileId: string): Promise<FileDetail> {
    return this.request<FileDetail>(`/api/files/${encodeURIComponent(fileId)}`);
  }

  async getFileContent(fileId: string): Promise<FileContent> {
    return this.request<FileContent>(`/api/files/${encodeURIComponent(fileId)}/content`);
  }

  async uploadFile(file: File): Promise<FileDetail> {
    const token = await acquireAccessToken();
    const correlationId = crypto.randomUUID().replace(/-/g, '');

    const form = new FormData();
    form.append('file', file, file.name);

    // No Content-Type header: the browser must set the multipart boundary itself.
    const response = await fetch(`${config.apiOrigin}/api/files`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${token}`,
        'X-Correlation-Id': correlationId,
      },
      body: form,
    });

    if (!response.ok) {
      throw new ApiError(response.status, await this.readProblem(response));
    }

    return (await response.json()) as FileDetail;
  }

  async deleteFile(fileId: string): Promise<void> {
    await this.request<void>(`/api/files/${encodeURIComponent(fileId)}`, { method: 'DELETE' });
  }

  async extendRetention(fileId: string, expiresAt: string): Promise<RetentionUpdate> {
    return this.request<RetentionUpdate>(`/api/files/${encodeURIComponent(fileId)}/retention`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expiresAt }),
    });
  }

  async downloadBundle(fileId: string): Promise<Blob> {
    const token = await acquireAccessToken();
    const response = await fetch(`${config.apiOrigin}/api/files/${encodeURIComponent(fileId)}/download`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    if (!response.ok) {
      throw new ApiError(response.status, await this.readProblem(response));
    }

    return response.blob();
  }

  async listComments(fileId: string): Promise<Comment[]> {
    return this.request<Comment[]>(`/api/files/${encodeURIComponent(fileId)}/comments`);
  }

  async createComment(fileId: string, body: string, anchor: Anchor, parentId?: string): Promise<Comment> {
    // Note what is not sent: an author. Identity comes from the token, server side, always
    // (FR-005, FR-023).
    return this.request<Comment>(`/api/files/${encodeURIComponent(fileId)}/comments`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ body, anchor, parentId: parentId ?? null }),
    });
  }

  async editComment(fileId: string, commentId: string, body: string): Promise<Comment> {
    return this.request<Comment>(
      `/api/files/${encodeURIComponent(fileId)}/comments/${encodeURIComponent(commentId)}`,
      {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ body }),
      },
    );
  }

  async deleteComment(fileId: string, commentId: string): Promise<void> {
    await this.request<void>(
      `/api/files/${encodeURIComponent(fileId)}/comments/${encodeURIComponent(commentId)}`,
      { method: 'DELETE' },
    );
  }

  async getPreferences(): Promise<UserPreferences> {
    return this.request<UserPreferences>('/api/me/preferences');
  }

  async updatePreferences(preferences: UserPreferences): Promise<UserPreferences> {
    return this.request<UserPreferences>('/api/me/preferences', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(preferences),
    });
  }
}

export const apiClient = new ApiClient();
