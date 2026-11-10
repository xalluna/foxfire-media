import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import { api, formatBytes, formatDate } from '../api';

interface StorageSummary {
  drive: { mountPoint: string; format: string; totalBytes: number; freeBytes: number; usedBytes: number; freePercent: number };
  lowSpace: boolean;
  lowSpaceWarningPercent: number;
  videoBytes: number;
  sourceBytes: number;
  outputBytes: number;
  uploadsInProgressBytes: number;
  processingScratchBytes: number;
  perApplication: { application: string; videoCount: number; sourceBytes: number; outputBytes: number }[];
  largest: { id: string; title: string; application: string; bytes: number }[];
  lastCheck?: { at: string; orphanCount: number; orphanBytes: number; videosMissingFiles: string[]; sizesCorrected: number } | null;
}

function Meter({ summary }: { summary: StorageSummary }) {
  const { drive } = summary;
  const total = Math.max(drive.totalBytes, 1);
  const media = Math.min(summary.videoBytes + summary.uploadsInProgressBytes + summary.processingScratchBytes, drive.usedBytes);
  const other = Math.max(drive.usedBytes - media, 0);
  const width = (bytes: number) => `${(bytes / total) * 100}%`;

  return (
    <div className="stack">
      <div className="meter" role="img" aria-label={`${formatBytes(drive.freeBytes)} free of ${formatBytes(drive.totalBytes)}`}>
        <div className="meter-media" ref={(el) => el?.style.setProperty('width', width(media))} />
        <div className="meter-other" ref={(el) => el?.style.setProperty('width', width(other))} />
      </div>
      <div className="row legend">
        <span>
          <i className="swatch media" /> FoxfireMedia {formatBytes(media)}
        </span>
        <span>
          <i className="swatch other" /> Other {formatBytes(other)}
        </span>
        <span>
          <i className="swatch free" /> Free {formatBytes(drive.freeBytes)} ({drive.freePercent}%)
        </span>
      </div>
    </div>
  );
}

export function StoragePage() {
  const client = useQueryClient();
  const summary = useQuery({ queryKey: ['storage'], queryFn: () => api<StorageSummary>('/storage'), refetchInterval: 30_000 });
  const refresh = () => void client.invalidateQueries({ queryKey: ['storage'] });
  const check = useMutation({ mutationFn: () => api('/storage/check', { method: 'POST' }), onSuccess: refresh });
  const clean = useMutation({ mutationFn: () => api<undefined>('/storage/orphans/delete', { method: 'POST' }), onSuccess: refresh });

  if (summary.isPending) return <p className="muted">Loading…</p>;
  if (summary.isError) return <p className="error">{summary.error.message}</p>;
  const s = summary.data;

  return (
    <section className="stack">
      <div className="page-head">
        <h1 className="page-title">Storage</h1>
        <button type="button" className="button" disabled={check.isPending} onClick={() => check.mutate()}>
          {check.isPending ? 'Checking…' : 'Check now'}
        </button>
      </div>

      <div className={s.lowSpace ? 'card stack low-space' : 'card stack'}>
        <div className="page-head">
          <h2>
            {formatBytes(s.drive.freeBytes)} free of {formatBytes(s.drive.totalBytes)}
          </h2>
          <span className="muted mono">
            {s.drive.mountPoint} ({s.drive.format})
          </span>
        </div>
        {s.lowSpace && <p className="error">Free space is below {s.lowSpaceWarningPercent}%. New uploads may be refused.</p>}
        <Meter summary={s} />
      </div>

      <div className="stat-grid">
        <div className="card">
          <div className="muted">Streams (HLS)</div>
          <div className="stat">{formatBytes(s.outputBytes)}</div>
        </div>
        <div className="card">
          <div className="muted">Kept originals</div>
          <div className="stat">{formatBytes(s.sourceBytes)}</div>
        </div>
        <div className="card">
          <div className="muted">Uploads in progress</div>
          <div className="stat">{formatBytes(s.uploadsInProgressBytes)}</div>
        </div>
        <div className="card">
          <div className="muted">Processing scratch</div>
          <div className="stat">{formatBytes(s.processingScratchBytes)}</div>
        </div>
      </div>

      <div className="card table-wrap">
        <h2>By application</h2>
        <table className="table">
          <thead>
            <tr>
              <th>Application</th>
              <th>Videos</th>
              <th>Streams</th>
              <th>Originals</th>
              <th>Total</th>
            </tr>
          </thead>
          <tbody>
            {s.perApplication.map((a) => (
              <tr key={a.application}>
                <td>
                  <Link to={`/?app=${encodeURIComponent(a.application)}`}>{a.application}</Link>
                </td>
                <td>{a.videoCount}</td>
                <td>{formatBytes(a.outputBytes)}</td>
                <td>{formatBytes(a.sourceBytes)}</td>
                <td>{formatBytes(a.outputBytes + a.sourceBytes)}</td>
              </tr>
            ))}
            {s.perApplication.length === 0 && (
              <tr>
                <td colSpan={5} className="muted">
                  No videos yet.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="card table-wrap">
        <h2>Largest videos</h2>
        <table className="table">
          <tbody>
            {s.largest.map((v) => (
              <tr key={v.id}>
                <td>
                  <Link to={`/watch/${v.id}`}>{v.title}</Link>
                </td>
                <td className="muted">{v.application}</td>
                <td>{formatBytes(v.bytes)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="card stack">
        <h2>Consistency check</h2>
        {s.lastCheck ? (
          <>
            <p className="muted">Last run {formatDate(s.lastCheck.at)}.</p>
            <p>
              {s.lastCheck.orphanCount === 0
                ? 'No orphaned files.'
                : `${s.lastCheck.orphanCount} orphaned item(s) using ${formatBytes(s.lastCheck.orphanBytes)} (files with no video).`}
              {s.lastCheck.videosMissingFiles.length > 0 && ` ${s.lastCheck.videosMissingFiles.length} video(s) had missing files and were marked failed.`}
              {s.lastCheck.sizesCorrected > 0 && ` ${s.lastCheck.sizesCorrected} recorded size(s) corrected.`}
            </p>
            {s.lastCheck.orphanCount > 0 && (
              <div className="row">
                <button
                  type="button"
                  className="button danger"
                  disabled={clean.isPending}
                  onClick={() => {
                    if (window.confirm(`Delete ${s.lastCheck?.orphanCount} orphaned item(s) (${formatBytes(s.lastCheck?.orphanBytes ?? 0)})? This cannot be undone.`)) clean.mutate();
                  }}
                >
                  Delete orphaned files
                </button>
              </div>
            )}
          </>
        ) : (
          <p className="muted">Not run yet since the server started. It runs hourly, or use “Check now”.</p>
        )}
        {(check.isError || clean.isError) && <p className="error">{(check.error ?? clean.error)?.message}</p>}
      </div>
    </section>
  );
}
