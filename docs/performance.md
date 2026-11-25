# Performance

Measured with [k6](https://k6.io) against the Compose stack, with the scripts in
[`loadtests/`](../loadtests). Reproduce with Docker (Compose 2.24 or later) and nothing else:

```powershell
.\loadtests\run.ps1                                        # both scenarios, one worker
.\loadtests\run.ps1 -Workers 3 -Only worker-throughput
```

The script runs its own Compose project (`convertapi-loadtest`, port 5081) and deletes its data
afterwards; it fails if any k6 threshold fails. The per-key rate limit and active-job quota are
raised for the run ([`compose.loadtest.yaml`](../loadtests/compose.loadtest.yaml)): they exist
to stop one client from starving the others, and here the load test is that one client.

**Machine**: AMD Ryzen 9 7950X (16 cores), 31 GB RAM, Windows 11, Docker Desktop (WSL 2 VM with
32 CPUs and 15 GB). Measured on 25 November 2026.

## Creating jobs: `POST /v1/jobs`

Requests per second ramp from 5 to 50 and hold there for a minute, each uploading a 1×1 PNG so
the time is the API's own work: authentication, quota check, multipart parsing with content
sniffing, storage and the job row. The spec's target is a 95th percentile below 200 ms.

| Requests | Failed | Dropped | Median | p95 | p99 | Max |
|---:|---:|---:|---:|---:|---:|---:|
| 4,425 over 2 min | 0 | 0 | 6.2 ms | **8.0 ms** | 9.3 ms | 36.6 ms |

The target holds with a wide margin, and the load generator kept up: every planned request was
sent and accepted (202).

## Conversion throughput

300 PNG to JPEG jobs, each a 1920×1080 image (1.9 MB), uploaded one after another by a single
client; timed from the first upload to the last finished job.

| Workers | Uploads done | All jobs done | Jobs per minute | Failed | Bound by |
|---:|---:|---:|---:|---:|---|
| 1 | 7.5 s | 14.7 s | **≈ 1,230** | 0 | the worker |
| 3 | 8.4 s | 9.2 s | **≥ 1,960** | 0 | the client |

With one worker, conversion carries on for 7 seconds after the last upload: about 49 ms per Full
HD image end to end (claim, copy to its scratch directory, ImageMagick, storage, saving the
outcome). With three, the last job finishes less than a second after the last upload, so the
figure is what one sequential client can send, a lower bound on what three workers can convert.

## Limits of these numbers

- **Synthetic image**: the test image is a smooth gradient, the same for every job. Real photos
  carry more detail and encode more slowly; expect lower throughput.
- **Local and generous**: network, disk and database are all on one fast desktop, behind Docker
  Desktop's `host.docker.internal` proxy. Each worker may use 2 CPUs and the API is not capped,
  and the API runs in the Development environment. On Azure Container Apps (0.5 vCPU for the
  API, 1 vCPU per worker, files on Azure Files over SMB) expect slower figures; the same scripts
  can target the test environment with `BASE_URL` and `API_KEY`.
- **Image jobs only**: audio and video conversions take seconds to minutes, so a worker's
  throughput there is bounded by FFmpeg, not by the service.
- **Tiny uploads for the p95** by design; large uploads are streamed straight to storage, so
  their time is the transfer itself.
- The run rebuilds the `convertapi-api` and `convertapi-worker` images of the usual Compose
  stack (same tags); its data is never touched.
