import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { api, formatBytes, formatDate } from '../api';
import { StatusBadge } from '../components/StatusBadge';

interface ApplicationSummary {
  name: string;
  displayName: string;
  status: string;
  allowedOrigins: string[];
  webhookUrl?: string | null;
  videoCount: number;
  storageBytes: number;
  pendingDeliveries: number;
  deadDeliveries: number;
  createdAt: string;
  lastSyncedAt: string;
  missingSince?: string | null;
}

interface DeliveryRow {
  eventId: string;
  type: string;
  status: string;
  attempt: number;
  lastStatusCode?: number | null;
  lastError?: string | null;
  createdAt: string;
  deliveredAt?: string | null;
  nextAttemptAt: string;
}

function Deliveries({ app }: { app: ApplicationSummary }) {
  const client = useQueryClient();
  const deliveries = useQuery({
    queryKey: ['deliveries', app.name],
    queryFn: () => api<DeliveryRow[]>(`/applications/${encodeURIComponent(app.name)}/webhooks`),
    refetchInterval: 5000,
  });
  const resend = useMutation({
    mutationFn: (eventId: string) => api<undefined>(`/applications/${encodeURIComponent(app.name)}/webhooks/${encodeURIComponent(eventId)}/resend`, { method: 'POST' }),
    onSuccess: () => void client.invalidateQueries({ queryKey: ['deliveries', app.name] }),
  });

  if (deliveries.isPending) return <p className="muted">Loading deliveries…</p>;
  if (deliveries.isError) return <p className="error">{deliveries.error.message}</p>;
  if (deliveries.data.length === 0) return <p className="muted">No webhook deliveries yet.</p>;

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Event</th>
            <th>Status</th>
            <th>Attempts</th>
            <th>Response</th>
            <th>Created</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {deliveries.data.map((d) => (
            <tr key={d.eventId}>
              <td>
                {d.type}
                <div className="muted mono">{d.eventId}</div>
              </td>
              <td>
                <StatusBadge status={d.status} />
                {d.status === 'Pending' && d.attempt > 0 && <div className="muted">next {formatDate(d.nextAttemptAt)}</div>}
              </td>
              <td>{d.attempt}</td>
              <td>
                {d.lastStatusCode ?? '—'}
                {d.lastError && <div className="muted job-error">{d.lastError}</div>}
              </td>
              <td>{formatDate(d.createdAt)}</td>
              <td>
                {d.status !== 'Pending' && (
                  <button type="button" className="button text" disabled={resend.isPending} onClick={() => resend.mutate(d.eventId)}>
                    Resend
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ApplicationCard({ app }: { app: ApplicationSummary }) {
  const client = useQueryClient();
  const [showDeliveries, setShowDeliveries] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const refresh = () => void client.invalidateQueries({ queryKey: ['applications'] });

  const test = useMutation({
    mutationFn: () => api<undefined>(`/applications/${encodeURIComponent(app.name)}/webhooks/test`, { method: 'POST' }),
    onSuccess: () => {
      setNotice('Test event queued.');
      setShowDeliveries(true);
      void client.invalidateQueries({ queryKey: ['deliveries', app.name] });
    },
  });
  const revoke = useMutation({
    mutationFn: () => api<undefined>(`/applications/${encodeURIComponent(app.name)}/revoke-tokens`, { method: 'POST' }),
    onSuccess: () => setNotice('All outstanding links for this application were revoked.'),
  });
  const purge = useMutation({
    mutationFn: () => api<undefined>(`/applications/${encodeURIComponent(app.name)}`, { method: 'DELETE' }),
    onSuccess: refresh,
  });
  const error = test.error ?? revoke.error ?? purge.error;

  return (
    <article className="card stack app-card">
      <div className="page-head">
        <div>
          <h2>
            {app.displayName} {app.displayName !== app.name && <span className="muted">({app.name})</span>}
          </h2>
          <StatusBadge status={app.status} />
          {app.missingSince && <span className="muted"> since {formatDate(app.missingSince)}</span>}
        </div>
        <div className="row">
          <Link className="button" to={`/?app=${encodeURIComponent(app.name)}`}>
            Videos
          </Link>
          {app.webhookUrl && app.status === 'Active' && (
            <button type="button" className="button" disabled={test.isPending} onClick={() => test.mutate()}>
              Send test event
            </button>
          )}
          <button
            type="button"
            className="button"
            disabled={revoke.isPending}
            onClick={() => {
              if (window.confirm(`Revoke every embed, playback and upload link issued for ${app.name}? Viewers will need fresh links.`)) revoke.mutate();
            }}
          >
            Revoke links
          </button>
          {app.status === 'Missing' && (
            <button
              type="button"
              className="button danger"
              disabled={purge.isPending}
              onClick={() => {
                if (window.prompt(`This permanently deletes ${app.name} and all ${app.videoCount} of its videos. Type the name to confirm.`) === app.name) purge.mutate();
              }}
            >
              Purge
            </button>
          )}
        </div>
      </div>

      <dl className="facts">
        <dt>Videos</dt>
        <dd>
          {app.videoCount} · {formatBytes(app.storageBytes)}
        </dd>
        <dt>Allowed origins</dt>
        <dd>{app.allowedOrigins.length ? app.allowedOrigins.join(', ') : <span className="muted">None (cannot embed)</span>}</dd>
        <dt>Webhook</dt>
        <dd>
          {app.webhookUrl ?? <span className="muted">Not configured</span>}
          {(app.pendingDeliveries > 0 || app.deadDeliveries > 0) && (
            <span className="muted">
              {' '}
              · {app.pendingDeliveries} pending, {app.deadDeliveries} failed
            </span>
          )}
        </dd>
        <dt>Registered</dt>
        <dd>{formatDate(app.createdAt)}</dd>
      </dl>

      {notice && <p className="muted">{notice}</p>}
      {error && <p className="error">{error.message}</p>}

      {app.webhookUrl && (
        <div>
          <button type="button" className="button text" onClick={() => setShowDeliveries((v) => !v)}>
            {showDeliveries ? 'Hide deliveries' : 'Show deliveries'}
          </button>
          {showDeliveries && <Deliveries app={app} />}
        </div>
      )}
    </article>
  );
}

export function ApplicationsPage() {
  const apps = useQuery({ queryKey: ['applications'], queryFn: () => api<ApplicationSummary[]>('/applications') });

  return (
    <section className="stack">
      <div className="page-head">
        <h1 className="page-title">Applications</h1>
        <span className="muted">Declared in config/appsettings.json; keys come only from ApiKeys__&lt;Name&gt; environment variables.</span>
      </div>
      {apps.isPending && <p className="muted">Loading…</p>}
      {apps.isError && <p className="error">{apps.error.message}</p>}
      {apps.data?.length === 0 && <p className="muted">No applications are configured.</p>}
      {apps.data?.map((app) => <ApplicationCard key={app.name} app={app} />)}
    </section>
  );
}
