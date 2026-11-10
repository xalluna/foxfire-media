/**
 * postMessage control API for apps that draw their own player UI (embed with ?controls=0).
 *
 * Protocol (every message carries `foxfire: 1`):
 *   parent -> player  { type: 'hello' }                                   start listening
 *                     { type: 'command', id, command, value? }            play | pause | seek | setVolume
 *                                                                         | setMuted | setPlaybackRate | getState
 *   player -> parent  { type: 'connected', state }                        reply to hello
 *                     { type: 'event', event, state }                     ready | play | pause | timeupdate | seeking
 *                                                                         | seeked | progress | durationchange | ended
 *                                                                         | volumechange | ratechange | waiting | error
 *                     { type: 'response', id, ok, state?, error? }        reply to a command
 *
 * Only the parent window may talk to the player, and only from one of the application's allowed origins
 * (the same list that may frame it). Events go only to the origin that completed the handshake.
 */

export interface PlayerState {
  currentTime: number;
  duration: number;
  paused: boolean;
  ended: boolean;
  seeking: boolean;
  muted: boolean;
  volume: number;
  playbackRate: number;
  buffered: [number, number][];
  readyState: number;
}

type Command = 'play' | 'pause' | 'seek' | 'setVolume' | 'setMuted' | 'setPlaybackRate' | 'getState';

interface IncomingMessage {
  foxfire: 1;
  type: 'hello' | 'command';
  id?: number;
  command?: Command;
  value?: unknown;
}

const FORWARDED_EVENTS = [
  'play',
  'pause',
  'timeupdate',
  'seeking',
  'seeked',
  'progress',
  'durationchange',
  'ended',
  'volumechange',
  'ratechange',
  'waiting',
  'error',
] as const;

const finite = (n: number) => (Number.isFinite(n) ? n : 0);

export function snapshot(video: HTMLVideoElement): PlayerState {
  const buffered: [number, number][] = [];
  for (let i = 0; i < video.buffered.length; i++) {
    buffered.push([video.buffered.start(i), video.buffered.end(i)]);
  }
  return {
    currentTime: video.currentTime,
    duration: finite(video.duration),
    paused: video.paused,
    ended: video.ended,
    seeking: video.seeking,
    muted: video.muted,
    volume: video.volume,
    playbackRate: video.playbackRate,
    buffered,
    readyState: video.readyState,
  };
}

function isIncoming(data: unknown): data is IncomingMessage {
  return typeof data === 'object' && data !== null && (data as { foxfire?: unknown }).foxfire === 1;
}

function numberArg(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error('A finite number is required.');
  }
  return value;
}

async function run(video: HTMLVideoElement, command: Command | undefined, value: unknown): Promise<void> {
  switch (command) {
    case 'play':
      await video.play();
      return;
    case 'pause':
      video.pause();
      return;
    case 'seek': {
      const max = Number.isFinite(video.duration) ? video.duration : Number.MAX_SAFE_INTEGER;
      video.currentTime = Math.min(Math.max(numberArg(value), 0), max);
      return;
    }
    case 'setVolume':
      video.volume = Math.min(Math.max(numberArg(value), 0), 1);
      return;
    case 'setMuted':
      if (typeof value !== 'boolean') throw new Error('A boolean is required.');
      video.muted = value;
      return;
    case 'setPlaybackRate':
      video.playbackRate = Math.min(Math.max(numberArg(value), 0.25), 4);
      return;
    case 'getState':
      return;
    default:
      throw new Error(`Unknown command '${String(command)}'.`);
  }
}

export function attachBridge(video: HTMLVideoElement, allowedOrigins: readonly string[]): () => void {
  if (window.parent === window || allowedOrigins.length === 0) {
    return () => {};
  }

  let target: string | null = null;
  const post = (message: Record<string, unknown>) => {
    if (target) window.parent.postMessage({ foxfire: 1, ...message }, target);
  };

  const onMessage = (event: MessageEvent) => {
    if (event.source !== window.parent || !allowedOrigins.includes(event.origin) || !isIncoming(event.data)) {
      return;
    }

    const message = event.data;
    if (message.type === 'hello') {
      target = event.origin;
      post({ type: 'connected', state: snapshot(video) });
      if (video.readyState >= HTMLMediaElement.HAVE_METADATA) {
        post({ type: 'event', event: 'ready', state: snapshot(video) });
      }
      return;
    }

    if (message.type === 'command') {
      target = event.origin;
      const id = typeof message.id === 'number' ? message.id : null;
      run(video, message.command, message.value).then(
        () => post({ type: 'response', id, ok: true, state: snapshot(video) }),
        (error: unknown) => post({ type: 'response', id, ok: false, error: error instanceof Error ? error.message : String(error), state: snapshot(video) }),
      );
    }
  };

  const listeners = FORWARDED_EVENTS.map((name) => {
    const listener = () => post({ type: 'event', event: name, state: snapshot(video) });
    video.addEventListener(name, listener);
    return [name, listener] as const;
  });
  const onReady = () => post({ type: 'event', event: 'ready', state: snapshot(video) });
  video.addEventListener('loadedmetadata', onReady);
  window.addEventListener('message', onMessage);

  return () => {
    window.removeEventListener('message', onMessage);
    video.removeEventListener('loadedmetadata', onReady);
    for (const [name, listener] of listeners) video.removeEventListener(name, listener);
  };
}
