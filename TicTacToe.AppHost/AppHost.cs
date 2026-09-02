using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Storage;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = DistributedApplication.CreateBuilder(args);

// When the pipeline runs (publish/deploy) WITHOUT the aspire CLI attached — e.g. the published
// AppHost binary in a CI deploy stage — step progress normally vanishes (the default reporter
// feeds a channel only the CLI reads). Print it to the console instead. The check on
// ASPIRE_BACKCHANNEL_PATH keeps CLI runs (`aspire do push`, `aspire deploy`) on the CLI's own
// rendering — registering ours there would blank the CLI's output.
if (builder.ExecutionContext.IsPublishMode
    && Environment.GetEnvironmentVariable("ASPIRE_BACKCHANNEL_PATH") is null)
{
    builder.Services.AddSingleton<Aspire.Hosting.Pipelines.IPipelineActivityReporter,
        TicTacToe.AppHost.ConsolePipelineReporter>();
}

// Each environment deploys into its own resource group AND its own existing Azure Container Apps
// environment (see the Azure deployment section below). The cache/postgres resources still carry
// the target environment as a suffix (cache-dev, postgres-dev, …) via nameSuffix; the
// server/webfrontend keep stable names so their images stay env-agnostic. In run mode the names
// stay bare, so local development is unchanged.
var envSlug = builder.Environment.EnvironmentName.ToLowerInvariant();
var nameSuffix = builder.ExecutionContext.IsPublishMode ? $"-{envSlug}" : "";

// Redis — holds live, in-progress game state (the cache).
var redis = builder.AddRedis($"cache{nameSuffix}");

// Postgres — persists finished games and their moves for replay.
var postgres = builder.AddPostgres($"postgres{nameSuffix}");
if (builder.ExecutionContext.IsRunMode)
{
    // Local-dev persistence only. In Azure this maps to Azure Files, which the
    // org's "no public network access on storage" policy forbids, so it's omitted.
    postgres.WithDataVolume();
}

// The database keeps a stable name ("gamesdb") even though its Postgres server is
// suffixed — the name is the connection key the Server looks up, and the generated
// connection string still points at the (suffixed) server host, so nothing breaks.
var gamesDb = postgres.AddDatabase("gamesdb");

// Azure Blob Storage — holds user avatar images. RunAsEmulator means Azurite is
// used when running locally, while a real storage account is provisioned when
// publishing to Azure. Avatars live in the "userimages" container (container names
// must be lowercase); the container stays private and images are served back through
// the API (no public blob access).
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var blobs = storage.AddBlobs("blobs");
storage.AddBlobContainer("userimages", blobContainerName: "userimages");

// Org policy forbids public storage ("Storage accounts should disable public network access"),
// which blocks provisioning otherwise. The server reaches blobs via its managed identity
// (Azure AD), so key auth and public blobs stay off too. NOTE: this shared ACA environment has
// no VNet, so a private-only storage account is NOT reachable from the container apps — the
// avatar feature won't work at runtime until the environment is VNet-integrated with a private
// endpoint. (Provisioning + everything else still succeeds; only blob access is affected.)
storage.ConfigureInfrastructure(infra =>
{
    var account = infra.GetProvisionableResources().OfType<StorageAccount>().Single();
    account.PublicNetworkAccess = StoragePublicNetworkAccess.Disabled;
    account.AllowSharedKeyAccess = false;
    account.AllowBlobPublicAccess = false;
});

// API backend (ASP.NET Core). The frontend is now its own image (below), so the server just
// exposes the API. The resource name is STABLE (no env suffix) so the pushed IMAGE is the same
// for every environment (brettaspiredemo-server); the deployed container-APP name is made
// env-specific separately (see the app-name override below).
// Unlike the cache/db, the Redis *connection name* is baked into the Server's code as a
// literal ("cache"). Because the cache resource is suffixed per environment, tell the
// Server the actual name to look up (Cache:ConnectionName) rather than renaming it there.
// ExcludeLaunchProfile in publish mode: launch-profile resolution reads the csproj/
// launchSettings.json from the path baked in at BUILD time — which doesn't exist when the
// published AppHost binary runs on a different deploy agent (crashes with "Project file …
// was not found"). Publish/deploy never needs the launch profile; run mode keeps it.
var server = builder.AddProject<Projects.TicTacToe_Server>("brettaspiredemo-server",
        options => options.ExcludeLaunchProfile = builder.ExecutionContext.IsPublishMode)
    .WithReference(redis).WaitFor(redis)
    .WithReference(gamesDb).WaitFor(gamesDb)
    .WithReference(blobs)
    .WithEnvironment("Cache__ConnectionName", redis.Resource.Name);

// Without the launch profile there are no implicit endpoints in publish mode — declare the
// http endpoint explicitly (ACA fronts it with HTTPS ingress regardless). Must precede
// WithHttpHealthCheck, which resolves its endpoint eagerly. Run mode keeps the
// launch-profile endpoints, so nothing is added there.
if (builder.ExecutionContext.IsPublishMode)
{
    server.WithHttpEndpoint();
}
server.WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Vite/React frontend as its own image. In run mode this is the Vite dev server (which
// proxies /api to the server via vite.config.ts). In publish it's built and served as its
// own container image by YARP (PublishAsStaticWebsite), which also reverse-proxies /api to
// the server via service discovery — so the browser stays same-origin (no CORS) and the
// frontend code is unchanged.
var webfrontend = builder.AddViteApp("brettaspiredemo-webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server)
    .PublishAsStaticWebsite("/api", server)
    .WithExternalHttpEndpoints();

// The pushed images stay env-agnostic (the resource names above), but the deployed CONTAINER
// APPS are named per environment (brettaspiredemo-server-dev, brettaspiredemo-webfrontend-dev, …)
// so several environments can share one Azure Container Apps environment without name collisions.
// PublishAsAzureContainerApp only affects publish/deploy, so run mode is unchanged.
//
// Aspire's service discovery — and therefore the frontend's YARP /api proxy — targets the
// RESOURCE name (brettaspiredemo-server), not this overridden app name, so the generated proxy
// URL would point at a host that doesn't exist. The WithEnvironment overrides in the Azure
// deployment section below repoint the two service-discovery env vars at the suffixed app name
// directly in the generated bicep, so no post-deploy `az containerapp update` is needed.
if (builder.ExecutionContext.IsPublishMode)
{
    server.PublishAsAzureContainerApp((_, app) => app.Name = $"brettaspiredemo-server-{envSlug}");
    webfrontend.PublishAsAzureContainerApp((_, app) => app.Name = $"brettaspiredemo-webfrontend-{envSlug}");
}

// --- Azure deployment (publish/deploy only) --------------------------------
// Images are pushed to the existing igsops registry, which lives in the Enterprise
// Production subscription. The container apps deploy into an existing Container Apps
// environment in the Enterprise Dev/Test subscription and pull from igsops using a
// pre-created user-assigned identity that already holds AcrPull on igsops (provisioned
// via Terraform). Because the registry is in a different subscription than everything
// else, it's referenced with PublishAsExistingInResourceGroup(name, rg, subscription)
// (Aspire 13.5+) so the lookup is scoped to the right subscription. WithAcrPullIdentity
// still tells Aspire to use that identity rather than minting its own AcrPull role
// assignment — a cross-subscription role assignment would require the deploy principal
// to have RBAC-write in Production, which we don't want.
//
// Parameter values come from per-environment appsettings.{env}.json (selected by
// DOTNET_ENVIRONMENT), and can be overridden by env vars (Parameters__name) or
// `-- --Parameters:name=value`. .NET configuration merges these layers per key, so:
//   * registry parameters are identical for every environment -> shared appsettings.json
//   * environment/identity parameters vary per environment    -> appsettings.{env}.json
//
// The resource group the container apps deploy INTO is not an AddParameter — it's the
// Aspire provisioning setting `Azure:ResourceGroup`, which varies per environment
// (brett-aspire-demo-{env}) in appsettings.{env}.json. The shared subscription/location
// live in the base appsettings.json. Note this is distinct from `aca-env-rg`: the container apps
// land in their per-env RG and attach to that environment's own existing ACA environment
// (referenced as existing via aca-env-name / aca-env-rg per environment), and all pull from the
// one shared igsops registry.
if (builder.ExecutionContext.IsPublishMode)
{
    // --- Per-environment password vault (created BY Aspire in each env's RG) ------------
    // Persists the generated data-service passwords across deploys: read back into config at
    // startup (secret `Parameters--x` → config `Parameters:x`, which beats generation and
    // disables the revision stamp below), written after provisioning by the
    // persist-passwords pipeline step. First run: the vault doesn't exist yet → the read
    // warns and continues (generated passwords + revision stamp keep that run consistent);
    // provisioning then creates the vault and persist-passwords seeds it.
    // Credential for the vault read/write done by the AppHost itself. Honor
    // Azure:CredentialSource like Aspire's own provisioning does — critically, on Azure-hosted
    // build agents DefaultAzureCredential would pick the pool VM's MANAGED IDENTITY over the
    // AzureCLI task's login (ManagedIdentityCredential precedes AzureCliCredential in its
    // chain), binding vault access to the wrong principal. AzureCliCredential also fails fast
    // when not logged in, instead of hanging on managed-identity endpoint probes.
    Azure.Core.TokenCredential deployCredential =
        builder.Configuration["Azure:CredentialSource"] == "AzureCli"
            ? new AzureCliCredential()
            : new DefaultAzureCredential();

    // Object id of whoever is running this publish/deploy (service connection in CI, az
    // login locally) — used below to grant vault access. Null when no credential is
    // available (e.g. the audit `aspire publish` step), which just omits the role assignment.
    var deployerObjectId = await TryGetDeployerObjectIdAsync(deployCredential);

    var passwordVaultName = $"kv-brettaspiredemo-{envSlug}";   // vault names cap at 24 chars
    var passwordVault = builder.AddAzureKeyVault("passwords");
    passwordVault.ConfigureInfrastructure(infra =>
    {
        var kv = infra.GetProvisionableResources().OfType<KeyVaultService>().Single();
        // Pin the name (default is uniqueString-based) so the startup read below can find it.
        kv.Name = passwordVaultName;
        // Org policy: no public endpoints. This demo's shared ACA env has no VNet/private
        // endpoint yet, so with public access Disabled the vault is unreachable and password
        // persistence degrades gracefully (regenerate + revision stamp, warnings in the log).
        // Set "PasswordVault": { "PublicNetworkAccess": "Enabled" } per env to make
        // persistence functional until private networking exists.
        kv.Properties.PublicNetworkAccess =
            builder.Configuration["PasswordVault:PublicNetworkAccess"] ?? "Disabled";

        // Grant the DEPLOYING principal (service connection in CI, az login locally) rights
        // to read/write secrets — the persist-passwords step and the startup read need it.
        // NONE of Aspire's known parameters resolve to the deployer during `aspire deploy`:
        // principalId/principalType throw ("An Azure principal parameter was not supplied a
        // value") because the provisioner only fills them in run mode, and userPrincipalId is
        // only wired by the azd/publish path (ARM InvalidTemplate at deploy). So the deployer
        // object id is resolved directly from the deploy credential's token (oid claim, see
        // TryGetDeployerObjectIdAsync below) and baked into the role assignment as a literal.
        // principalType is omitted — ARM infers it (SP in CI, User locally).
        if (deployerObjectId is not null)
        {
            var secretsOfficerRole = BicepFunction.GetSubscriptionResourceId(
                "Microsoft.Authorization/roleDefinitions", "b86a8fe4-44ce-4948-aee5-eccb2c155cd7");
            infra.Add(new RoleAssignment("passwords_deployer_secrets_officer")
            {
                Name = BicepFunction.CreateGuid(kv.Id, deployerObjectId, secretsOfficerRole),
                Scope = new IdentifierExpression(kv.BicepIdentifier),
                PrincipalId = Guid.Parse(deployerObjectId),
                RoleDefinitionId = secretsOfficerRole,
            });
        }
        else
        {
            Console.WriteLine(
                "warn: no Azure credential available — the password vault's deployer role " +
                "assignment is omitted from this run's template.");
        }
    });

    try
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri($"https://{passwordVaultName}.vault.azure.net/"),
            deployCredential,
            new ParameterSecretsOnlyManager());
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"warn: password vault '{passwordVaultName}' not readable ({ex.GetType().Name}); " +
            "using generated passwords this run.");
    }

    // Shared across all environments (appsettings.json).
    var acrName = builder.AddParameter("acr-name");
    var acrRg = builder.AddParameter("acr-rg");
    var acrSub = builder.AddParameter("acr-subscription");   // igsops lives in Enterprise Production.

    // Environment-specific (appsettings.{env}.json). Everything except the ACR
    // lives in the Enterprise Dev/Test subscription (target-subscription).
    var envName = builder.AddParameter("aca-env-name");
    var envRg = builder.AddParameter("aca-env-rg");
    var identityName = builder.AddParameter("acr-pull-identity-name");
    var identityRg = builder.AddParameter("acr-pull-identity-rg");
    var targetSub = builder.AddParameter("target-subscription");

    // Only the registry is cross-subscription, so it carries its own subscription scope.
    var igsops = builder.AddAzureContainerRegistry("igsops")
        .PublishAsExistingInResourceGroup(acrName, acrRg, acrSub);

    var acrPull = builder.AddAzureUserAssignedIdentity("acrpull")
        .PublishAsExistingInResourceGroup(identityName, identityRg, targetSub);

    var acaEnv = builder.AddAzureContainerAppEnvironment("acaenv")
        .PublishAsExistingInResourceGroup(envName, envRg, targetSub)
        .WithAzureContainerRegistry(igsops)
        .WithAcrPullIdentity(acrPull);

    // Repoint the frontend's /api proxy at the env-suffixed server app. Service discovery
    // generates these vars from the RESOURCE name (brettaspiredemo-server), but the deployed
    // app is brettaspiredemo-server-{env}; a later WithEnvironment with the same key wins, so
    // this bakes the correct FQDN into the generated bicep (replacing the post-deploy
    // `az containerapp update` repoint the Octopus process performs).
    var acaDomain = acaEnv.GetOutput("AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN");
    var serverFqdn = ReferenceExpression.Create(
        $"https://brettaspiredemo-server-{envSlug}.{acaDomain}");
    webfrontend
        .WithEnvironment("services__brettaspiredemo-server__https__0", serverFqdn)
        .WithEnvironment("services__brettaspiredemo-server__http__0", serverFqdn);

    // Tag both pushed images with the build number. Pass it to `aspire do push`:
    //   aspire do push -- --Parameters:image-tag=<build-number-or-branch>  (default "latest")
    // Stable resource names → env-agnostic images (brettaspiredemo-server,
    // brettaspiredemo-webfrontend): built + pushed once, deployed to every environment. The
    // container-APP names are made per-environment by the PublishAsAzureContainerApp override
    // above, so environments can share one ACA environment; the deploy repoints the frontend's
    // /api proxy at the suffixed server app name.
    var imageTag = builder.Configuration["Parameters:image-tag"] ?? "latest";
    server.WithRemoteImageTag(imageTag);
    webfrontend.WithRemoteImageTag(imageTag);

    // CI wipes deployment state, so GENERATED cache/postgres passwords change every deploy —
    // but ACA secret updates alone don't restart running replicas, and these apps' templates
    // otherwise never change, so their replicas would keep the OLD password forever while
    // each new server revision uses the NEW one (28P01 auth failures). Stamping the tag into
    // their templates forces a new revision per deploy, so they restart with the current
    // password and stay in lockstep with the server (their data is ephemeral regardless — no
    // volumes in ACA). When a STABLE password is supplied explicitly (e.g. the pipeline's
    // optional Key Vault fetch → Parameters:cache-{env}-password), the stamp is skipped:
    // nothing drifts, so cache/postgres keep running undisturbed across deploys.
    if (builder.Configuration[$"Parameters:cache{nameSuffix}-password"] is null)
    {
        redis.WithEnvironment("ASPIRE_DEPLOY_STAMP", imageTag);
    }
    if (builder.Configuration[$"Parameters:postgres{nameSuffix}-password"] is null)
    {
        postgres.WithEnvironment("ASPIRE_DEPLOY_STAMP", imageTag);
    }

    // After provisioning, persist the resolved data-service passwords into the per-env
    // vault so the NEXT deploy reads them back (stable passwords from run 2 onward). The
    // step is registered without dependencies and wired below only when the deploy-graph
    // steps exist, so publish/push runs simply leave it orphaned (never executed).
    var passwordParameterNames = new[] { $"cache{nameSuffix}-password", $"postgres{nameSuffix}-password" };
    builder.Pipeline.AddStep("persist-passwords", async ctx =>
    {
        var client = new SecretClient(
            new Uri($"https://{passwordVaultName}.vault.azure.net/"), deployCredential);
        foreach (var parameterName in passwordParameterNames)
        {
            var parameter = ctx.Model.Resources.OfType<ParameterResource>()
                .FirstOrDefault(p => string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));
            if (parameter is null)
            {
                continue;
            }

            var value = await parameter.GetValueAsync(ctx.CancellationToken);
            var secretName = $"Parameters--{parameterName}";

            // Fresh role assignments on a just-created vault can take a little while to
            // propagate; retry before giving up. An unreachable vault (no public access and
            // no private endpoint yet) must not fail the deploy — next run regenerates and
            // the revision stamp keeps everything consistent.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    string? existing = null;
                    try
                    {
                        existing = (await client.GetSecretAsync(secretName, cancellationToken: ctx.CancellationToken)).Value.Value;
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                    }

                    if (existing != value)
                    {
                        await client.SetSecretAsync(secretName, value, ctx.CancellationToken);
                        ctx.Logger.LogInformation("Persisted '{Secret}' to vault {Vault}.", secretName, passwordVaultName);
                    }
                    break;
                }
                catch (Exception ex) when (attempt < 4)
                {
                    ctx.Logger.LogWarning("Vault write attempt {Attempt} failed ({Error}); retrying in 15s…",
                        attempt, ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(15), ctx.CancellationToken);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex,
                        "Could not persist '{Secret}' to vault {Vault}; passwords will regenerate next deploy " +
                        "(revision stamp keeps services consistent).", secretName, passwordVaultName);
                    break;
                }
            }
        }
    });

    // Wire persist-passwords into the deploy graph only when that graph exists (publish/push
    // runs don't have provision-azure-bicep-resources; an unwired step is simply skipped).
    acaEnv.WithPipelineConfiguration(ctx =>
    {
        var persist = ctx.Steps.FirstOrDefault(s => s.Name == "persist-passwords");
        var provisionAll = ctx.Steps.FirstOrDefault(s => s.Name == "provision-azure-bicep-resources");
        var deploy = ctx.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.Deploy);
        // Guard against duplicates: configuration callbacks run once per resolution pass,
        // and steps registered via builder.Pipeline are the same instance across passes.
        if (persist is not null && provisionAll is not null && deploy is not null)
        {
            if (!persist.DependsOnSteps.Contains(provisionAll.Name))
            {
                persist.DependsOn(provisionAll);
            }
            if (!deploy.DependsOnSteps.Contains(persist.Name))
            {
                deploy.DependsOn(persist);
            }
        }
    });

    // Deploy-only mode: `--Parameters:skip-image-build=true` trims every build/push step out
    // of the pipeline graph so `aspire do deploy` provisions bicep only, reusing images already
    // pushed by an earlier `aspire do push` with the same image-tag. Safe because the
    // *_containerimage bicep parameter is computed ({registry}/{resource-name}:{image-tag}),
    // not recorded from a push. Verify the trim with:
    //   aspire do deploy --list-steps -e dev -- --Parameters:skip-image-build=true
    if (string.Equals(builder.Configuration["Parameters:skip-image-build"], "true",
            StringComparison.OrdinalIgnoreCase))
    {
        // NOTE: must be a RESOURCE-level configuration on a resource added AFTER the compute
        // resources — the ACA integration adds the push→provision edges in its own resource
        // configuration callback, and callbacks run pipeline-level first, then resource
        // annotations in model order. A builder.Pipeline.AddPipelineConfiguration would run
        // BEFORE the edges exist and trim nothing.
        acaEnv.WithPipelineConfiguration(ctx =>
        {
            var imageStepNames = ctx.Steps
                .Where(s => s.Tags.Contains(WellKnownPipelineTags.BuildCompute)
                         || s.Tags.Contains(WellKnownPipelineTags.PushContainerImage))
                .Select(s => s.Name)
                .ToHashSet();

            // Cut both edge directions: DependsOn edges pointing at build/push steps, and
            // RequiredBy edges the build/push steps declare (normalized into DependsOn later).
            foreach (var step in ctx.Steps)
            {
                step.DependsOnSteps.RemoveAll(imageStepNames.Contains);
                if (imageStepNames.Contains(step.Name))
                {
                    step.RequiredBySteps.Clear();
                }
            }

            return Task.CompletedTask;
        });
    }
}

builder.Build().Run();

// Resolves the object id of the current Azure credential by reading the 'oid' claim from an
// ARM access token — the same principal `aspire deploy` provisions with. Returns null when
// no credential is available so callers can degrade gracefully.
static async Task<string?> TryGetDeployerObjectIdAsync(Azure.Core.TokenCredential credential)
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["https://management.azure.com/.default"]),
            timeout.Token);
        var payload = token.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var claims = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
        return claims.RootElement.TryGetProperty("oid", out var oid) ? oid.GetString() : null;
    }
    catch
    {
        return null;
    }
}

// Only load Parameters--* secrets from the password vault — keeps unrelated vault secrets
// out of the AppHost's configuration (and off the wire).
internal sealed class ParameterSecretsOnlyManager : KeyVaultSecretManager
{
    public override bool Load(Azure.Security.KeyVault.Secrets.SecretProperties secret)
        => secret.Name.StartsWith("Parameters--", StringComparison.OrdinalIgnoreCase);
}
