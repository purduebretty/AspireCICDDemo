# Deploying to Azure

There are three ways to deploy:

- **`aspire deploy`** (direct / local) — builds and pushes the image, provisions the
  per-environment resource group, and deploys the container apps in one command. Best for
  ad-hoc and local deploys. Covered below.
- **GitHub Actions** (CI/CD) — [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml)
  publishes the Bicep for every environment as an artifact, then deploys staging and
  production. See [CI/CD via GitHub Actions](#cicd-via-github-actions).
- **Azure DevOps → Octopus** (CI/CD) — [`azure-pipelines.yml`](../azure-pipelines.yml) at the
  repo root builds the app, `aspire do push`es the image to the registry, publishes the per-env
  Bicep, zips it, and pushes the package to Octopus; Octopus runs the deployment. See
  [CI/CD via Azure DevOps + Octopus](#cicd-via-azure-devops--octopus) at the end.

Both share the model below.

The container registry and the Container Apps environment can live in different
subscriptions. **Which registry is never hardcoded** — it comes from the `acr-name` parameter,
looked up in `acr-rg` / `acr-subscription` (see `appsettings.json`). This works because:

- Images are **pushed to that registry** (referenced as an *existing* resource).
- The apps **pull from it using a pre-created user-assigned identity** that already holds
  `AcrPull` on the registry. `WithAcrPullIdentity` in the AppHost tells Aspire to use that
  identity instead of creating its own + a (cross-subscription, impossible) role assignment.

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

  Two resources have a public-network-access switch, because the Container Apps environment has
  **no VNet and no private endpoints** — a private-only resource isn't reachable from the
  container apps at all:

  | Switch | Default | Effect when `Disabled` |
  |---|---|---|
  | `Storage` | **`Enabled`** (every environment) | avatar upload fails with `403 AuthorizationFailure` |
  | `PasswordVault` | `Disabled` | passwords regenerate every deploy, with a revision stamp to keep services in lockstep |

  Storage defaults to `Enabled` everywhere, production included: an account nothing can reach
  isn't a safer demo, just a broken one. That conflicts with the org's "storage accounts should
  disable public network access" policy — a deliberate, reversible trade for this demo. Note what
  it does *not* do: `allowSharedKeyAccess` and `allowBlobPublicAccess` stay `false`
  unconditionally, so there are no SAS tokens, no account keys, and no anonymous containers; every
  request is still an authenticated Azure AD call from a principal holding `Storage Blob Data
  Contributor`. What it adds is that the endpoint answers from the internet at all.

  Lock either one down per environment once that environment has a VNet and a private endpoint:

  ```json
  { "Storage": { "PublicNetworkAccess": "Disabled" } }
  ```

  **`403 AuthorizationFailure` from Blob Storage is usually the network, not RBAC.** Azure Storage
  reports a network-rule denial with the same generic code it uses for permission denials, so the
  error points at the wrong thing. Check `publicNetworkAccess` on the account before auditing role
  assignments.

**One resource group per environment holds everything.** `Azure:ResourceGroup`
(`brett-aspire-demo-{env}`) is the default scope for every resource Aspire provisions — the
container apps, the Container Apps environment, storage, the password vault, and the managed
identities — and the deployment creates that RG itself (`AllowResourceGroupCreation`).

**The container registry is the only outlier.** It's a pre-existing shared registry, so it keeps
its own `acr-rg` + `acr-subscription`, and the generated template scopes exactly two modules
there: the registry reference (`acr`) and the `AcrPull` role assignment `acaenv-mi-roles-acr`
(which has to be scoped where the registry lives). Everything else is scoped to `rg`.

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
  a cross-subscription registry it does not, so pre-create the identity with `AcrPull` and name
  it in config. (Handing `WithAcrPullIdentity` a freshly created identity is
  what produces `unable to pull image using Managed identity …` on every revision.)
- **A created ACA environment lands in `Azure:ResourceGroup`** — Aspire provisions one
  deployment into one RG. It keeps the configured `aca-env-name`, and since `aca-env-rg`
  defaults to that same RG, the next deploy finds it and references it as existing rather than
  re-provisioning it (~10 minutes each time). Only an environment deliberately placed elsewhere
  needs `aca-env-rg` set.

The **registry is never auto-created**: it's a shared, pre-existing registry (possibly in
another subscription), and standing up a second one would push images nowhere useful.

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
az login                       # a principal with Contributor on the target RGs + AcrPush on the registry
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

0. **`version`** — computes the build number once (see below) and exposes it as a job output.
1. **`publish-bicep`** — a matrix over `staging` and `production` runs `aspire publish -e {env}`
   and uploads `main.bicep` plus the per-resource modules as the artifact `bicep-{env}`
   (30-day retention). Staging doesn't consume it — `aspire deploy` regenerates the same
   templates itself — but **production deploys this exact artifact**.
2. **`deploy-staging`** — `aspire deploy -e staging`, tagging both images with the commit sha.
3. **`deploy-production`** — applies the **published Bicep artifact** with plain `az`. No Aspire,
   no .NET, no Docker, not even a checkout of this repo. See below.

The two deploy jobs are deliberately different, because the contrast is the point: staging shows
what the tool does for you, production shows what that costs when you hand the artifact to a
separate release process (Octopus, an ADO release stage, a platform team).

### Build numbering

GitHub has no built-in build counter like Azure DevOps' `Build.BuildId`. The closest equivalent
is `github.run_number`, and the workflow builds the image tag from it:

```
{date}.{run_number}      e.g.  2026.09.02.42
```

| Part | Why |
|---|---|
| `date` (UTC) | sorts chronologically and is readable at a glance in the registry |
| `run_number` | monotonic per workflow, so builds order unambiguously within a day |

The commit isn't in the tag. The run summary prints it alongside the build number, and the run
itself records it, so `2026.09.02.42` is enough to find the commit that produced an image.

Two details that matter more than they look:

- **`run_number`, not `run_attempt`.** `run_number` is stable across re-runs of the same run.
  Production promotes the image staging pushed, so re-running production alone must resolve to the
  same tag — with `run_attempt` in the tag it would compute a new one and deploy an image that was
  never published.
- **Computed once, in its own job.** Every other job reads
  `needs.version.outputs.image-tag` rather than recomputing. The date component is why: an approval
  gate can hold production until the next day, and a per-job `date` would silently produce a
  different tag than the one staging pushed.

The same value flows everywhere: `aspire publish`/`aspire deploy` receive it as
`--Parameters:image-tag`, the production job passes it into the container-app modules, and ARM
deployments are named with the build number so the portal's history matches the run.

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
| `github-id-main` | `repo:purduebretty@4551941/AspireCICDDemo@1354943196:ref:refs/heads/main` | `publish-bicep` (no environment) |
| `github-id-env-staging` | `repo:purduebretty@4551941/AspireCICDDemo@1354943196:environment:staging` | `deploy-staging` |
| `github-id-env-production` | `repo:purduebretty@4551941/AspireCICDDemo@1354943196:environment:production` | `deploy-production` |

**The `owner@<owner_id>/repo@<repo_id>` form is not optional here.** GitHub now presents an
*immutable* subject built from numeric ids rather than names, so a credential written the
readable way (`repo:purduebretty/AspireCICDDemo:...`) never matches and login fails with
`AADSTS700213`. The ids come from `https://api.github.com/repos/{owner}/{repo}` (`.owner.id` and
`.id`), or straight out of the failing run's log — `azure/login` prints the exact **subject
claim** it presented, which is the string to copy. Name-based duplicates (`github-main`,
`github-env-staging`, `github-env-production`) are also present as a fallback in case the format
changes back; they're inert while GitHub sends ids.

All use issuer `https://token.actions.githubusercontent.com` and audience
`api://AzureADTokenExchange`. Running the workflow manually from a branch other than `main`
won't match any of these, so add a credential for that ref if you need it. To add one:

```bash
az identity federated-credential create \
  --name github-id-env-staging --identity-name uai-brett-github -g brett-demo-resources \
  --issuer https://token.actions.githubusercontent.com \
  --audiences api://AzureADTokenExchange \
  --subject 'repo:purduebretty@4551941/AspireCICDDemo@1354943196:environment:staging'
```

Single-quote the subject: in zsh, `$VAR:ref` triggers history modifiers (`:r`, `:e`) and
silently mangles the string, producing a credential that looks right in a table but never
matches.

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
- Every ARM deployment is named with the build number (`aspire-prod-42`, `aspire-server-42`), so
  the portal's deployment history lines up with the workflow run.
- Runners are `linux/amd64`, matching what Azure Container Apps requires — no `--platform` flag
  is needed here, unlike local builds on Apple Silicon.

## Deploying the published Bicep

`publish-bicep` renders the templates; `deploy-production` downloads that artifact and applies it
with `az deployment sub create` plus four `az deployment group create` calls. It works — and the
gaps you have to fill yourself are the interesting part:

**1. No parameters file.** `aspire publish` emits templates, not the configuration behind them.
Nothing in the artifact records the resource group, location, or registry it was generated for,
so the job re-declares all of it in `env:`. Change `appsettings.production.json` and this silently
drifts — the templates would be regenerated correctly while the job keeps deploying with stale
values.

**2. Passwords become your problem.** The data-service passwords are plain template parameters.
`aspire deploy` generates them, writes them to the password vault, and reads them back on the next
run. This path has none of that, so the job generates fresh ones each time — which restarts cache
and postgres on every deploy — and has to thread the identical values through all four modules or
the server won't match its data services.

**3. The parameter surface isn't stable.** The AppHost probes Azure while publishing, so whether
the Container Apps environment already exists decides whether the template *creates* it or
*references* it — and referencing it adds `aca_env_name`, `aca_env_rg`, and `target_subscription`
parameters that a first-ever deploy doesn't have. The same commit publishes a 6-parameter template
against an empty subscription and a 9-parameter one afterwards. A hardcoded `--parameters` list
breaks the first time that flips, so the job reads the parameters the template actually declares
(`az bicep build … | jq '.parameters | keys[]'`) and fails loudly on any it can't supply.

**4. `main.bicep` doesn't contain the apps.** It is subscription-scoped and provisions the shared
infrastructure: the resource group, the Container Apps environment, storage, the password vault,
the identities, the role assignments. Each container app is a *separate* resource-group-scoped
template that Aspire deploys itself, wiring ~11 parameters from `main`'s outputs. "Just deploy the
Bicep" is really five deployments, and that wiring is now yours to maintain — including the
server's container port, which nothing in the artifact tells you (it's `8080`).

**5. Nothing builds or pushes an image.** The tags production deploys exist only because
`deploy-staging` pushed them earlier in the same run. That makes production a true promotion of
the bits staging validated — but a production-only run would point the apps at images that were
never published.

**6. Publish-time values are frozen.** Anything the AppHost computes while generating templates is
baked in: the probe results above, the deployer's object id in the vault role assignment, and
`ASPIRE_DEPLOY_STAMP`. That last one is why `publish-bicep` passes the same
`--Parameters:image-tag=${{ github.sha }}` the deploy uses — without it the stamp would be
`latest` and the data services would never get a new revision. An artifact is only valid for the
target and the moment it was published against.

For comparison, staging expresses all of that as one line: `aspire deploy -e staging`.

### Skipping the image build with Aspire

If you'd rather have production also run `aspire deploy` and simply not rebuild, the AppHost
supports it — `--Parameters:skip-image-build=true` trims every build and push step out of the
pipeline graph, which you can confirm without deploying:

```bash
aspire deploy --list-steps -e staging -- --Parameters:skip-image-build=true   # no build-*/push-* steps
```

It works because the image names are env-agnostic (`{app}-server`, `{app}-webfrontend`) and the
`*_containerimage` Bicep parameter is *computed* as `{registry}/{resource}:{image-tag}` rather
than recorded from a push, so any deploy passing the same tag resolves to the same image. The one
rule: that tag must already exist in the registry.

A manual run takes an `environment` input (`both`, `staging`, `production`) to deploy just one.

`publish-bicep` logs into Azure too, and that isn't optional: the AppHost probes Azure while
generating templates (does the ACA environment exist? is the resource group mid-deletion? who is
the deploying principal?), so without a login the artifact would be wrong — and production
deploys that artifact verbatim.

## CI/CD via Azure DevOps + Octopus

[`azure-pipelines.yml`](../azure-pipelines.yml) is the **CI + packaging** side; **Octopus does
the actual deploy**. Per run it:

1. builds the .NET solution and the Vite frontend (fail fast);
2. `aspire do push`es the server image to the registry (built once — the image is env-agnostic),
   tagged with the run number;
3. `aspire publish`es the Bicep for every environment into `bicep/<env>/`, zips it into one
   `TicTacToe.Deploy.<version>.zip`, and pushes that package to Octopus.

The image tag and the package version are the same value, so an Octopus release maps to the
exact image the run built.

**Configure before first run** (Azure DevOps): an ARM service connection with `AcrPush` on
the container registry, an *Octopus Deploy* service connection (needs the Octopus
marketplace extension), and your Octopus space/project — all named at the top of the pipeline
(move them to a variable group if you prefer).

> ⚠️ **The package is raw Bicep — Octopus still needs a deploy step to apply it.** The
> published templates declare parameters but don't carry values, and the `cache`/`postgres`
> secrets live in Aspire's deployment state, not in the package. So the Octopus project needs
> a step (e.g. *Run an Azure CLI Script* / *Deploy an ARM template*) that applies
> `bicep/#{Octopus.Environment.Name}/main.bicep` and the per-resource modules with the
> environment's parameter + secret values. That step is not in this repo (the old `apply.sh`
> that did it was removed); if you want it back as a packaged script, say so and I'll add one.
