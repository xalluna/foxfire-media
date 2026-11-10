// The light build: no subtitles, alternate audio or DRM, none of which a single-rendition VOD needs.
import Hls from 'hls.js/light';

export interface PlayerSource {
  /** /stream/{token}/index.m3u8 */
  manifestUrl: string;
  /** /stream/{token}/refresh */
  refreshUrl: string;
  posterUrl?: string;
  expiresAt: string;
  title: string;
  autoplay?: boolean;
  muted?: boolean;
  start?: number;
}

export interface PlayerHandle {
  video: HTMLVideoElement;
  destroy(): void;
}

interface RefreshResponse {
  token: string;
  manifestUrl: string;
  expiresAt: string;
}

const TOKEN_IN_PATH = /\/stream\/[^/]+\//;

function tokenOf(url: string): string {
  const match = /\/stream\/([^/]+)\//.exec(url);
  if (!match?.[1]) {
    throw new Error('Not a stream URL');
  }
  return match[1];
}

/**
 * Mounts an HLS player. Playback tokens are short-lived and travel in the URL path; the player
 * refreshes the token in the background and swaps it into every subsequent request, so even a
 * multi-hour movie never needs a long-lived link.
 */
export function mountPlayer(container: HTMLElement, source: PlayerSource): PlayerHandle {
  const video = document.createElement('video');
  video.className = 'ff-video';
  video.controls = true;
  video.playsInline = true;
  video.preload = 'metadata';
  video.title = source.title;
  // Required for Safari's ManagedMediaSource (iOS 17.1+); also stops casting the raw stream elsewhere.
  video.disableRemotePlayback = true;
  video.setAttribute('controlslist', 'nodownload noremoteplayback');
  video.addEventListener('contextmenu', (e) => e.preventDefault());
  if (source.posterUrl) {
    video.poster = source.posterUrl;
  }
  if (source.muted) {
    video.muted = true;
  }
  if (source.autoplay) {
    video.autoplay = true;
  }
  container.replaceChildren(video);

  let token = tokenOf(source.manifestUrl);
  let timer: number | undefined;
  let destroyed = false;
  let hls: Hls | undefined;

  const showError = (message: string) => {
    const overlay = document.createElement('div');
    overlay.className = 'ff-overlay';
    overlay.textContent = message;
    container.append(overlay);
  };

  const refresh = async (mode?: 'native'): Promise<RefreshResponse | undefined> => {
    const response = await fetch(`/stream/${token}/refresh${mode ? '?mode=native' : ''}`, {
      method: 'POST',
      credentials: 'omit',
      cache: 'no-store',
    });
    if (!response.ok) {
      return undefined;
    }
    const body = (await response.json()) as RefreshResponse;
    token = body.token;
    return body;
  };

  const scheduleRefresh = (expiresAt: string) => {
    const remaining = new Date(expiresAt).getTime() - Date.now();
    const delay = Math.max(5_000, remaining / 3);
    timer = window.setTimeout(async () => {
      if (destroyed) return;
      const next = await refresh().catch(() => undefined);
      if (next) {
        scheduleRefresh(next.expiresAt);
      } else {
        // The session cap was reached or access was revoked; keep what is buffered, then stop.
        showError('This viewing session has expired. Reload to keep watching.');
      }
    }, delay);
  };

  if (source.start && source.start > 0) {
    video.addEventListener('loadedmetadata', () => (video.currentTime = source.start ?? 0), { once: true });
  }

  if (Hls.isSupported()) {
    hls = new Hls({
      // Every playlist/segment request uses the newest token, whatever URL hls.js derived.
      xhrSetup: (xhr, url) => {
        xhr.open('GET', url.replace(TOKEN_IN_PATH, `/stream/${token}/`), true);
      },
      enableWorker: true,
      lowLatencyMode: false,
      backBufferLength: 60,
    });
    hls.on(Hls.Events.ERROR, (_event, data) => {
      if (data.fatal) {
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
          hls?.startLoad();
        } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          hls?.recoverMediaError();
        } else {
          showError('This video cannot be played.');
          hls?.destroy();
        }
      }
    });
    hls.loadSource(source.manifestUrl);
    hls.attachMedia(video);
    scheduleRefresh(source.expiresAt);
  } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
    // Native HLS (older iOS) cannot swap tokens mid-stream: ask for one that covers the whole video.
    void refresh('native').then((native) => {
      if (destroyed) return;
      if (native) {
        video.src = native.manifestUrl;
      } else {
        showError('This video is unavailable.');
      }
    });
  } else {
    showError('Your browser cannot play this video.');
  }

  return {
    video,
    destroy() {
      destroyed = true;
      window.clearTimeout(timer);
      hls?.destroy();
      video.removeAttribute('src');
      video.load();
    },
  };
}
