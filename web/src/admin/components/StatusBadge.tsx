import { statusLabel, statusTone } from '../types';

export function StatusBadge({ status }: { status: string }) {
  return <span className={`badge ${statusTone(status)}`}>{statusLabel(status)}</span>;
}
