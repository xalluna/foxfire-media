import { useInfiniteQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router';
import { api, formatBytes, formatDate, formatDuration } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import { VIDEO_STATUSES, statusLabel, type AdminVideoPage } from '../types';

export function LibraryPage() {
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const status = params.get('status') ?? '';
  const app = params.get('app') ?? '';

  const videos = useInfiniteQuery({
    queryKey: ['videos', { q, status, app }],
    initialPageParam: '',
    queryFn: ({ pageParam, signal }) => {
      const search = new URLSearchParams();
      if (q) search.set('q', q);
      if (status) search.set('status', status);
      if (app) search.set('app', app);
      if (pageParam) search.set('cursor', pageParam);
      return api<AdminVideoPage>(`/videos?${search.toString()}`, { signal });
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const setFilter = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next);
  };

  const items = videos.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <section>
      <div className="chips" role="toolbar" aria-label="Filter by status">
        <button type="button" className={status ? 'chip' : 'chip selected'} onClick={() => setFilter('status', '')}>
          All
        </button>
        {VIDEO_STATUSES.map((s) => (
          <button key={s} type="button" className={status === s ? 'chip selected' : 'chip'} onClick={() => setFilter('status', s)}>
            {statusLabel(s)}
          </button>
        ))}
      </div>

      {(q || app) && (
        <p className="muted filter-note">
          {q && <>Results for “{q}”. </>}
          {app && <>Application: {app}. </>}
          <button type="button" className="button text" onClick={() => setParams(status ? { status } : {})}>
            Clear
          </button>
        </p>
      )}

      {videos.isPending && <p className="muted">Loading…</p>}
      {videos.isError && <p className="error">{videos.error.message}</p>}
      {videos.isSuccess && items.length === 0 && <p className="muted">No videos match.</p>}

      <div className="video-grid">
        {items.map((video) => (
          <Link key={video.id} to={`/watch/${video.id}`} className="video-card">
            <div className="thumb">
              {video.thumbnailUrl ? <img src={video.thumbnailUrl} alt="" loading="lazy" /> : <div className="thumb-empty">{statusLabel(video.status)}</div>}
              {video.durationMs ? <span className="duration">{formatDuration(video.durationMs)}</span> : null}
            </div>
            <div className="video-meta">
              <h3 className="video-title">{video.title}</h3>
              <p className="muted">{video.application}</p>
              <p className="muted">
                {formatBytes(video.sizeBytes)} · {formatDate(video.createdAt)}
              </p>
              {video.status !== 'Ready' && <StatusBadge status={video.status} />}
            </div>
          </Link>
        ))}
      </div>

      {videos.hasNextPage && (
        <div className="row load-more">
          <button type="button" className="button" disabled={videos.isFetchingNextPage} onClick={() => void videos.fetchNextPage()}>
            {videos.isFetchingNextPage ? 'Loading…' : 'Load more'}
          </button>
        </div>
      )}
    </section>
  );
}
