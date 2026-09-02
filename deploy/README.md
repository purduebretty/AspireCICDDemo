# Deploying to Azure

There are three ways to deploy:

- **`aspire deploy`** (direct / local) — builds and pushes the image, provisions the
  per-environment resource group, and deploys the container apps in one command. Best for
  ad-hoc and local deploys. Covered below.
- **GitHub Actions** (CI/CD) — [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml)
  publishes the Bicep for every environment as an artifact, then deploys staging and
  production. See [CI/CD via GitHub Actions](#cicd-via-github-actions).
- **Azure DevOps → Octopus** (CI/CD) — [`azure-pipelines.yml`](../azure-pipelines.yml) at the
  repo root builds the app, `aspire do push`es the image to igsops, publishes the per-env
  Bicep, zips it, and pushes the package to Octopus; Octopus runs the deployment. See
  [CI/CD via Azure DevOps + Octopus](#cicd-via-azure-devops--octopus) at the end.

Both share the model below.

The container registry (`igsops`, **Enterprise Production**) and the Container Apps
environment (**Enterprise Dev/Test**) can live in different subscriptions. This works because:

- Images are **pushed to `igsops`** (referenced as an *existing* registry).
- The apps **pull from `igsops` using a pre-created user-assigned identity**
  (`uai-brett-aspire-demp`) that already holds `AcrPull` on `igsops` (provisioned via
  Terraform). `WithAcrPullIdentity` in the AppHost tells Aspire to use that identity
  instead of creating its own + a (cross-subscription, impossible) role assignment.

So Aspire creates **no role assignments** and **no extra registry** — see
[../TicTacToe.AppHost/AppHost.cs](../TicTacToe.AppHost/AppHost.cs). Because the AppHost
references those existing resources with `PublishAsExistingInResourceGroup(name, rg,
subscription)`, the generated Bicep scopes them to the right subscription
(`resourceGroup(acr_subscription, acr_rg)`) natively — `aspire deploy` handles the
Production/Dev-Test split with no manual patching.

## Per-environment configuration

Deploy values come from .NET configuration, merged **per key** across `appsettings.json`
and `appsettings.{env}.json`, so they split cleanly:

- **Shared across every environment → `appsettings.json`** — `app-name`, the registry, and
  the deploy subscription/location:
  ```json
  {
    "Parameters": {
      "app-name": "aspire",
      "acr-name": "bgoldmandemo",
      "acr-rg": "brett-demo-resources",
      "acr-subscription": "82a8659c-…",
      "target-subscription": "82a8659c-…"
    },
    "Azure": {
      "SubscriptionId": "82a8659c-…",
      "Location": "eastus2",
      "AllowResourceGroupCreation": true
    }
  }
  ```
- **Environment-specific → `appsettings.{env}.json`** (selected by `-e`/`--environment`):

  | File | Environment | Target resource group |
  |------|-------------|-----------------------|
  | `appsettings.Development.json` | local | (run-mode; not an Azure deploy target) |
  | `appsettings.dev.json`         | dev   | `brett-aspire-demo-dev` |
  | `appsettings.staging.json`     | staging | `brett-aspire-demo-staging` |
  | `appsettings.production.json`  | production | `brett-aspire-demo-production` |

  The file name must match the `-e` value exactly — **config file lookup is case-sensitive on
  Linux**, so the CI runners need `-e staging`, not `-e Staging`. Each file holds only what
  actually differs:

  ```json
  {
    "Azure": { "ResourceGroup": "brett-aspire-demo-staging" },
    "PasswordVault": { "PublicNetworkAccess": "Enabled" }
  }
  ```

  `dev` and `staging` enable public access on the password vault so password persistence
  actually works; **production omits it** and stays `Disabled`, per the org's no-public-endpoint
  policy — which means production regenerates the data-service passwords on every deploy and
  stamps a new revision to keep them in lockstep. Add the same `PasswordVault` block to make
  production persist them too.

**One resource group per environment holds everything.** `Azure:ResourceGroup`
(`brett-aspire-demo-{env}`) is the default scope for every resource Aspire provisions — the
container apps, the Container Apps environment, storage, the password vault, and the managed
identities — and the deployment creates that RG itself (`AllowResourceGroupCreation`).

**The container registry is the only outlier.** `igsops` is a pre-existing shared registry, so
it keeps its own `acr-rg` + `acr-subscription`, and the generated template scopes exactly two
modules there: the registry reference and the `AcrPull` role assignment (which has to be scoped
where the registry lives). Everything else is scoped to `rg`.

That's why `aca-env-rg` and `acr-pull-identity-rg` are absent from the env files: **they default
to `Azure:ResourceGroup`**. Set one only to point at a resource that lives somewhere else — a
Container Apps environment shared across environments, say, or a Terraform-managed identity.

### Gotcha: deployment state outranks `appsettings.{env}.json`

`aspire deploy` caches every resolved parameter in
`~/.aspire/deployments/<apphost-hash>/{env}.json`, and that state **wins over the config file**.
Editing `appsettings.{env}.json` after a deploy therefore appears to do nothing — the AppHost
keeps using the cached value. Delete the stale keys (or the whole `{env}.json`) to make the file
authoritative again. CI is unaffected: it starts with no state and passes values as
`Parameters__name` env vars.

### Missing infrastructure is created, not fatal

The Container Apps environment and the ACR-pull identity may be pre-created (Terraform) and
referenced as *existing*. A brand-new environment has neither, and referencing a resource that
doesn't exist fails provisioning with `ResourceNotFound`. So at publish/deploy time the AppHost
**probes each one** with the deploy credential:

| Probe result | What happens |
|---|---|
| Found | Referenced as existing — the deploy never modifies it. |
| Not found, or not named in config | Aspire **provisions and owns** it (in `Azure:ResourceGroup`). |
| Lookup failed (no credential / no read permission) | Treated as existing, with a warning — a bad probe never duplicates infrastructure. |

So **omitting `aca-env-name` or `acr-pull-identity-name` from `appsettings.{env}.json` is the
way to say "Aspire owns this one"** — those parameters are declared only on the
existing-reference branch, so a missing value creates the resource instead of prompting for it.
(The matching `-rg` values just default to `Azure:ResourceGroup`.) The **resource group** itself is created by the provisioner, which needs
`"Azure": { "AllowResourceGroupCreation": true }`. Container **apps** were always created by the
deploy; nothing changed there.

Two things worth knowing:

- **The ACR-pull identity is all-or-nothing.** `WithAcrPullIdentity` is BYO-identity: it pulls
  with the identity given and mints *no* role assignment. That is only correct for an identity
  that already holds `AcrPull`. When the configured identity isn't found, the AppHost passes
  *none*, so Aspire creates its own identity **and** the `AcrPull` role assignment — which needs
  RBAC-write in the registry's subscription. That works for a same-subscription registry; for
  the cross-subscription `igsops` it does not, so pre-create the identity with `AcrPull` in
  Terraform and name it in config. (Handing `WithAcrPullIdentity` a freshly created identity is
  what produces `unable to pull image using Managed identity …` on every revision.)
- **A created ACA environment lands in `Azure:ResourceGroup`** — Aspire provisions one
  deployment into one RG. It keeps the configured `aca-env-name`, and since `aca-env-rg`
  defaults to that same RG, the next deploy finds it and references it as existing rather than
  re-provisioning it (~10 minutes each time). Only an environment deliberately placed elsewhere
  needs `aca-env-rg` set.

The **registry is never auto-created**: `igsops` is a shared org registry in another
subscription, and standing up a second one would push images nowhere useful.

Set `"Azure": { "CreateMissingInfrastructure": false }` to disable the probes and always
reference everything as existing.

### Naming: one knob, everything else derived

`app-name` (default `aspire`) is the only name in the config. Everything else is built from it,
the environment slug, and a deterministic token:

| | Pattern | Example |
|---|---|---|
| Resource (→ ACR repo) | `{app}-{role}` | `aspire-server` |
| Deployed container app | `{app}-{role}-{env}` | `aspire-server-dev` |
| Container Apps environment | `{app}-{env}-{token}` | `aspire-dev-jfxu22` |
| Password vault | `kv-{app}-{env}-{token}` | `kv-aspire-dev-jfxu22` |

The **token** is a deterministic 6-character hash of the target (subscription + resource group)
plus app and environment — same inputs, same token, on every machine and every deploy, so names
never churn. It exists for the names that must be *globally* unique: without it, `kv-aspire-dev`
would collide across subscriptions, and a soft-deleted vault would hold the name hostage for its
full retention window.

App names carry the environment because several environments may share one Container Apps
environment, where names must be unique. Notes:

- The **resource** name has no environment or token, so the pushed image repo stays
  `{app}-server` and one image promotes across envs; only the *deployed container-app* name is
  suffixed, via `PublishAsAzureContainerApp`.
- The Postgres **database** stays `gamesdb`, so the Server's `gamesdb` connection lookup is
  unaffected by any renaming above it.
- The Redis connection name is baked into the Server as a literal, so the AppHost passes the
  real resource name via `Cache__ConnectionName`.
- `frontend/vite.config.ts` hardcodes the server resource name (`aspire-server`) to build its
  dev-proxy target — **change `app-name` and you must change that constant too.**

Renaming `app-name` renames everything on the next deploy; the previously deployed resources are
left behind, not migrated.

## Deploy

Select the environment with `-e` / `--environment` (this sets the AppHost's host
environment, so `appsettings.{env}.json` loads). Always pass it — the default is
`Production`, which has no matching file here.

```bash
az login                       # a principal with Contributor on the Dev/Test RGs + Reader on igsops
aspire deploy -e dev           # builds & pushes the image, provisions brett-aspire-demo-dev, deploys the apps
```

`aspire deploy` resolves parameters from `appsettings.{env}.json` and its own deployment
state. Resolution order is **env var (`Parameters__name` / `Azure__…`) → config file →
prompt**, so CI can override any file value with an env var or `-- --Parameters:name=value`
without editing files (e.g. a versioned image tag: `-- --Parameters:image-tag=$BUILD`).

To inspect the generated Bicep without deploying:

```bash
aspire publish -e dev -o ./artifacts   # writes main.bicep + per-resource modules (gitignored)
```

> Postgres runs **without** a persistent volume (org policy forbids storage accounts with
> public network access), so game history resets if the `postgres` app restarts — fine for
> a live demo.

## CI/CD via GitHub Actions

[`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml) runs on every push to `main`
(and on demand via **Run workflow**):

1. **`publish-bicep`** — a matrix over `staging` and `production` runs `aspire publish -e {env}`
   and uploads `main.bicep` plus the per-resource modules as the artifact `bicep-{env}`
   (30-day retention). This is the reviewable output; `aspire deploy` regenerates the same
   templates from the same AppHost and config, so the deploy jobs don't consume the artifact.
2. **`deploy-staging`** — `aspire deploy -e staging`, tagging both images with the commit sha.
3. **`deploy-production`** — the same for production, only after staging succeeds.

A manual run takes an `environment` input (`both`, `staging`, `production`) to deploy just one.

`publish-bicep` logs into Azure too, and that isn't optional: the AppHost probes Azure while
generating templates (does the ACA environment exist? is the resource group mid-deletion? who is
the deploying principal?), so without a login the generated Bicep wouldn't match what deploys.

### Setup

**Secrets** (Settings → Secrets and variables → Actions). The workflow authenticates as the
user-assigned managed identity `uai-brett-github` (resource group `brett-demo-resources`) via
OIDC — federated, so there is no client secret anywhere:

| Secret | Value |
|---|---|
| `CLIENT_ID` | `69db8745-ae46-4e0b-b436-ea6c063e7500` (the `clientId` of `uai-brett-github`) |
| `TENANT_ID` | `0b89df11-aae9-4d55-b967-9242a64a6490` |
| `SUBSCRIPTION_ID` | `82a8659c-720d-48ab-a58e-bdaa5d42c92a` |

Each job passes these into the [`setup-aspire`](../.github/actions/setup-aspire/action.yml)
composite action, which installs the toolchain and runs `azure/login`. The values are passed
from the job rather than read inside the action because **a composite action cannot access the
`secrets` context** — it only sees what its caller hands it. A missing secret resolves to an
empty string and fails at login rather than erroring on the name, so if login fails with a blank
client id, check the secret names first.

The workflow requests `id-token: write`, so the identity needs a federated credential per
subject. **A job that declares `environment:` gets the environment subject, not the ref
subject** — which is why three jobs need three credentials. All three already exist on
`uai-brett-github`:

| Credential | Subject | Used by |
|---|---|---|
| `github-main` | `repo:purduebretty/AspireCICDDemo:ref:refs/heads/main` | `publish-bicep` (no environment) |
| `github-env-staging` | `repo:purduebretty/AspireCICDDemo:environment:staging` | `deploy-staging` |
| `github-env-production` | `repo:purduebretty/AspireCICDDemo:environment:production` | `deploy-production` |

All three use issuer `https://token.actions.githubusercontent.com` and audience
`api://AzureADTokenExchange`. A missing subject fails at login with `AADSTS70021`. Running the
workflow manually from a branch other than `main` won't match `github-main`, so add a credential
for that ref if you need it. To recreate them:

```bash
az identity federated-credential create \
  --name github-env-staging --identity-name uai-brett-github -g brett-demo-resources \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:purduebretty/AspireCICDDemo:environment:staging \
  --audiences api://AzureADTokenExchange
```

**Azure permissions.** The identity needs Contributor on the deploy subscription (it creates the
per-environment resource groups), `AcrPush` on the registry, and — because Aspire creates the
ACR-pull identity and its `AcrPull` grant — RBAC-write (User Access Administrator or Owner) on
the registry's resource group. `uai-brett-github` currently holds **Owner on the whole
subscription**, which covers all of that and then some; anything that runs in this workflow gets
subscription Owner, so it's worth narrowing to the three grants above if the repo ever takes
outside contributions.

**Environments** (Settings → Environments): create `staging` and `production`. Adding required
reviewers to `production` turns the last job into an approval gate — the jobs already declare
`environment:`, so no workflow change is needed.

### Notes

- A runner starts with **no Aspire deployment state**, so every value resolves from
  `appsettings.{env}.json` on each run. That sidesteps the stale-state traps described above,
  and it means generated passwords come from the password vault (or regenerate, with a revision
  stamp, when the vault isn't readable).
- The image tag is the commit sha, so a deployed revision traces back to a commit. Retrying a
  failed run reuses the same tag.
- Runners are `linux/amd64`, matching what Azure Container Apps requires — no `--platform` flag
  is needed here, unlike local builds on Apple Silicon.

## CI/CD via Azure DevOps + Octopus

[`azure-pipelines.yml`](../azure-pipelines.yml) is the **CI + packaging** side; **Octopus does
the actual deploy**. Per run it:

1. builds the .NET solution and the Vite frontend (fail fast);
2. `aspire do push`es the server image to `igsops` (built once — the image is env-agnostic),
   tagged with the run number;
3. `aspire publish`es the Bicep for every environment into `bicep/<env>/`, zips it into one
   `TicTacToe.Deploy.<version>.zip`, and pushes that package to Octopus.

The image tag and the package version are the same value, so an Octopus release maps to the
exact image the run built.

**Configure before first run** (Azure DevOps): an ARM service connection with `AcrPush` on
`igsops` (Enterprise Production), an *Octopus Deploy* service connection (needs the Octopus
marketplace extension), and your Octopus space/project — all named at the top of the pipeline
(move them to a variable group if you prefer).

> ⚠️ **The package is raw Bicep — Octopus still needs a deploy step to apply it.** The
> published templates declare parameters but don't carry values, and the `cache`/`postgres`
> secrets live in Aspire's deployment state, not in the package. So the Octopus project needs
> a step (e.g. *Run an Azure CLI Script* / *Deploy an ARM template*) that applies
> `bicep/#{Octopus.Environment.Name}/main.bicep` and the per-resource modules with the
> environment's parameter + secret values. That step is not in this repo (the old `apply.sh`
> that did it was removed); if you want it back as a packaged script, say so and I'll add one.
