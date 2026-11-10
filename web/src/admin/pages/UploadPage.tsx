import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { Upload } from 'tus-js-client';
import { api, formatBytes } from '../api';

interface ApplicationOption {
  name: string;
  displayName: string;
  status: string;
}

interface UploadTicket {
  videoId: string;
  uploadUrl: string;
  uploadToken: string;
  expiresAt: string;
  maxBytes: number;
}

type Phase = { kind: 'idle' } | { kind: 'uploading'; sent: number; total: number } | { kind: 'done'; videoId: string } | { kind: 'error'; message: string };

const CHUNK_SIZE = 50 * 1024 * 1024; // fits reverse proxies with body-size limits

export function UploadPage() {
  const client = useQueryClient();
  const apps = useQuery({ queryKey: ['application-options'], queryFn: () => api<ApplicationOption[]>('/application-options') });
  const activeApps = apps.data?.filter((a) => a.status === 'Active') ?? [];

  const [application, setApplication] = useState('');
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const upload = useRef<Upload | null>(null);

  useEffect(() => () => void upload.current?.abort(), []);

  const chosenApp = application || activeApps[0]?.name || '';

  const start = async (event: FormEvent) => {
    event.preventDefault();
    if (!file || !chosenApp) return;
    setPhase({ kind: 'uploading', sent: 0, total: file.size });

    let ticket: UploadTicket;
    try {
      ticket = await api<UploadTicket>('/uploads', {
        method: 'POST',
        body: { application: chosenApp, title: title.trim() || file.name, filename: file.name, sizeBytes: file.size, description: description || null },
      });
    } catch (error) {
      setPhase({ kind: 'error', message: (error as Error).message });
      return;
    }

    const tus = new Upload(file, {
      endpoint: ticket.uploadUrl,
      headers: { Authorization: `Bearer ${ticket.uploadToken}` },
      chunkSize: CHUNK_SIZE,
      retryDelays: [0, 1000, 3000, 5000, 10000],
      removeFingerprintOnSuccess: true,
      storeFingerprintForResuming: false, // a token can only resume its own upload in this page session
      metadata: { filename: file.name },
      onProgress: (sent, total) => setPhase({ kind: 'uploading', sent, total }),
      onSuccess: () => {
        setPhase({ kind: 'done', videoId: ticket.videoId });
        void client.invalidateQueries({ queryKey: ['videos'] });
        void client.invalidateQueries({ queryKey: ['jobs'] });
      },
      onError: (error) => setPhase({ kind: 'error', message: error.message }),
    });
    upload.current = tus;
    tus.start();
  };

  const cancel = () => {
    void upload.current?.abort(true);
    setPhase({ kind: 'error', message: 'Upload cancelled.' });
  };

  const busy = phase.kind === 'uploading';
  const percent = phase.kind === 'uploading' && phase.total > 0 ? (phase.sent / phase.total) * 100 : 0;

  return (
    <section className="narrow">
      <h1 className="page-title">Upload video</h1>
      <form className="card stack" onSubmit={start}>
        <label>
          Application
          <select value={chosenApp} onChange={(e) => setApplication(e.target.value)} disabled={busy || activeApps.length === 0} required>
            {activeApps.map((app) => (
              <option key={app.name} value={app.name}>
                {app.displayName === app.name ? app.name : `${app.displayName} (${app.name})`}
              </option>
            ))}
          </select>
        </label>
        {apps.isSuccess && activeApps.length === 0 && <p className="error">No applications are configured.</p>}
        <label>
          File
          <input
            type="file"
            accept="video/*,.mkv,.avi,.ts,.m2ts,.flv,.wmv,.webm,.mov,.mp4"
            disabled={busy}
            onChange={(e) => {
              const picked = e.target.files?.[0] ?? null;
              setFile(picked);
              if (picked && !title) setTitle(picked.name.replace(/\.[^.]+$/, ''));
            }}
            required
          />
        </label>
        {file && <p className="muted">{formatBytes(file.size)}</p>}
        <label>
          Title
          <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} disabled={busy} required />
        </label>
        <label>
          Description
          <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={3} maxLength={5000} disabled={busy} />
        </label>

        {phase.kind === 'uploading' && (
          <div className="stack">
            <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)}>
              <div className="progress-bar" data-percent={Math.round(percent)} ref={(el) => el?.style.setProperty('width', `${percent}%`)} />
            </div>
            <p className="muted">
              {formatBytes(phase.sent)} of {formatBytes(phase.total)} ({percent.toFixed(1)}%)
            </p>
          </div>
        )}
        {phase.kind === 'done' && (
          <p>
            Uploaded. It is being processed now. <Link to={`/watch/${phase.videoId}`} className="button text">Open video</Link>
          </p>
        )}
        {phase.kind === 'error' && <p className="error">{phase.message}</p>}

        <div className="row">
          {busy ? (
            <button type="button" className="button" onClick={cancel}>
              Cancel
            </button>
          ) : (
            <button type="submit" className="button primary" disabled={!file || !chosenApp}>
              Upload
            </button>
          )}
        </div>
      </form>
    </section>
  );
}
