import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { api, formatDate } from '../api';
import { StatusBadge } from '../components/StatusBadge';

interface JobRow {
  videoId: string;
  videoTitle: string;
  application: string;
  status: string;
  attempt: number;
  maxAttempts: number;
  progressPercent: number;
  stage?: string | null;
  lastError?: string | null;
  reason: string;
  createdAt: string;
  startedAt?: string | null;
  finishedAt?: string | null;
}

export function ProcessingPage() {
  const jobs = useQuery({
    queryKey: ['jobs'],
    queryFn: ({ signal }) => api<JobRow[]>('/jobs', { signal }),
    refetchInterval: 3000,
  });

  return (
    <section>
      <div className="page-head">
        <h1 className="page-title">Processing</h1>
        <span className="muted">Refreshes every few seconds</span>
      </div>
      {jobs.isPending && <p className="muted">Loading…</p>}
      {jobs.isError && <p className="error">{jobs.error.message}</p>}
      {jobs.data?.length === 0 && <p className="muted">No processing jobs yet.</p>}
      {jobs.data && jobs.data.length > 0 && (
        <div className="card table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Video</th>
                <th>Application</th>
                <th>Status</th>
                <th>Progress</th>
                <th>Attempt</th>
                <th>Requested by</th>
                <th>Queued</th>
              </tr>
            </thead>
            <tbody>
              {jobs.data.map((job) => (
                <tr key={`${job.videoId}-${job.createdAt}`}>
                  <td>
                    <Link to={`/watch/${job.videoId}`}>{job.videoTitle}</Link>
                    {job.lastError && <div className="muted job-error">{job.lastError}</div>}
                  </td>
                  <td>{job.application}</td>
                  <td>
                    <StatusBadge status={job.status} />
                  </td>
                  <td>{job.status === 'Running' ? `${job.progressPercent.toFixed(0)}%` : job.status === 'Succeeded' ? '100%' : '—'}</td>
                  <td>
                    {job.attempt}/{job.maxAttempts}
                  </td>
                  <td>{job.reason}</td>
                  <td>{formatDate(job.createdAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
