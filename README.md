# FoxfireMedia

A self-hosted video host for your own applications. Apps upload movies, shows and gameplay VODs,
FoxfireMedia turns them into HLS, and apps embed a player for **their own** videos. Viewers never
get a download link.

- **Server:** ASP.NET Core (.NET 10), PostgreSQL, ffmpeg.
- **Web:** React + TypeScript (admin console and embed player).
- **Ships as:** one Docker image.

## Contents

- [Run it](#run-it)
- [Configuration](#configuration)
- [Integrating an application](#integrating-an-application)
- [Security model](#security-model)
- [Development](#development)
- [Known limitations](#known-limitations)

## Run it

```bash
cp .env.example .env                                         # replace every value (openssl rand -base64 48)
cp config/appsettings.example.json config/appsettings.json   # declare your applications
docker compose up -d --build
```

The server listens on port `8080`. Put it behind a TLS-terminating reverse proxy and set
`PublicBaseUrl` to its public origin. `/healthz` is liveness. `/readyz` checks the database, that
media storage is writable, and that ffmpeg is present.

If your network cannot reach `archive.ubuntu.com`, set `APT_MIRROR=http://azure.archive.ubuntu.com`
in `.env` before building.

Media lives in `/data/media`. Everything large goes there: uploads in progress, kept originals,
processing scratch and HLS streams. Bind-mount a dedicated disk with `MEDIA_PATH=/mnt/media`, so
the admin console's free-space figure describes that disk.

**Make sure media only ever lands on that disk.** If the drive isn't mounted, the mount point is an
empty directory on the OS disk, and Docker even creates a missing one. To guard against this:

1. Create the marker on the drive once:

   ```bash
   touch /mnt/media/.foxfire-media-volume
   ```

2. Set `Storage__RequireVolumeMarker=true` in `.env`.

Without the marker, the server refuses to start and creates nothing on the wrong disk. If the drive
disappears while the server is running, `/readyz` reports unhealthy and new uploads are refused with
`507`.

On a Linux host, also run `chown 1654 /mnt/media`, since the container runs as uid 1654.

### Admin console

Open `/admin`. The first admin comes from `Admin__BootstrapUsername` / `Admin__BootstrapPassword`,
and is created only while no admin exists. Sign in, then add the rest of your admins on the
**Admins** page.

If you are locked out, set `Admin__ResetBootstrapPassword=true` for one restart. This resets the
bootstrap admin's password to the configured value.

| Page | What it does |
|---|---|
| **Library** / **Watch** | Browse and search every app's videos. Play, edit, re-process from the kept original, or delete. |
| **Upload** | Upload on behalf of an application. Uploads are resumable and in 50 MB chunks. |
| **Processing** | The job queue, with progress and errors. |
| **Applications** | Each app's usage and webhook delivery log. Send a test event, resend a delivery, revoke all outstanding links, or purge an app that was removed from config. |
| **Storage** | Free space on the media disk, usage per app, the largest videos, and the consistency check (orphans and missing files). |
| **Admins** | Add or remove admins, and change your own password (this ends your other sessions). |

## Configuration

### Applications

Applications are declared in `config/appsettings.json`. Their API keys come **only** from the
environment.

```json
{
  "Applications": [
    {
      "Name": "Foxfire",
      "WebhookUrl": "https://foxfire.alluna.dev/api/uploads",
      "AllowedOrigins": ["https://foxfire.alluna.dev"]
    }
  ]
}
```

```bash
ApiKeys__Foxfire=<openssl rand -base64 48>
```

- **`Name`** is the identity. It must match `^[A-Za-z][A-Za-z0-9_]{1,31}$` and pairs with `ApiKeys__<Name>`.
- **`AllowedOrigins`** are the exact origins that may frame the player and upload from a browser.
  An app without origins can still upload and manage videos, but cannot embed them.
- **`WebhookUrl`** is optional and must use https. See [Webhooks](#webhooks).
- **Startup refuses to run** when:
  - a key is found in any config file or on the command line;
  - an app has no key, or a key has no app;
  - two keys are identical;
  - a key is weak or still a placeholder.

  Error messages name the app, never the key.
- **Removing an app** marks it *Missing*. Its key and all its links stop working, and its videos are
  kept until an admin purges them.
- **Rotating a key:**
  1. Set `ApiKeysSecondary__<Name>`.
  2. Restart, then switch the app to the new key.
  3. Promote the new key to `ApiKeys__<Name>`.

### Other settings (environment variables)

| Variable | Default | Purpose |
|---|---|---|
| `PublicBaseUrl` | (required) | Public origin used for every absolute URL. The Host header is never trusted. |
| `Signing__Key` / `Signing__PreviousKey` | (required) | Root key for every signed token (base64, at least 32 bytes). The previous key is accepted while you rotate. |
| `ConnectionStrings__Foxfire` | set by compose | PostgreSQL connection string. |
| `Uploads__MaxBytes` | 200 GiB | Largest single upload. |
| `Uploads__TokenLifetime` | 00:15:00 | How long a minted upload token can be used to *start* an upload. |
| `Uploads__IdleTimeout` | 00:10:00 | An upload with no progress for this long fails. |
| `Media__MaxHeight`, `Media__MaxBitrateKbps` | off | Sources above these limits are transcoded down. |
| `Media__X264Preset`, `Media__Crf` | veryfast, 20 | Encoder settings used when a transcode is needed. |
| `Processing__Concurrency` | 1 | Number of ffmpeg jobs that run at once. |
| `Processing__KeepSource` | true | Keep originals after encoding, so videos can be re-processed. |
| `Embed__DefaultLifetime` | 01:00:00 | Default lifetime of an embed URL (clamped to 5 min – 24 h). |
| `Streaming__PlaybackTokenLifetime` | 00:15:00 | Playback token lifetime. The player refreshes it, up to a 12 h session. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | false | Set to `true` behind a reverse proxy so client IPs are real. |

## Integrating an application

Every app call sends `X-Api-Key: <key>` from **your server**. Keys never belong in a browser.

### 1. Upload (minted, one-time tokens)

**Mint a ticket.** Your server asks for a ticket and hands only the `uploadToken` to your client.

```http
POST /api/v1/uploads
X-Api-Key: …
Content-Type: application/json

{ "title": "Movie night", "filename": "movie.mkv", "sizeBytes": 4294967296, "externalId": "your-id-123" }
```

The response looks like this:

```json
{ "videoId": "dQw4w9WgXcQ", "uploadUrl": "https://media.example.com/uploads/tus", "uploadToken": "v1.…", "expiresAt": "…", "maxBytes": 4294967296 }
```

**Upload the file.** The client uploads straight to FoxfireMedia over [tus](https://tus.io), with
`Authorization: Bearer <uploadToken>`. Bytes never pass through your server. With tus-js-client:

```ts
new tus.Upload(file, {
  endpoint: ticket.uploadUrl,
  headers: { Authorization: `Bearer ${ticket.uploadToken}` },
  chunkSize: 50 * 1024 * 1024,
  metadata: { filename: file.name },
}).start();
```

**Token rules:**
- The token must be used to start the upload within **15 minutes**.
- Starting the upload **consumes** it. The same token can only resume that one upload.
- If the upload fails (the client aborts, it stalls for 10 minutes, or it runs past 12 hours),
  request a new token with `POST /api/v1/videos/{id}/upload-token`.

**Processing.** A finished upload is processed automatically:
- H.264/AAC sources are remuxed in minutes.
- Other sources are transcoded once to single-quality HLS.
- The original is kept.

### 2. Embed

```http
POST /api/v1/videos/{id}/embed
X-Api-Key: …

{ "ttlSeconds": 3600 }
```

The response gives you `embedUrl` and `posterUrl`. Put the embed URL in an iframe on one of your
`AllowedOrigins`:

```html
<iframe src="{embedUrl}" allow="autoplay; fullscreen; picture-in-picture" allowfullscreen></iframe>
```

You can append `?autoplay=true&muted=true&t=30` to the URL. The URL only needs to be valid when the
iframe loads, so mint one per page view.

### 3. Manage

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/me` | The calling app's name, origins and webhook URL. |
| `GET /api/v1/videos?status=Ready&limit=25&cursor=…` | Your videos, newest first, paged. |
| `GET /api/v1/videos/{id}` | One video. |
| `GET /api/v1/videos/by-external-id/{externalId}` | A video looked up by your own id. |
| `PATCH /api/v1/videos/{id}` | Change `title`, `description` or `externalId`. |
| `DELETE /api/v1/videos/{id}` | Delete the video and all its files. |

Another app's video is always reported as `404`, exactly as if it didn't exist.

### Webhooks

When an app has a `WebhookUrl`, FoxfireMedia POSTs events to it, so you don't need to poll.

**Events:** `upload.started`, `upload.completed`, `upload.failed`, `upload.expired`,
`video.processing`, `video.ready`, `video.failed`, `video.deleted`

**Payload:**

```json
{ "id": "evt_…", "type": "video.ready", "createdAt": "…",
  "data": { "videoId": "dQw4w9WgXcQ", "externalId": "your-id-123", "title": "Movie night", "status": "Ready",
            "durationMs": 7200000, "width": 1920, "height": 1080, "error": null } }
```

**Headers:**

| Header | Meaning |
|---|---|
| `Foxfire-Event-Id` | Stable across retries; use it to drop duplicates. |
| `Foxfire-Event-Type` | The event type. |
| `Foxfire-Signature: t=<unix>,v1=<hex>` | HMAC-SHA256 of `"<t>.<raw body>"`, keyed with **your API key**. A second `v1` is included while a secondary key is set. |

**Delivery:**
- Any 2xx response counts as delivered. Redirects are not followed.
- Failed deliveries are retried after 1m, 5m, 30m, 2h, 6h and 12h, then dropped.
- Events for one video arrive in order.

**Verifying a signature (C#):**

```csharp
static bool Verify(string header, string body, string apiKey, TimeSpan tolerance)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).ToLookup(p => p[0], p => p[1]);
    var t = parts["t"].Single();
    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(t)) > tolerance.TotalSeconds) return false;
    var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes($"{t}.{body}")));
    return parts["v1"].Any(v => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(v), Encoding.ASCII.GetBytes(expected)));
}
```

**Verifying a signature (TypeScript / Node):**

```ts
import { createHmac, timingSafeEqual } from 'node:crypto';

export function verify(header: string, body: string, apiKey: string, toleranceSec = 300): boolean {
  const parts = header.split(',').map((p) => p.split('=', 2) as [string, string]);
  const t = parts.find(([k]) => k === 't')?.[1];
  if (!t || Math.abs(Date.now() / 1000 - Number(t)) > toleranceSec) return false;
  const expected = Buffer.from(createHmac('sha256', apiKey).update(`${t}.${body}`).digest('hex'));
  return parts.some(([k, v]) => k === 'v1' && v.length === expected.length && timingSafeEqual(Buffer.from(v), expected));
}
```

## Security model

- **API keys are never visible in the application.**
  - They are read only from the environment and held in memory as SHA-256 hashes.
  - They are never stored in the database (not even as a hash), never logged, and never returned by
    any API or shown in the admin UI.
  - The test suite checks logs, every database row and responses for leaks.
- **Isolation.**
  - Every app query is scoped to the caller.
  - An app cannot mint upload, embed or poster tokens for another app's video. Tokens are
    HMAC-signed with a separate key per purpose, and are bound to the video, the app and a
    revocation epoch.
- **No download path.**
  - Video is served only as short-lived, token-gated HLS segments from `/stream/{token}/…`.
  - Responses carry `Cross-Origin-Resource-Policy: same-origin`, and never range responses,
    `Content-Disposition` or CORS headers.
  - Requests a browser labels cross-site are refused.
  - The embed page may be framed only by the owning app's origins (CSP `frame-ancestors`).

## Development

```bash
docker compose up -d db          # or any local PostgreSQL
dotnet run --project src/Foxfire.Server   # http://localhost:5080 (see appsettings.Development.json)
cd web && npm ci && npm run dev  # admin UI on http://localhost:5173, proxied to the server
```

```bash
dotnet test --solution Foxfire.slnx   # unit + integration (needs Docker for Testcontainers)
```

`Foxfire.MediaTests` exercise the real ffmpeg pipeline. They are skipped where ffmpeg isn't
installed; CI runs them.

## Known limitations

- **Download can't be fully prevented.** Anyone who can watch a stream can capture it. These
  measures stop hotlinking, cross-site embedding and casual downloading, not a determined viewer.
- **Environment variables are visible to whoever controls the host.** For example,
  `docker inspect` shows them.
- **Path-embedded playback tokens can appear in reverse-proxy access logs.** They are short-lived
  and are not API keys. FoxfireMedia redacts them from its own logs.
- **Quality:**
  - Single rendition with no quality cap by default, so very high-bitrate remuxes can buffer on
    slow connections. Set `Media__MaxHeight` / `Media__MaxBitrateKbps` to cap them.
  - HDR sources are transcoded without tone-mapping.
  - Software x264 encoding is CPU-heavy.
- **Disk:** keeping originals roughly doubles disk use.
- **Single instance:** storage is local disk.
- **Reverse proxy settings:** keep tus chunks at 50 MB or less behind proxies with body-size
  limits, such as Cloudflare.
