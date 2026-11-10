import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router';
import { api, formatBytes, formatDate, formatDuration } from '../api';
import { Player } from '../components/Player';
import { StatusBadge } from '../components/StatusBadge';
import type { AdminPreview, AdminVideoDetail } from '../types';

export function WatchPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const client = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');

  const detail = useQuery({
    queryKey: ['video', id],
    queryFn: ({ signal }) => api<AdminVideoDetail>(`/videos/${id}`, { signal }),
    refetchInterval: (query) => (query.state.data && ['Queued', 'Processing', 'Uploading'].includes(query.state.data.status) ? 3000 : false),
  });

  const ready = detail.data?.status === 'Ready' && detail.data.applicationStatus === 'Active';
  const preview = useQuery({
    queryKey: ['preview', id],
    queryFn: () => api<AdminPreview>(`/videos/${id}/preview`, { method: 'POST' }),
    enabled: ready,
    staleTime: Infinity,
    gcTime: 0,
  });

  const videoTitle = detail.data?.title ?? '';
  const source = useMemo(() => (preview.data ? { ...preview.data, title: videoTitle } : null), [preview.data, videoTitle]);

  const invalidate = () => {
    void client.invalidateQueries({ queryKey: ['video', id] });
    void client.invalidateQueries({ queryKey: ['videos'] });
  };

  const save = useMutation({
    mutationFn: () => api<undefined>(`/videos/${id}`, { method: 'PATCH', body: { title, description } }),
    onSuccess: () => {
      setEditing(false);
      invalidate();
    },
  });

  const reprocess = useMutation({
    mutationFn: () => api<undefined>(`/videos/${id}/reprocess`, { method: 'POST' }),
    onSuccess: invalidate,
  });

  const remove = useMutation({
    mutationFn: () => api<undefined>(`/videos/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['videos'] });
      navigate('/');
    },
  });

  if (detail.isPending) return <p className="muted">Loading…</p>;
  if (detail.isError) return <p className="error">{detail.error.message}</p>;
  const video = detail.data;

  const startEdit = () => {
    setTitle(video.title);
    setDescription(video.description ?? '');
    setEditing(true);
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <div className="watch">
      <div className="watch-main">
        <div className="watch-stage">
          {source ? (
            <Player source={source} />
          ) : (
            <div className="watch-placeholder">
              <StatusBadge status={video.status} />
              {video.error && <p>{video.error}</p>}
            </div>
          )}
        </div>

        {editing ? (
          <form className="stack edit-form" onSubmit={submit}>
            <label>
              Title
              <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} required />
            </label>
            <label>
              Description
              <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={4} maxLength={5000} />
            </label>
            {save.isError && <p className="error">{save.error.message}</p>}
            <div className="row">
              <button className="button primary" type="submit" disabled={save.isPending}>
                Save
              </button>
              <button className="button" type="button" onClick={() => setEditing(false)}>
                Cancel
              </button>
            </div>
          </form>
        ) : (
          <>
            <h1 className="watch-title">{video.title}</h1>
            <div className="row watch-actions">
              <span className="muted">
                {video.application}
                {video.applicationStatus !== 'Active' && ' (missing from config)'}
              </span>
              <span className="spacer" />
              <button className="button" type="button" onClick={startEdit}>
                Edit
              </button>
              <button
                className="button"
                type="button"
                disabled={!video.hasSource || !['Ready', 'Failed'].includes(video.status) || reprocess.isPending}
                title={video.hasSource ? 'Encode again from the kept original. The video is unavailable until it finishes.' : 'The original is no longer kept'}
                onClick={() => reprocess.mutate()}
              >
                Re-process
              </button>
              <button
                className="button danger"
                type="button"
                disabled={remove.isPending}
                onClick={() => {
                  if (window.confirm(`Delete “${video.title}” and all of its files? This cannot be undone.`)) remove.mutate();
                }}
              >
                Delete
              </button>
            </div>
            {(reprocess.isError || remove.isError) && <p className="error">{(reprocess.error ?? remove.error)?.message}</p>}
            {video.description && <div className="description">{video.description}</div>}
          </>
        )}
      </div>

      <aside className="watch-side card">
        <h2>Details</h2>
        <dl className="facts">
          <dt>Status</dt>
          <dd>
            <StatusBadge status={video.status} />
          </dd>
          <dt>Video id</dt>
          <dd className="mono">{video.id}</dd>
          {video.externalId && (
            <>
              <dt>External id</dt>
              <dd className="mono">{video.externalId}</dd>
            </>
          )}
          <dt>Duration</dt>
          <dd>{formatDuration(video.durationMs) || '—'}</dd>
          <dt>Resolution</dt>
          <dd>{video.width && video.height ? `${video.width}×${video.height}` : '—'}</dd>
          <dt>Codecs</dt>
          <dd>{[video.videoCodec, video.audioCodec].filter(Boolean).join(' / ') || '—'}</dd>
          <dt>Processing</dt>
          <dd>{video.processingMode ?? '—'}</dd>
          <dt>Stream size</dt>
          <dd>{formatBytes(video.outputBytes)}</dd>
          <dt>Original</dt>
          <dd>{video.hasSource ? `${formatBytes(video.sourceBytes)}${video.originalFileName ? ` · ${video.originalFileName}` : ''}` : 'Not kept'}</dd>
          <dt>Uploaded</dt>
          <dd>
            {formatDate(video.createdAt)} by {video.createdBy}
          </dd>
          {video.readyAt && (
            <>
              <dt>Ready</dt>
              <dd>{formatDate(video.readyAt)}</dd>
            </>
          )}
        </dl>

        {video.jobs.length > 0 && (
          <>
            <h2>Processing history</h2>
            <ul className="jobs">
              {video.jobs.map((job) => (
                <li key={job.createdAt}>
                  <StatusBadge status={job.status} /> attempt {job.attempt}/{job.maxAttempts}
                  {job.status === 'Running' && ` · ${job.progressPercent.toFixed(0)}%`} · {job.reason} · {formatDate(job.createdAt)}
                  {job.lastError && <div className="muted job-error">{job.lastError}</div>}
                </li>
              ))}
            </ul>
          </>
        )}
      </aside>
    </div>
  );
}
