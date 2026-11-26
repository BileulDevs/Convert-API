# ConvertAPI

[![CI](https://github.com/BileulDevs/Convert-API/actions/workflows/ci.yml/badge.svg)](https://github.com/BileulDevs/Convert-API/actions/workflows/ci.yml)

A file conversion API built with .NET 10. You upload a file and ask for a target format; a
background worker converts it; you follow the job, then download the result. Files are deleted
automatically after 24 hours.

It is a portfolio project about running **untrusted files through powerful tools** (FFmpeg,
ImageMagick) reliably and safely.

## What it does

| Family | Input | Output | Engine |
|---|---|---|---|
| Image | bmp, gif, jpg, png, tiff, webp | bmp, gif, jpg, png, webp | Magick.NET |
| Audio | aac, flac, m4a, mp3, ogg, wav | aac, flac, mp3, ogg, wav | FFmpeg |
| Video | avi, mkv, mov, mp4, webm | gif, mp3, mp4, wav, webm | FFmpeg |

Document conversions (DOCX, ODT, Markdown, HTML) are planned but **not available yet**.

- **Asynchronous jobs**, followed by polling or in real time with Server-Sent Events.
- **Cancel** a job or delete its result at any time.
- **Signed webhooks** (HMAC-SHA256) with retries when a job ends.
- **Per-key rate limit and quota**: 100 requests a minute and 10 active jobs by default.
- **Crash recovery**: if a worker dies mid-conversion, another one picks the job up (3 attempts).
- **Defense in depth**: formats are detected from file content, uploads are streamed, FFmpeg runs
  as a separate process with a restricted protocol list and an empty environment, the worker
  container has no internet access, and webhook URLs are protected against SSRF.
- **Observability**: OpenTelemetry traces (one trace from the upload to the conversion) and
  business metrics.
- A minimal **web front** to try it in a browser.

## How it works

```text
              ┌──────────────┐   jobs (also the queue)   ┌──────────────┐
 client ────► │     API      │ ────────► PostgreSQL ◄─── │    Worker    │
 (HTTP, SSE)  │ + web front  │                           │ FFmpeg,      │
              └──────┬───────┘                           │ ImageMagick  │
                     │            shared files           └──────┬───────┘
                     └──────────► uploads / results ◄───────────┘
```

There is no separate message broker: workers claim jobs from the `Jobs` table with
`FOR UPDATE SKIP LOCKED` and hold them with a lease.

| Folder | Content |
|---|---|
| `src/ConvertApi.Api` | HTTP API (minimal APIs), webhooks, serves the web front |
| `src/ConvertApi.Worker` | Background conversions and expiry sweep |
| `src/ConvertApi.Core` | Domain: job state machine, formats, storage, with no dependency |
| `src/ConvertApi.Data` | EF Core and PostgreSQL, migrations, job queue |
| `src/ConvertApi.ServiceDefaults` | Telemetry and health checks |
| `src/ConvertApi.AppHost` | .NET Aspire host for local development |
| `web` | Nuxt single-page front |
| `tests` | Unit and integration tests |
| `infra` | Azure infrastructure (Bicep) and setup script |
| `loadtests` | k6 load tests |

## Run it locally

### Option 1: Docker Compose (simplest)

You only need [Docker](https://docs.docker.com/get-docker/).

1. Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD` to any long random value
   (avoid `'` and `;`).

   ```bash
   cp .env.example .env
   ```

2. Start everything:

   ```bash
   docker compose up --build
   ```

| URL | What |
|---|---|
| http://localhost:5080 | Web front |
| http://localhost:5080/scalar | Interactive API documentation |

Use this **development API key**: `cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProd`. It is public,
only accepted in the Development environment, and the API only listens on `127.0.0.1`.

To run more conversions at once: `docker compose up --scale worker=3`.
If PostgreSQL rejects the password after you changed it, the old volume still holds the previous
one. `docker compose down -v` resets it, **deleting all local data**.

### Option 2: .NET Aspire (for development)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker (for PostgreSQL) and
[FFmpeg](https://ffmpeg.org/download.html) on your `PATH` (or set `Conversion__FfmpegPath`).

```bash
dotnet run --project src/ConvertApi.AppHost --launch-profile http
```

The console prints the link to the Aspire dashboard (logs, traces, metrics). The API listens on
http://localhost:5080, with its documentation at `/scalar`.

This mode does not serve the web front. Run it separately with [Node.js](https://nodejs.org) 24:

```bash
npm --prefix web install
npm --prefix web run dev
```

It opens on http://localhost:3000 and forwards `/v1` to http://localhost:5080
(set `API_URL` to target another address).

## Use the API

Every `/v1/jobs` call needs the `X-Api-Key` header. In Windows PowerShell, type `curl.exe`
instead of `curl`.

```bash
KEY=cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProd

# 1. Create a job (202 Accepted, with the job id)
curl -H "X-Api-Key: $KEY" -F "file=@photo.png" -F "targetFormat=jpg" http://localhost:5080/v1/jobs

# 2. Follow it
curl -H "X-Api-Key: $KEY" http://localhost:5080/v1/jobs/{id}
curl -N -H "X-Api-Key: $KEY" http://localhost:5080/v1/jobs/{id}/events

# 3. Download the result
curl -H "X-Api-Key: $KEY" -o result.jpg http://localhost:5080/v1/jobs/{id}/result
```

| Endpoint | Description |
|---|---|
| `GET /v1/formats` | Accepted formats and their targets (no key needed) |
| `POST /v1/jobs` | Upload a file and queue its conversion; optional `callbackUrl` |
| `GET /v1/jobs/{id}` | State of a job |
| `GET /v1/jobs/{id}/events` | Status changes as Server-Sent Events |
| `GET /v1/jobs/{id}/result` | Download the result (supports `Range`) |
| `DELETE /v1/jobs/{id}` | Cancel the job, or delete its result |
| `GET /health/live`, `/health/ready` | Health probes |

Size limits: 500 MB for video, 200 MB for audio, 50 MB for images. A conversion is stopped after
10 minutes, and audio or video output is cut at 2 hours.

### Webhooks

A `callbackUrl` must be `https://` and point to a public host. When the job ends, the API posts
the same JSON as `GET /v1/jobs/{id}`, with a header:

```text
X-ConvertApi-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body">
```

To verify it, recompute the HMAC over the raw body with your key's webhook secret, compare in
constant time, and reject a timestamp more than 5 minutes off. Delivery is *at least once*:
ignore a job id and status you already handled. A failed delivery is retried after 30 s, 2 min,
10 min and 1 h.

## Tests

You need the .NET 10 SDK, FFmpeg (or the `FFMPEG_PATH` environment variable) and Docker, which
the integration tests use to start PostgreSQL.

```bash
dotnet test --solution ConvertApi.slnx
```

Load tests need Docker only (Windows PowerShell script):

```powershell
.\loadtests\run.ps1
```

Measured results and their limits are in [docs/performance.md](docs/performance.md).

## Continuous integration

GitHub Actions, in [`.github/workflows`](.github/workflows):

- every push: web front build, .NET build (warnings are errors), format check, unit tests;
- pull requests to `main`: integration tests;
- pushes to `main`: the API and worker images are published to GitHub Container Registry
  (`ghcr.io/bileuldevs/convertapi-api` and `convertapi-worker`), tagged with the commit SHA and
  `latest`;
- optionally, deployment to Azure (see below).

Dependabot proposes weekly updates against `dev`.

## Deploy

### On Azure (provided)

The repository describes an Azure Container Apps deployment: API and worker scaling to zero,
Azure Files for uploads and results, PostgreSQL Flexible Server, Key Vault and Application
Insights, sized with Azure's free grants in mind. Check current Azure pricing before using it;
the PostgreSQL grant is time-limited and covers a single server.

> **Status:** the Bicep template builds and lints, but this deployment **has not been run
> against a real Azure subscription yet**. Expect to fix things on the first run.

Once set up, every push to `main` deploys to **test**, runs a smoke test (a real conversion),
then waits for your approval before **prod**. GitHub signs in to Azure with OIDC, so no Azure
secret is stored. In short:

```powershell
az login
gh auth login
.\infra\setup.ps1 -SubscriptionId <subscription id>
```

Read the script first: it creates Azure resources and GitHub environments and secrets. If you
forked the repository, pass `-Repository <owner>/<repo>` and change the image defaults and names
to yours. The full procedure is in [docs/deploy.md](docs/deploy.md).

### On your own servers

**Do not use `compose.yaml` as is in production**: it runs in the Development environment, with
the public development key. Whatever the platform, you need PostgreSQL, a folder shared by the
API and all workers, the two container images, and HTTPS in front of the API.

| Setting (environment variable) | Used by | Value |
|---|---|---|
| `ConnectionStrings__convertapi` | API, worker | PostgreSQL connection string |
| `Storage__Root` | API, worker | The shared folder |
| `Database__MigrateOnStartup` | API | `true` to apply migrations at startup |
| `ApiKeys__Keys__0__Id` | API | A new GUID: the owner of this key's jobs |
| `ApiKeys__Keys__0__KeyId` | API | The 8 characters after `cvt_` in the key |
| `ApiKeys__Keys__0__Name` | API | A label |
| `ApiKeys__Keys__0__SecretHash` | API | Lower-case hex SHA-256 of the whole key |
| `ApiKeys__Keys__0__WebhookSecret` | API | A random secret of at least 32 characters |
| `Conversion__WorkRoot` | worker | A writable scratch folder |

Both images run as a non-root user (UID 1654), which must be able to write to the shared folder.
Keep secrets in a secret store. To generate a key and its hash:

```powershell
. .\infra\setup.ps1 -SubscriptionId unused; New-ApiKey
```

For the worker, reproduce the isolation of `compose.yaml`: no internet access, read-only file
system, no Linux capabilities, and CPU, memory and process limits.

Other settings and their defaults:

| Setting | Default |
|---|---|
| `Conversion__ResultRetention` (worker) | `1.00:00:00` (24 h) |
| `Conversion__MaxDuration` (worker) | `00:10:00` |
| `RateLimit__PermitLimit` / `RateLimit__Window` (API) | `100` / `00:01:00` |
| `RateLimit__MaxActiveJobs` (API) | `10` |
| `Uploads__MaxVideoBytes`, `MaxAudioBytes`, `MaxImageBytes` (API) | 500 / 200 / 50 MiB |

## Known limits

- Rate limit counters are kept per API instance.
- A compromised worker shares the files folder and the database credentials with the API.
- Concurrent uploads can slightly exceed the active-job quota.

## Third-party notices

The worker image ships a GPL build of FFmpeg, run as a separate process. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
