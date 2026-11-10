export interface AdminVideoItem {
  id: string;
  title: string;
  application: string;
  status: string;
  durationMs?: number | null;
  width?: number | null;
  height?: number | null;
  sizeBytes: number;
  thumbnailUrl?: string | null;
  createdAt: string;
}

export interface AdminVideoPage {
  items: AdminVideoItem[];
  nextCursor?: string | null;
}

export interface AdminJobItem {
  status: string;
  attempt: number;
  maxAttempts: number;
  progressPercent: number;
  stage?: string | null;
  lastError?: string | null;
  reason: string;
  createdAt: string;
  finishedAt?: string | null;
}

export interface AdminVideoDetail {
  id: string;
  title: string;
  description?: string | null;
  externalId?: string | null;
  application: string;
  applicationStatus: string;
  status: string;
  processingMode?: string | null;
  error?: string | null;
  durationMs?: number | null;
  width?: number | null;
  height?: number | null;
  videoCodec?: string | null;
  audioCodec?: string | null;
  sourceBytes: number;
  outputBytes: number;
  hasSource: boolean;
  originalFileName?: string | null;
  createdBy: string;
  createdAt: string;
  readyAt?: string | null;
  jobs: AdminJobItem[];
}

export interface AdminPreview {
  title: string;
  manifestUrl: string;
  refreshUrl: string;
  posterUrl: string;
  expiresAt: string;
}

export const VIDEO_STATUSES = ['Ready', 'Processing', 'Queued', 'Uploading', 'AwaitingUpload', 'Failed', 'UploadFailed', 'UploadExpired'] as const;

export function statusTone(status: string): 'ok' | 'warn' | 'bad' | '' {
  if (status === 'Ready' || status === 'Succeeded' || status === 'Active') return 'ok';
  if (status.includes('Fail') || status === 'Missing' || status === 'Dead' || status === 'UploadExpired') return 'bad';
  if (status === 'Processing' || status === 'Queued' || status === 'Uploading' || status === 'Running' || status === 'Pending') return 'warn';
  return '';
}

export function statusLabel(status: string): string {
  return status.replace(/([a-z])([A-Z])/g, '$1 $2');
}
