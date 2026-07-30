# Deploying Highlighter to Azure

Azure Container Apps end to end; Supabase (Postgres + Auth + Storage) stays
external, exactly as in local dev.

```
Browser ──HTTPS──▶ highlighter-web    (Blazor Server, sticky sessions, 1–3 replicas)
                       │ server-to-server
                       ▼
                   highlighter-api    (Minimal API, 1–3 replicas on HTTP load)
                       │ enqueue ingest        │ light verbs (revise, publish,
                       ▼                       │ exports) run as subprocesses
                pipeline-jobs queue            ▼
                 (Azure Storage) ──KEDA──▶ highlighter-worker (Container Apps
                       ▲                    Job: one execution per ingest run,
                       │ state: pipeline_jobs table    0–10 in parallel)
                       ▼
                   Supabase / Azure OpenAI / Azure AI Speech / Deepgram / …
```

Ingest — the heavy highlight-detection pipeline (capture, transcribe, TransNet
scene cuts, LLM scoring, rendering) — is dispatched through an Azure Storage
queue: the API writes a `pipeline_jobs` row (the durable state record) and
enqueues a message; KEDA scales the `highlighter-worker` job from 0 to one
execution per queued run. Workers and the API share the `outputs` Azure Files
mount, so the `/media` mirror, revise/publish gating, and per-job log files
keep working exactly as on a single machine. Cancellation stays cooperative
through the DB (`projects.status = 'stopping'`), with a `cancel_requested`
flag on the job row as the force-kill path.

Three images, all built from the repo root:

- `docker/api.Dockerfile` — ASP.NET API + worker CLI + ffmpeg/yt-dlp/streamlink
  for the light verbs it still runs in-container. TransNetV2/torch is **not**
  installed (ingest doesn't run here in prod).
- `docker/worker.Dockerfile` — the queue worker (`highlighter run-queued`).
  Same media tooling, **with** TransNetV2 on CPU-only torch: TransNet operates
  on 48×27 frames, so no GPU is needed.
- `docker/web.Dockerfile` — the Blazor Server studio.

## Local parity

```bash
cp .env.example .env          # fill in keys
docker compose up --build     # web on :5097, api on :5199
```

## The CI/CD pipeline

Two workflows under `.github/workflows/`:

### `ci.yml` — every push and PR

1. `dotnet test` the pipeline worker solution and the API solution,
   `dotnet build` the web app.
2. Build both Docker images (without pushing) so a PR can never merge an
   image that no longer assembles. Layers are cached in the GitHub Actions
   cache, so unchanged stages (apt installs, NuGet restore) are near-instant.

No Azure credentials are involved; CI runs fine before the Azure account exists.

### `deploy.yml` — every push to `main` (and manual runs)

1. **Log in to Azure via OIDC.** GitHub mints a short-lived, workflow-scoped
   token; Azure accepts it because of a federated credential you register
   once (below). No password or long-lived service-principal secret ever
   lives in GitHub.
2. **Build and push** `highlighter-api`, `highlighter-web`, and
   `highlighter-worker` images to Azure Container Registry, tagged with the
   commit SHA (plus `latest`). The images build in parallel matrix jobs.
3. **Roll the apps**: `az containerapp update --image …:<sha>` on each app and
   `az containerapp job update` on the worker (running executions finish on
   the old image; new ones start on the new).
   Container Apps creates a new *revision*, waits for it to become healthy
   (the API has a `/healthz` liveness probe), then shifts traffic and drains
   the old revision — an automatic, zero-downtime blue/green per deploy.
4. **Smoke check**: poll `https://<api-fqdn>/healthz` and fail the workflow
   if the new revision never comes up. Because the image tag is the commit
   SHA, rolling back is re-running the deploy job from any earlier commit
   (workflow_dispatch), or `az containerapp revision activate` on the
   previous revision.

The `deploy` job is serialized by a concurrency group, so two merges can't
interleave their rollouts.

## One-time Azure setup

Everything below happens once, when the Azure account is ready.

### 1. Provision the infrastructure

```bash
az group create -n highlighter-rg -l eastus2

# secrets.json mirrors .env: {"SUPABASE_URL": "...", "AZURE_OPENAI_API_KEY": "...", ...}
# Every key becomes a Container Apps secret exposed as an env var to both apps.
az deployment group create -g highlighter-rg -f infra/main.bicep \
  -p acrName=<globally-unique-name> pipelineSecrets=@secrets.json
```

`infra/main.bicep` creates: Log Analytics, the Container Apps environment, an
ACR (Basic), a storage account with two Azure Files shares (`outputs` for
renders + the `/media` mirror, `dpkeys` for the web app's DataProtection key
ring) and the `pipeline-jobs` queue, a user-assigned identity with AcrPull,
the two container apps, and the event-driven worker job. Everything starts on
a public placeholder image; the first `deploy.yml` run replaces it.

Also apply `supabase/migrations/` to the Supabase project — distributed
dispatch needs the `pipeline_jobs` table (202608010014).

To rotate or add a secret later, re-run the deployment with the updated
`secrets.json` (or `az containerapp secret set` + a new revision).

### 2. Let GitHub deploy (OIDC federated credential)

```bash
# An Entra app registration the workflow logs in as
az ad app create --display-name highlighter-deploy
APP_ID=<appId from output>
az ad sp create --id $APP_ID

# Trust GitHub's OIDC issuer for this repo's main branch...
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>/<repo>:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
# ...and for the production environment (used by environment-scoped jobs)
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-production-env",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>/<repo>:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'

# Least privilege: Contributor on the one resource group only
az role assignment create --assignee $APP_ID --role Contributor \
  --scope /subscriptions/<sub-id>/resourceGroups/highlighter-rg
```

### 3. GitHub repository configuration

Settings → Secrets and variables → Actions:

| Kind     | Name                   | Value                          |
|----------|------------------------|--------------------------------|
| secret   | `AZURE_CLIENT_ID`      | the app registration's appId   |
| secret   | `AZURE_TENANT_ID`      | your tenant id                 |
| secret   | `AZURE_SUBSCRIPTION_ID`| your subscription id           |
| variable | `AZURE_RESOURCE_GROUP` | `highlighter-rg`               |
| variable | `ACR_NAME`             | the ACR name from step 1       |

Optionally create a `production` environment (Settings → Environments) and
require reviewers on it — the deploy jobs reference it, so that adds a manual
approval gate between merge and rollout.

### 4. Point Supabase at the deployed web app

Add `https://highlighter-web.<env-default-domain>` to the Supabase project's
auth redirect allow-list if you use email confirmation links.

## Design notes (and the remaining sharp edges)

- **Ingest scales horizontally, light verbs don't need to.** Each queued
  ingest gets its own 4 vCPU / 8 GiB job execution (up to 10 in parallel —
  `maxExecutions` in `infra/main.bicep`). Revise/publish/thumbnails/exports
  are quick enough to stay as subprocesses of the API replica that took the
  request; their state is mirrored to `pipeline_jobs` so `GET /api/jobs/*`
  works from any replica, but a *force*-kill for them only lands on the
  replica that owns the process (cooperative cancel via the DB always works).
- **The shared `outputs` Azure Files mount is the contract** between API and
  workers: `/media` mirror, `project.json` gating, and the per-job log files
  the API tails for `/logs` and `/logs/stream`.
- **CPU-only workers.** TransNetV2 runs on 48×27 frames; the smallest Azure
  GPU tier (serverless T4) is quota-gated and ~5–10× the cost for no
  measurable gain here. If that changes: switch `device` in
  `pipeline/highlighter_pipeline/shots.py`, use a CUDA torch wheel in
  `docker/worker.Dockerfile`, and put the job on a GPU workload profile.
- **No Redis.** The storage queue carries the work signal; Supabase Postgres
  is the state store and cancellation channel. A second stateful service
  would buy nothing at this scale.
- **Blazor Server web app** keeps sticky sessions (stateful circuits) while
  scaling 1–3.
- **Secrets are Container Apps secrets.** A later step up is Azure Key Vault
  references, which the bicep can adopt without touching app code.
