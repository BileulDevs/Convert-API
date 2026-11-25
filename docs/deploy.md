# Deploying to Azure

ConvertAPI runs on Azure Container Apps, described in [`infra/main.bicep`](../infra/main.bicep).
GitHub Actions deploys it: every push to `main` publishes the images, deploys them to **test**,
smoke-tests them, then waits for your approval before doing the same in **prod**.

```text
push to main ─► CI (build, tests) ─► images to ghcr.io ─► deploy test ─► smoke test
                                                            └─► approval ─► deploy prod ─► smoke test
```

## What gets created

Per environment, in the resource group `rg-convertapi-<environment>`:

| Resource | Role | Free grant |
|---|---|---|
| Container Apps environment, `api` and `worker` apps | Both scale to zero; KEDA wakes the worker for queued jobs and expired results, and the API for due webhooks | 180,000 vCPU-s and 360,000 GiB-s a month |
| Azure Files share, 5 GiB | Uploads and results, mounted at `/files` in both apps | 5 GiB for 12 months |
| PostgreSQL Flexible Server, B1ms | Jobs and queue | 750 hours a month for 12 months, **one server** |
| Key Vault | Connection strings and webhook secrets, read by one identity per app | A few cents |
| Log Analytics and Application Insights | OpenTelemetry traces and logs, capped at 0.15 GB a day | 5 GB a month |

The PostgreSQL grant covers one server: running test and prod all month doubles it. Deploy prod
only when needed, or stop its server (`az postgres flexible-server stop`) in between.

## One-time setup

You need an Azure subscription (a free account gives the 12-month grants), the
[Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and the GitHub CLI.

1. Sign in to both:

   ```powershell
   az login
   gh auth login
   ```

2. Read [`infra/setup.ps1`](../infra/setup.ps1), then run it with your subscription id:

   ```powershell
   .\infra\setup.ps1 -SubscriptionId <subscription id>
   ```

   It creates the resource groups and an app registration that GitHub signs in as through OIDC
   (no Azure secret is stored anywhere), with Contributor on the two resource groups and the right
   to grant one role only, Key Vault Secrets User. On GitHub it creates the `test` and `prod`
   environments (prod requires your approval), their Azure variables and generated secrets, then
   turns deployment on (`DEPLOY_ENABLED`). It prints a demo API key per environment once: keep it,
   GitHub stores it as a secret and Azure only its hash. Running the script again changes nothing
   that exists.

3. Push to `main` (merge a pull request). The first run publishes the images.

4. Make the two packages public, once: on GitHub, **Packages**, `convertapi-api` then
   `convertapi-worker`, **Package settings**, **Change visibility**, **Public**. Container Apps
   pulls them anonymously. Then re-run the failed deployment from the Actions tab.

## Secrets

Kept in each GitHub environment, passed to the deployment as environment variables, and stored
by Azure in Key Vault. Nothing secret is in the repository.

| Secret | Content |
|---|---|
| `POSTGRES_PASSWORD` | Database administrator password, 32 letters and digits |
| `API_KEYS` | JSON array of `{ id, keyId, name, secretHash }`: key hashes, never keys |
| `WEBHOOK_SECRETS` | JSON object `{ "<keyId>": "<signing secret>" }` |
| `SMOKE_API_KEY` | A full API key, used by the smoke test |

To add a client key, generate one (the setup script's `New-ApiKey` does it:
`. .\infra\setup.ps1 -SubscriptionId x; New-ApiKey`), add its hash to `API_KEYS` and a secret to
`WEBHOOK_SECRETS`, then redeploy.

## Good to know

- **First deployment**: new Key Vault role assignments can take minutes to apply; the workflow
  retries once. The `Jobs` table exists once the API has started, which the smoke test triggers.
- **Cold starts**: with no traffic both apps are at zero replicas. The first request waits for
  the API (and its database connection), the first job for a worker: about a minute.
- **Webhooks** are sent by the API; while it sleeps, KEDA wakes it within about 30 seconds of a
  notification falling due.
- **Known limits**: PostgreSQL is reached over its public endpoint, restricted to Azure services
  and protected by TLS and the password (a VNet would make it private, at a cost). Container Apps
  cannot cut the worker's internet access without a VNet either, unlike the Compose setup.
