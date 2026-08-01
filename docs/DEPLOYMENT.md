# Deploying Highlighter to Azure

Two containers on Azure Container Apps; Supabase (Postgres + Auth + Storage)
stays external, exactly as in local dev.

```
Browser ──HTTPS──▶ highlighter-web  (Blazor Server, sticky sessions)
                       │ server-to-server
                       ▼
                   highlighter-api  (Minimal API; spawns the pipeline worker
                       │             as a subprocess inside the same container)
                       ▼
                   Supabase / Azure OpenAI / Azure AI Speech / Deepgram / …
```

The API image bundles everything a pipeline run needs: the ASP.NET API, the
`highlighter` worker CLI, ffmpeg/ffprobe, yt-dlp, streamlink, fonts for
caption rasterization, and the Python shots-sidecar sources. TransNetV2/torch
is **not** installed by default (it adds multiple GB); scene-cut detection is
skipped gracefully, or opt in with `--build-arg INSTALL_SHOTS=true`.

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
2. **Build and push** `highlighter-api` and `highlighter-web` images to Azure
   Container Registry, tagged with the commit SHA (plus `latest`). The two
   images build in parallel matrix jobs.
3. **Roll the apps**: `az containerapp update --image …:<sha>` on each app.
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
ring), a user-assigned identity with AcrPull, and the two container apps. The
apps start on a public placeholder image; the first `deploy.yml` run replaces
it.

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

## Deliberate minimalism (and what scaling out would take)

- **One replica each.** Pipeline jobs live in the API's process memory and
  renders land on the mounted `outputs` share; scaling the API out needs a
  real job queue (e.g. moving job state to Postgres and workers to Container
  Apps Jobs). The web app is Blazor Server (stateful circuits) — sticky
  sessions are already configured for the day `maxReplicas` goes up.
- **Worker runs inside the API container**, exactly like local dev — the API
  spawns `dotnet /app/worker/highlighter.dll`. The container gets 2 vCPU /
  4 GiB; raise it in `infra/main.bicep` if renders need more.
- **No TransNetV2 by default.** Build the API image with
  `INSTALL_SHOTS=true` (and consider a dedicated-workload profile) when scene
  cuts matter in prod.
- **Secrets are Container Apps secrets.** A later step up is Azure Key Vault
  references, which the bicep can adopt without touching app code.
