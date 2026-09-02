# Deploying to Azure

There are two ways to deploy:

- **`aspire deploy`** (direct / local) — builds and pushes the image, provisions the
  per-environment resource group, and deploys the container apps in one command. Best for
  ad-hoc and local deploys. Covered below.
- **Azure DevOps → Octopus** (CI/CD) — [`azure-pipelines.yml`](../azure-pipelines.yml) at the
  repo root builds the app, `aspire do push`es the image to igsops, publishes the per-env
  Bicep, zips it, and pushes the package to Octopus; Octopus runs the deployment. See
  [CI/CD via Azure DevOps + Octopus](#cicd-via-azure-devops--octopus) at the end.

Both share the model below.

The container registry (`igsops`, **Enterprise Production**) and the Container Apps
environment (`brettaspiredemo-aab0`, **Enterprise Dev/Test**) live in different
subscriptions. This works because:

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

- **Shared across every environment → `appsettings.json`** — the registry (always `igsops`)
  plus the deploy subscription/location:
  ```json
  {
    "Parameters": { "acr-name": "igsops", "acr-rg": "IGS-DevOps" },
    "Azure": {
      "SubscriptionId": "2642b41d-…",
      "Location": "eastus2",
      "AllowResourceGroupCreation": true
    }
  }
  ```
- **Environment-specific → `appsettings.{env}.json`** (selected by `-e`/`--environment`):

  | File | Environment | Target resource group |
  |------|-------------|-----------------------|
  | `appsettings.local.json` | local | (run-mode; not an Azure deploy target) |
  | `appsettings.dev.json`   | dev   | `brett-aspire-demo-dev` |
  | `appsettings.int.json`   | int   | `brett-aspire-demo-int` |
  | `appsettings.qa.json`    | qa    | `brett-aspire-demo-qa` |
  | `appsettings.prod.json`  | prod  | `brett-aspire-demo-prod` |

  ```json
  {
    "Parameters": {
      "aca-env-name": "brettaspiredemo-aab0",
      "aca-env-rg": "brett-aspire-demo",
      "acr-pull-identity-name": "uai-brett-aspire-demp",
      "acr-pull-identity-rg": "brett-aspire-demo"
    },
    "Azure": { "ResourceGroup": "brett-aspire-demo-dev" }
  }
  ```

Each environment deploys its container apps into its **own** resource group
(`brett-aspire-demo-{env}`, from `Azure:ResourceGroup`), but every environment shares the
**same** container registry (`igsops`) and the **same, existing** Container Apps environment
(`brettaspiredemo-aab0` in `aca-env-rg`, referenced as existing — never re-created). The
`aca-env-*` and `acr-pull-identity-*` values are identical across env files today; only
`Azure:ResourceGroup` differs.

### App names are suffixed per environment

Because every environment shares the one Container Apps environment, and app names must be
unique *within* a managed environment, the AppHost suffixes each container app with the
environment name in publish mode: `cache-{env}`, `postgres-{env}`, `server-{env}`. This is
automatic — it derives from the `-e` value; there's nothing to set per env. Notes:

- The `server` **resource** keeps its name (so the pushed image repo stays
  `brettaspiredemo/server` and one image still promotes across envs); only its *deployed
  container-app* name is suffixed via `PublishAsAzureContainerApp`.
- The Postgres **database** stays `gamesdb` (only its server app is suffixed), so the
  Server's `gamesdb` connection lookup is unaffected.
- The Redis connection name is baked into the Server as a literal, so the AppHost passes the
  suffixed name to the Server via `Cache__ConnectionName` (defaults to `cache` locally).

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
