// A minimal "application" that embeds FoxfireMedia videos, the way your real apps will:
// the API key stays on this server, which lists its videos and mints a fresh embed URL per page view.
//
//   node samples/embed-playground/server.mjs        (then open http://localhost:5500)
//
// Configuration (environment variables, falling back to the repo's .env):
//   FOXFIRE_URL      FoxfireMedia's public origin      (default: PublicBaseUrl from .env)
//   FOXFIRE_APP      application name                  (default: DemoA)
//   FOXFIRE_API_KEY  that application's API key        (default: ApiKeys__<FOXFIRE_APP> from .env)
//   PORT             port for this page                (default: 5500; must be one of the app's AllowedOrigins)

import { existsSync, readFileSync } from 'node:fs';
import http from 'node:http';
import { join } from 'node:path';

function loadDotEnv(path) {
  if (!existsSync(path)) return {};
  return Object.fromEntries(
    readFileSync(path, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line && !line.startsWith('#') && line.includes('='))
      .map((line) => [line.slice(0, line.indexOf('=')).trim(), line.slice(line.indexOf('=') + 1).trim()]),
  );
}

const dotenv = loadDotEnv(process.env.FOXFIRE_ENV_FILE ?? join(import.meta.dirname, '..', '..', '.env'));
const foxfire = (process.env.FOXFIRE_URL ?? dotenv.PublicBaseUrl ?? 'http://localhost:8090').replace(/\/$/, '');
const appName = process.env.FOXFIRE_APP ?? 'DemoA';
const apiKey = process.env.FOXFIRE_API_KEY ?? dotenv[`ApiKeys__${appName}`];
const port = Number(process.env.PORT ?? 5500);

if (!apiKey) {
  console.error(`No API key: set FOXFIRE_API_KEY or ApiKeys__${appName} in .env`);
  process.exit(1);
}

const escape = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

async function foxfireApi(path, init = {}) {
  const response = await fetch(foxfire + path, {
    ...init,
    headers: { 'X-Api-Key': apiKey, 'Content-Type': 'application/json', ...init.headers },
  });
  if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${path} -> ${response.status} ${await response.text()}`);
  return response.json();
}

const page = (title, body) => `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escape(title)}</title>
<style>
  body { margin: 0; font: 14px/1.4 Roboto, Arial, sans-serif; color: #0f0f0f; background: #fff; }
  header { padding: 16px 24px; border-bottom: 1px solid #e5e5e5; }
  main { max-width: 1100px; margin: 0 auto; padding: 24px; }
  a { color: #065fd4; text-decoration: none; }
  ul { padding: 0; list-style: none; display: grid; gap: 8px; }
  li a { display: block; padding: 12px 16px; border-radius: 12px; background: #f2f2f2; color: inherit; }
  li a:hover { background: #e5e5e5; }
  .frame { position: relative; aspect-ratio: 16 / 9; border-radius: 12px; overflow: hidden; background: #000; }
  .frame iframe { position: absolute; inset: 0; width: 100%; height: 100%; border: 0; }
  code, pre { font: 12px ui-monospace, Consolas, monospace; }
  pre { white-space: pre-wrap; word-break: break-all; background: #f9f9f9; padding: 12px; border-radius: 8px; }
  .muted { color: #606060; }
</style></head>
<body><header><strong>Embed playground</strong> <span class="muted">· "${escape(appName)}" on ${escape(foxfire)}</span></header>
<main>${body}</main></body></html>`;

async function index() {
  const { items } = await foxfireApi('/api/v1/videos?limit=100');
  const list = items.length
    ? `<ul>${items
        .map((v) => `<li><a href="/watch?id=${encodeURIComponent(v.id)}">${escape(v.title)} <span class="muted">· ${escape(v.status)}</span></a></li>`)
        .join('')}</ul>`
    : `<p class="muted">No videos for ${escape(appName)} yet. Upload one in the admin console (${escape(foxfire)}/admin/upload), choosing ${escape(appName)}.</p>`;
  return page('Embed playground', `<h1>Videos</h1>${list}`);
}

async function watch(id, query) {
  // Server side: mint a short-lived embed URL with the API key. Only the URL goes to the browser.
  const embed = await foxfireApi(`/api/v1/videos/${encodeURIComponent(id)}/embed`, { method: 'POST', body: JSON.stringify({ ttlSeconds: 3600 }) });
  const options = new URLSearchParams();
  for (const name of ['autoplay', 'muted', 't']) if (query.get(name)) options.set(name, query.get(name));
  const src = embed.embedUrl + (options.size ? `?${options}` : '');
  return page(
    'Watch',
    `<p><a href="/">&larr; All videos</a></p>
     <div class="frame"><iframe src="${escape(src)}" allow="autoplay; fullscreen; picture-in-picture" allowfullscreen></iframe></div>
     <p class="muted">Try <a href="?id=${encodeURIComponent(id)}&autoplay=true&muted=true">autoplay</a> or
       <a href="?id=${encodeURIComponent(id)}&t=10">start at 0:10</a>. This embed URL expires ${escape(embed.expiresAt)}; reload for a fresh one.</p>
     ${embed.posterUrl ? `<p class="muted">Poster (usable as a thumbnail on your pages):</p><img src="${escape(embed.posterUrl)}" alt="" width="320">` : ''}
     <p class="muted">Embed URL minted by this page's server:</p><pre>${escape(src)}</pre>
     <p><a href="/custom?id=${encodeURIComponent(id)}">Try it with a custom player UI &rarr;</a></p>`,
  );
}

// The same embed with ?controls=0, driven entirely from this page through the FoxfirePlayer SDK.
async function custom(id) {
  const embed = await foxfireApi(`/api/v1/videos/${encodeURIComponent(id)}/embed`, { method: 'POST', body: JSON.stringify({ ttlSeconds: 3600 }) });
  return page(
    'Custom controls',
    `<p><a href="/watch?id=${encodeURIComponent(id)}">&larr; Standard player</a></p>
     <div class="frame"><iframe id="player" src="${escape(embed.embedUrl)}?controls=0" allow="autoplay; fullscreen; picture-in-picture" allowfullscreen></iframe></div>
     <div class="bar">
       <button id="toggle" disabled>Play</button>
       <button id="back">&minus;10s</button>
       <button id="forward">+10s</button>
       <div id="track"><div id="buffered"></div><div id="played"></div><div id="hover"></div></div>
       <span id="time">0:00 / 0:00</span>
       <button id="mute">Mute</button>
       <select id="rate"><option>0.5</option><option>0.75</option><option selected>1</option><option>1.25</option><option>1.5</option><option>2</option></select>
     </div>
     <p class="muted" id="status">Connecting&hellip;</p>
     <style>
       .bar { display: flex; align-items: center; gap: 8px; margin-top: 12px; }
       .bar button, .bar select { height: 36px; padding: 0 14px; border: 0; border-radius: 18px; background: #f2f2f2; font: inherit; cursor: pointer; }
       .bar button:hover { background: #e5e5e5; }
       #track { position: relative; flex: 1; height: 8px; border-radius: 4px; background: #e5e5e5; cursor: pointer; }
       #track > div { position: absolute; top: 0; bottom: 0; left: 0; border-radius: 4px; pointer-events: none; }
       #buffered { background: #c6c6c6; } #played { background: #f00; } #hover { background: rgb(0 0 0 / 12%); }
       #time { font-variant-numeric: tabular-nums; min-width: 96px; text-align: right; }
     </style>
     <script type="module">
       import { FoxfirePlayer } from '/foxfire-player.js';
       const $ = (id) => document.getElementById(id);
       const clock = (s) => { s = Math.floor(s || 0); const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = String(s % 60).padStart(2, '0');
         return h ? h + ':' + String(m).padStart(2, '0') + ':' + x : m + ':' + x; };
       const player = new FoxfirePlayer($('player'));
       window.foxfirePlayer = player; // for poking at it from the console
       const render = (s) => {
         if (!s) return;
         const d = s.duration || 1;
         $('played').style.width = (s.currentTime / d * 100) + '%';
         const end = s.buffered.reduce((m, [a, b]) => (a <= s.currentTime + 0.5 ? Math.max(m, b) : m), 0);
         $('buffered').style.width = (end / d * 100) + '%';
         $('time').textContent = clock(s.currentTime) + ' / ' + clock(s.duration);
         $('toggle').textContent = s.paused ? 'Play' : 'Pause';
         $('mute').textContent = s.muted ? 'Unmute' : 'Mute';
       };
       for (const e of ['timeupdate', 'progress', 'play', 'pause', 'seeked', 'volumechange', 'durationchange', 'ended']) player.on(e, render);
       player.ready.then((s) => { $('toggle').disabled = false; $('status').textContent = 'Ready: the controls below drive the player through postMessage.'; render(s); },
                         (e) => { $('status').textContent = e.message; });
       const fraction = (ev) => { const r = $('track').getBoundingClientRect(); return Math.min(Math.max((ev.clientX - r.left) / r.width, 0), 1); };
       $('track').addEventListener('click', (ev) => player.seek(fraction(ev) * (player.state?.duration || 0)));
       $('track').addEventListener('mousemove', (ev) => { $('hover').style.width = fraction(ev) * 100 + '%'; });
       $('track').addEventListener('mouseleave', () => { $('hover').style.width = '0'; });
       $('toggle').addEventListener('click', () => (player.state?.paused ? player.play() : player.pause()).catch((e) => ($('status').textContent = e.message)));
       $('back').addEventListener('click', () => player.seek((player.state?.currentTime || 0) - 10));
       $('forward').addEventListener('click', () => player.seek((player.state?.currentTime || 0) + 10));
       $('mute').addEventListener('click', () => player.setMuted(!player.state?.muted));
       $('rate').addEventListener('change', (ev) => player.setPlaybackRate(Number(ev.target.value)));
     </script>`,
  );
}

const sdkPath = join(import.meta.dirname, '..', '..', 'sdk', 'foxfire-player.js');

http
  .createServer(async (req, res) => {
    const url = new URL(req.url ?? '/', `http://localhost:${port}`);
    if (url.pathname === '/foxfire-player.js') {
      res.writeHead(200, { 'Content-Type': 'text/javascript; charset=utf-8', 'Cache-Control': 'no-store' });
      res.end(readFileSync(sdkPath));
      return;
    }

    try {
      const id = url.searchParams.get('id');
      const html =
        url.pathname === '/watch' && id ? await watch(id, url.searchParams)
        : url.pathname === '/custom' && id ? await custom(id)
        : await index();
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
      res.end(html);
    } catch (error) {
      res.writeHead(502, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(page('Error', `<h1>Request to FoxfireMedia failed</h1><pre>${escape(error.message)}</pre>`));
    }
  })
  .listen(port, () => console.log(`Embed playground for ${appName}: http://localhost:${port}  (FoxfireMedia: ${foxfire})`));
