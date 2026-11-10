export interface PlayerState {
  /** Seconds. */
  currentTime: number;
  /** Seconds (0 until metadata has loaded). */
  duration: number;
  paused: boolean;
  ended: boolean;
  seeking: boolean;
  muted: boolean;
  /** 0 to 1. */
  volume: number;
  playbackRate: number;
  /** Buffered ranges as [start, end] seconds. */
  buffered: [number, number][];
  /** HTMLMediaElement.readyState. */
  readyState: number;
}

export type PlayerEvent =
  | 'connected'
  | 'ready'
  | 'play'
  | 'pause'
  | 'timeupdate'
  | 'seeking'
  | 'seeked'
  | 'progress'
  | 'durationchange'
  | 'ended'
  | 'volumechange'
  | 'ratechange'
  | 'waiting'
  | 'error';

export declare class FoxfirePlayer {
  constructor(iframe: HTMLIFrameElement, options?: { origin?: string });
  readonly iframe: HTMLIFrameElement;
  readonly origin: string;
  /** Last state reported by the player. */
  readonly state: PlayerState | null;
  /** Resolves once metadata has loaded (duration known). */
  readonly ready: Promise<PlayerState>;
  on(event: PlayerEvent, callback: (state: PlayerState) => void): () => void;
  play(): Promise<PlayerState>;
  pause(): Promise<PlayerState>;
  seek(seconds: number): Promise<PlayerState>;
  setVolume(volume: number): Promise<PlayerState>;
  setMuted(muted: boolean): Promise<PlayerState>;
  setPlaybackRate(rate: number): Promise<PlayerState>;
  getState(): Promise<PlayerState>;
  destroy(): void;
}
