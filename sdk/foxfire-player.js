// FoxfireMedia player control SDK. Dependency-free ES module; copy it into your app.
//
//   import { FoxfirePlayer } from './foxfire-player.js';
//   const player = new FoxfirePlayer(document.querySelector('iframe'));   // iframe src: embedUrl + '?controls=0'
//   await player.ready;                                                    // metadata loaded; duration known
//   player.on('timeupdate', (state) => renderScrubber(state.currentTime, state.duration, state.buffered));
//   await player.seek(42);
//
// The iframe must be on one of the application's AllowedOrigins and carry allow="autoplay; fullscreen".

const HANDSHAKE_INTERVAL_MS = 250;
const HANDSHAKE_TIMEOUT_MS = 20000;
const COMMAND_TIMEOUT_MS = 10000;

export class FoxfirePlayer {
  /**
   * @param {HTMLIFrameElement} iframe The embed iframe.
   * @param {{ origin?: string }} [options] FoxfireMedia's origin; defaults to the iframe src origin.
   */
  constructor(iframe, options = {}) {
    this.iframe = iframe;
    this.origin = options.origin ?? new URL(iframe.src, window.location.href).origin;
    /** @type {import('./foxfire-player').PlayerState | null} */
    this.state = null;
    this._listeners = new Map();
    this._pending = new Map();
    this._nextId = 1;
    this._connected = false;

    this.ready = new Promise((resolve, reject) => {
      this._resolveReady = resolve;
      this._rejectReady = reject;
    });

    this._onMessage = (event) => this._handle(event);
    window.addEventListener('message', this._onMessage);
    this._startHandshake();
  }

  /** Subscribes to an event; returns a function that unsubscribes. */
  on(event, callback) {
    if (!this._listeners.has(event)) this._listeners.set(event, new Set());
    this._listeners.get(event).add(callback);
    return () => this._listeners.get(event)?.delete(callback);
  }

  play() {
    return this._command('play');
  }

  pause() {
    return this._command('pause');
  }

  /** Seeks to a time in seconds (clamped to the video). */
  seek(seconds) {
    return this._command('seek', seconds);
  }

  /** Volume from 0 to 1. */
  setVolume(volume) {
    return this._command('setVolume', volume);
  }

  setMuted(muted) {
    return this._command('setMuted', muted);
  }

  /** Playback speed from 0.25 to 4. */
  setPlaybackRate(rate) {
    return this._command('setPlaybackRate', rate);
  }

  getState() {
    return this._command('getState');
  }

  destroy() {
    window.removeEventListener('message', this._onMessage);
    clearInterval(this._handshake);
    for (const { reject, timer } of this._pending.values()) {
      clearTimeout(timer);
      reject(new Error('Player destroyed.'));
    }
    this._pending.clear();
    this._listeners.clear();
  }

  _startHandshake() {
    const started = Date.now();
    const hello = () => this.iframe.contentWindow?.postMessage({ foxfire: 1, type: 'hello' }, this.origin);
    this._handshake = setInterval(() => {
      if (this._connected) {
        clearInterval(this._handshake);
      } else if (Date.now() - started > HANDSHAKE_TIMEOUT_MS) {
        clearInterval(this._handshake);
        this._rejectReady(new Error('The player did not respond. Is this page on one of the application\'s AllowedOrigins?'));
      } else {
        hello();
      }
    }, HANDSHAKE_INTERVAL_MS);
    this.iframe.addEventListener('load', hello);
    hello();
  }

  _command(command, value) {
    const id = this._nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._pending.delete(id);
        reject(new Error(`'${command}' timed out.`));
      }, COMMAND_TIMEOUT_MS);
      this._pending.set(id, { resolve, reject, timer });
      this.iframe.contentWindow?.postMessage({ foxfire: 1, type: 'command', id, command, value }, this.origin);
    });
  }

  _emit(event, payload) {
    for (const callback of this._listeners.get(event) ?? []) {
      try {
        callback(payload);
      } catch (error) {
        console.error(error);
      }
    }
  }

  _handle(event) {
    if (event.source !== this.iframe.contentWindow || event.origin !== this.origin) return;
    const message = event.data;
    if (!message || message.foxfire !== 1) return;
    if (message.state) this.state = message.state;

    if (message.type === 'connected') {
      this._connected = true;
      this._emit('connected', message.state);
    } else if (message.type === 'event') {
      if (message.event === 'ready') this._resolveReady(message.state);
      this._emit(message.event, message.state);
    } else if (message.type === 'response' && this._pending.has(message.id)) {
      const { resolve, reject, timer } = this._pending.get(message.id);
      clearTimeout(timer);
      this._pending.delete(message.id);
      if (message.ok) resolve(message.state);
      else reject(Object.assign(new Error(message.error ?? 'Command failed.'), { state: message.state }));
    }
  }
}
