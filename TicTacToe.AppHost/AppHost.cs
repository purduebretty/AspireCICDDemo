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
using Azure.ResourceManager;
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

// --- Naming ----------------------------------------------------------------------------
// ONE knob shapes every name: app-name (default "aspire"). Everything else is derived, so
// nothing below hardcodes a project-specific string.
//
//   resources   {app}-{role}                 aspire-server        (stable → image repo name)
//   deployed    {app}-{role}-{env}           aspire-server-dev    (container app name)
//   infra       {app}-{env}-{token}          aspire-dev-k3x9qa    (ACA environment)
//               kv-{app}-{env}-{token}       kv-aspire-dev-k3x9qa (password vault)
//
// The token is a deterministic 6-char hash of the TARGET (subscription + resource group) plus
// app/environment — stable across deploys of the same target, distinct across targets. That's
// what keeps globally-unique names (Key Vault above all) from colliding with another
// environment's, or with a soft-deleted vault of the same name still occupying it.
//
// Only the resource group is hardcoded (Azure:ResourceGroup); the container registry is the
// one other outlier, since it's a pre-existing shared registry with its own name and RG.
var envSlug = builder.Environment.EnvironmentName.ToLowerInvariant();
var appName = builder.Configuration["Parameters:app-name"] ?? "aspire";

// The cache/postgres resources carry the target environment as a suffix (…-dev) so several
// environments can share one Container Apps environment; server/webfrontend keep stable names
// so their images stay env-agnostic. In run mode every suffix is empty — local dev is unchanged
// apart from the {app}- prefix, which keeps the dashboard consistent with what gets deployed.
var nameSuffix = builder.ExecutionContext.IsPublishMode ? $"-{envSlug}" : "";
var uniqueToken = ShortToken(
    builder.Configuration["Azure:SubscriptionId"],
    builder.Configuration["Azure:ResourceGroup"],
    appName,
    envSlug);

var cacheName = $"{appName}-cache{nameSuffix}";
var postgresName = $"{appName}-postgres{nameSuffix}";
var serverResource = $"{appName}-server";              // stable → ACR repo {app}-server
var frontendResource = $"{appName}-webfrontend";       // stable → ACR repo {app}-webfrontend
var serverApp = $"{serverResource}-{envSlug}";         // the deployed container app
var frontendApp = $"{frontendResource}-{envSlug}";

// Redis — holds live, in-progress game state (the cache).
var redis = builder.AddRedis(cacheName);

// Postgres — persists finished games and their moves for replay.
var postgres = builder.AddPostgres(postgresName);
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
// for every environment ({app}-server); the deployed container-APP name is made
// env-specific separately (see the app-name override below).
// Unlike the cache/db, the Redis *connection name* is baked into the Server's code as a
// literal ("cache"). Because the cache resource is suffixed per environment, tell the
// Server the actual name to look up (Cache:ConnectionName) rather than renaming it there.
// ExcludeLaunchProfile in publish mode: launch-profile resolution reads the csproj/
// launchSettings.json from the path baked in at BUILD time — which doesn't exist when the
// published AppHost binary runs on a different deploy agent (crashes with "Project file …
// was not found"). Publish/deploy never needs the launch profile; run mode keeps it.
var server = builder.AddProject<Projects.TicTacToe_Server>(serverResource,
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
var webfrontend = builder.AddViteApp(frontendResource, "../frontend")
    .WithReference(server)
    .WaitFor(server)
    .PublishAsStaticWebsite("/api", server)
    .WithExternalHttpEndpoints();

// The pushed images stay env-agnostic (the resource names above), but the deployed CONTAINER
// APPS are named per environment ({app}-server-{env}, {app}-webfrontend-{env}, …)
// so several environments can share one Azure Container Apps environment without name collisions.
// PublishAsAzureContainerApp only affects publish/deploy, so run mode is unchanged.
//
// Aspire's service discovery — and therefore the frontend's YARP /api proxy — targets the
// RESOURCE name ({app}-server), not this overridden app name, so the generated proxy
// URL would point at a host that doesn't exist. The WithEnvironment overrides in the Azure
// deployment section below repoint the two service-discovery env vars at the suffixed app name
// directly in the generated bicep, so no post-deploy `az containerapp update` is needed.
if (builder.ExecutionContext.IsPublishMode)
{
    server.PublishAsAzureContainerApp((_, app) => app.Name = serverApp);
    webfrontend.PublishAsAzureContainerApp((_, app) => app.Name = frontendApp);
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

    // Key Vault names are globally unique and cap at 24 characters.
    var passwordVaultName = Truncate($"kv-{appName}-{envSlug}-{uniqueToken}", 24);
    var passwordVault = builder.AddAzureKeyVault("passwords");
    passwordVault.ConfigureInfrastructure(infra =>
    {
        var kv = infra.GetProvisionableResources().OfType<KeyVaultService>().Single();
        // Pin the name (default is uniqueString-based) so the startup read below can find it.
        kv.Name = passwordVaultName;
        // Org policy is "no public endpoints", and with public access Disabled this vault is
        // unreachable (the ACA environment has no VNet/private endpoint), so password
        // persistence degrades gracefully — regenerate + revision stamp, warnings in the log.
        // "PasswordVault": { "PublicNetworkAccess": "Enabled" } per env makes persistence
        // actually work until private networking exists; dev sets it today.
        var vaultPublicAccess = builder.Configuration["PasswordVault:PublicNetworkAccess"] ?? "Disabled";
        kv.Properties.PublicNetworkAccess = vaultPublicAccess;

        // Spell the network rules out rather than leaving them null. Enabled + DefaultAction
        // Allow is what makes the vault reachable from wherever the deploy runs (agent IPs
        // vary, so an ip_rules allowlist isn't practical here) — i.e. the vault IS open to the
        // internet, protected by Azure AD + RBAC alone. That's the trade this switch buys, and
        // it's the thing to revisit once the environment is VNet-integrated.
        kv.Properties.NetworkRuleSet = new KeyVaultNetworkRuleSet
        {
            Bypass = KeyVaultNetworkRuleBypassOption.AzureServices,
            DefaultAction = string.Equals(vaultPublicAccess, "Enabled", StringComparison.OrdinalIgnoreCase)
                ? KeyVaultNetworkRuleAction.Allow
                : KeyVaultNetworkRuleAction.Deny,
        };

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
    // lives in the Enterprise Dev/Test subscription (target-subscription). The aca-env-* /
    // acr-pull-identity-* parameters are declared only on the branches that reference those
    // resources as existing — an AddParameter with no configured value PROMPTS, so declaring
    // them unconditionally would block a deploy that is creating the resource instead.
    var targetSub = builder.AddParameter("target-subscription");

    // --- Existing vs. created infrastructure --------------------------------------------
    // The shared pieces (ACA environment, ACR-pull identity) are normally pre-created by
    // Terraform and referenced as EXISTING, so a deploy never touches them. But a brand-new
    // environment has nothing yet, and referencing a resource that doesn't exist fails at
    // provisioning time ("ResourceNotFound") instead of creating it. So each one is probed
    // first with the deploy credential: found -> referenced as existing (unchanged behaviour);
    // missing -> the existing-marker is skipped, and Aspire PROVISIONS it into this
    // environment's own resource group (Azure:ResourceGroup — which the provisioner itself
    // creates when Azure:AllowResourceGroupCreation is true, see appsettings.{env}.json).
    // A probe that can't answer (no credential, no read permission) returns null and keeps the
    // existing-resource behaviour, so nothing is created by a failed lookup. Config that names
    // no resource at all is a deliberate "Aspire owns this one" — omit aca-env-name/-rg or
    // acr-pull-identity-name/-rg from appsettings.{env}.json to have Aspire create and manage it.
    // Set "Azure": { "CreateMissingInfrastructure": false } to always reference as existing.
    // The registry is deliberately NOT auto-created: igsops is a shared org registry in another
    // subscription, and silently standing up a second one would push images nowhere useful.
    var createMissing = !string.Equals(
        builder.Configuration["Azure:CreateMissingInfrastructure"], "false",
        StringComparison.OrdinalIgnoreCase);

    var arm = new ArmClient(deployCredential);
    var targetSubId = builder.Configuration["Parameters:target-subscription"];

    // ONE default resource group per environment — Azure:ResourceGroup — holds everything
    // Aspire provisions (container apps, ACA environment, storage, password vault, identities).
    // The registry is the single outlier: a pre-existing shared ACR that keeps its own acr-rg
    // and acr-subscription. So aca-env-rg / acr-pull-identity-rg only need setting when that
    // resource lives somewhere OTHER than the default RG; unset, they fall back to it. The
    // default RG is created by the provisioner when Azure:AllowResourceGroupCreation is true.
    var defaultRg = builder.Configuration["Azure:ResourceGroup"];

    // Aspire caches each provisioned module's ARM deployment id in its deployment state and
    // SKIPS the module when it's still there ("✓ Using existing deployment for storage").
    // Those ids carry the resource group they were deployed INTO, so changing
    // Azure:ResourceGroup leaves entries pointing at the old RG: the module is skipped, the
    // resource never appears in the new RG, and the deploy fails minutes later with
    // ResourceNotFound (storage account) or FailedIdentityOperation (a container app
    // referencing an identity that isn't there). Catch that here instead — the only RGs a
    // cached deployment may legitimately target are the default one and the registry's.
    var deploymentRgAllowList = new[] { defaultRg, builder.Configuration["Parameters:acr-rg"] }
        .Where(rg => !string.IsNullOrWhiteSpace(rg))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var stragglers = builder.Configuration.GetSection("Azure:Deployments").GetChildren()
        .Select(module => (Module: module.Key, Rg: ResourceGroupOf(module["Id"])))
        .Where(d => d.Rg is not null && !deploymentRgAllowList.Contains(d.Rg))
        .ToList();
    if (stragglers.Count > 0)
    {
        throw new InvalidOperationException(
            $"Deployment state has cached deployments in a resource group this deploy no longer " +
            $"targets ({string.Join(", ", stragglers.Select(d => $"{d.Module} → {d.Rg}"))}). " +
            $"Aspire would skip those modules and the deploy would fail later with ResourceNotFound. " +
            $"Delete the stale entries (or the whole file) under " +
            $"~/.aspire/deployments/<apphost-hash>/{envSlug}.json and deploy again.");
    }

    // Same trap, different cause: DELETING a resource group leaves its cached deployments
    // behind in the state file, still naming the RG this deploy targets — so the RG-mismatch
    // check above sees nothing wrong, Aspire skips those modules as "already deployed", and
    // the resources never come back. Verify the groups they name still exist.
    var deployStateSub = builder.Configuration["Azure:SubscriptionId"] ?? targetSubId;
    foreach (var rg in builder.Configuration.GetSection("Azure:Deployments").GetChildren()
                 .Select(module => ResourceGroupOf(module["Id"]))
                 .Where(rg => rg is not null).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (await ResourceGroupStateAsync(arm, deployStateSub, rg) is null)
        {
            throw new InvalidOperationException(
                $"Deployment state has cached deployments in resource group '{rg}', which no " +
                $"longer exists. Aspire would skip those modules and the deploy would fail later " +
                $"with ResourceNotFound. Delete the stale entries (or the whole file) under " +
                $"~/.aspire/deployments/<apphost-hash>/{envSlug}.json and deploy again.");
        }
    }

    // A resource group mid-deletion still resolves, but rejects every write with
    // ResourceGroupBeingDeleted — and the deploy only discovers that several steps in, after
    // images have been built and pushed. Fail before any of that work happens.
    if (string.Equals(await ResourceGroupStateAsync(arm, deployStateSub, defaultRg), "Deleting",
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Resource group '{defaultRg}' is being deleted; Azure rejects every write until that " +
            "finishes. Wait for the deletion to complete, then deploy again (the group is " +
            "re-created automatically). Note that deleting the group also soft-deletes the " +
            "password vault, whose name is globally unique — purge it before redeploying: " +
            "az keyvault purge -n <vault-name>.");
    }

    var acaEnvName = builder.Configuration["Parameters:aca-env-name"] ?? $"{appName}-{envSlug}-{uniqueToken}";
    var acaEnvRg = builder.Configuration["Parameters:aca-env-rg"] ?? defaultRg;
    var acrPullRg = builder.Configuration["Parameters:acr-pull-identity-rg"] ?? defaultRg;

    var acaEnvExists = createMissing
        ? await ResourceExistsAsync(arm, targetSubId, acaEnvRg,
            "Microsoft.App/managedEnvironments", acaEnvName)
        : null;
    var acrPullExists = createMissing
        ? await ResourceExistsAsync(arm, targetSubId, acrPullRg,
            "Microsoft.ManagedIdentity/userAssignedIdentities",
            builder.Configuration["Parameters:acr-pull-identity-name"])
        : null;



    // Only the registry is cross-subscription, so it carries its own subscription scope.
    var igsops = builder.AddAzureContainerRegistry("igsops")
        .PublishAsExistingInResourceGroup(acrName, acrRg, acrSub);

    var acaEnv = builder.AddAzureContainerAppEnvironment("acaenv");
    if (acaEnvExists is false)
    {
        // Created fresh — pinned to the configured name (the default is uniqueString-based) and
        // provisioned into Azure:ResourceGroup, not aca-env-rg: Aspire provisions a deployment
        // into one resource group, so a newly created environment lives with its apps. Later
        // deploys still won't find it under aca-env-rg and keep managing it here — an idempotent
        // update of the same resource. Point aca-env-rg at the resource group it actually landed
        // in (or at a Terraform-created environment) to go back to referencing it as existing.
        Console.WriteLine(
            $"info: container apps environment '{acaEnvName}' " +
            $"was not found in '{acaEnvRg}'; it will be created in " +
            "this environment's resource group.");
        acaEnv.ConfigureInfrastructure(infra =>
        {
            var managedEnv = infra.GetProvisionableResources()
                .OfType<ContainerAppManagedEnvironment>().Single();
            managedEnv.Name = acaEnvName;
        });
    }
    else
    {
        acaEnv.PublishAsExistingInResourceGroup(
            builder.AddParameter("aca-env-name", acaEnvName),
            builder.AddParameter("aca-env-rg", acaEnvRg!),
            targetSub);
    }

    acaEnv.WithAzureContainerRegistry(igsops);

    // WithAcrPullIdentity is BYO-identity: it tells Aspire to pull with the identity given and
    // to mint NO role assignment. That is only correct when the identity already exists AND
    // already holds AcrPull (Terraform grants it) — pointing it at an identity Aspire just
    // created produces an identity with no permissions, and every revision fails with
    // "unable to pull image using Managed identity …". So when the pre-created identity isn't
    // there, don't pass one: Aspire creates its own identity and the AcrPull role assignment
    // to go with it. That needs RBAC-write in the registry's subscription, which the deploy
    // principal has for a same-subscription registry but not for the cross-subscription one.
    if (acrPullExists is not false)
    {
        acaEnv.WithAcrPullIdentity(builder.AddAzureUserAssignedIdentity("acrpull")
            .PublishAsExistingInResourceGroup(
                builder.AddParameter("acr-pull-identity-name"),
                builder.AddParameter("acr-pull-identity-rg", acrPullRg!),
                targetSub));
    }
    else
    {
        var crossSubscriptionAcr = !string.Equals(
            builder.Configuration["Parameters:acr-subscription"], targetSubId,
            StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(
            $"info: ACR-pull identity '{builder.Configuration["Parameters:acr-pull-identity-name"]}' " +
            $"was not found in '{acrPullRg}'; Aspire will " +
            "create one and grant it AcrPull on the registry." + (crossSubscriptionAcr
                ? " WARNING: the registry is in a different subscription, so that role assignment " +
                  "needs RBAC-write there and will likely fail — pre-create the identity with " +
                  "AcrPull (Terraform) instead."
                : string.Empty));
    }

    // Repoint the frontend's /api proxy at the env-suffixed server app. PublishAsStaticWebsite
    // emits YARP's destination as the RESOURCE name (http://{app}-server) and relies
    // on Container Apps' in-environment DNS, where an app is reachable at http://{app-name}.
    // The deployed app is {app}-server-{env}, so the unsuffixed name doesn't resolve
    // ("Name or service not known (…-server:80)") — YARP dials this address
    // literally; the services__* vars below are NOT consulted for it. A later WithEnvironment
    // with the same key wins, so this bakes the corrected destination into the generated bicep
    // (replacing the post-deploy `az containerapp update` repoint the Octopus process performs).
    // Staying on the app name keeps the hop INSIDE the environment — no trip out to the public
    // FQDN and back — which is why this isn't the acaDomain-based URL.
    webfrontend.WithEnvironment(
        "REVERSEPROXY__CLUSTERS__api__DESTINATIONS__destination1__ADDRESS",
        $"http://{serverApp}");

    // Aspire also injects the server's address for service discovery under the resource name,
    // pointing at the UNSUFFIXED app (which doesn't exist). Nothing reads these today, but
    // leaving them wrong is a trap for the next thing that does — point them at the real app.
    var acaDomain = acaEnv.GetOutput("AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN");
    var serverFqdn = ReferenceExpression.Create($"https://{serverApp}.{acaDomain}");
    var serverEnvKey = serverResource.ToUpperInvariant().Replace('-', '_');
    webfrontend
        .WithEnvironment($"services__{serverResource}__https__0", serverFqdn)
        .WithEnvironment($"services__{serverResource}__http__0", serverFqdn)
        .WithEnvironment($"{serverEnvKey}_HTTPS", serverFqdn)
        .WithEnvironment($"{serverEnvKey}_HTTP", serverFqdn);

    // Tag both pushed images with the build number. Pass it to `aspire do push`:
    //   aspire do push -- --Parameters:image-tag=<build-number-or-branch>  (default "latest")
    // Stable resource names → env-agnostic images ({app}-server, {app}-webfrontend):
    // built + pushed once, deployed to every environment. The
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
    if (builder.Configuration[$"Parameters:{cacheName}-password"] is null)
    {
        redis.WithEnvironment("ASPIRE_DEPLOY_STAMP", imageTag);
    }
    if (builder.Configuration[$"Parameters:{postgresName}-password"] is null)
    {
        postgres.WithEnvironment("ASPIRE_DEPLOY_STAMP", imageTag);
    }

    // After provisioning, persist the resolved data-service passwords into the per-env
    // vault so the NEXT deploy reads them back (stable passwords from run 2 onward). The
    // step is registered without dependencies and wired below only when the deploy-graph
    // steps exist, so publish/push runs simply leave it orphaned (never executed).
    var passwordParameterNames = new[] { $"{cacheName}-password", $"{postgresName}-password" };
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

// The provisioning state of a resource group ("Succeeded", "Deleting", …), or null when it
// doesn't exist or can't be read — callers treat null as "not there".
static async Task<string?> ResourceGroupStateAsync(ArmClient arm, string? subscription, string? name)
{
    if (string.IsNullOrWhiteSpace(subscription) || string.IsNullOrWhiteSpace(name))
    {
        return null;
    }

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var id = new Azure.Core.ResourceIdentifier($"/subscriptions/{subscription}/resourceGroups/{name}");
        var group = await arm.GetResourceGroupResource(id).GetAsync(timeout.Token);
        return group.Value.Data.ResourceGroupProvisioningState;
    }
    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
    {
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"warn: could not read the state of resource group '{name}' " +
                          $"({ex.GetType().Name}); continuing.");
        return null;
    }
}

// The resource group segment of an ARM resource/deployment id, or null when there isn't one.
static string? ResourceGroupOf(string? id)
{
    var match = System.Text.RegularExpressions.Regex.Match(
        id ?? "", "/resourceGroups/(?<rg>[^/]+)/",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["rg"].Value : null;
}

// A short, stable, lowercase-alphanumeric token for the given inputs. Deterministic (same
// inputs → same token on every machine and every deploy), so names never churn, and it is not
// a secret — it exists only to keep globally-unique names from colliding across targets.
static string ShortToken(params string?[] parts)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts.Select(p => p ?? ""))));
    const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    var value = BitConverter.ToUInt64(bytes, 0);
    return string.Concat(Enumerable.Range(0, 6).Select(_ =>
    {
        var c = alphabet[(int)(value % (ulong)alphabet.Length)];
        value /= (ulong)alphabet.Length;
        return c;
    }));
}

// Trims a generated name to a service's length cap, keeping the trailing uniqueness token
// (the tail is what makes the name unique, so cut from the middle-left, not the end).
static string Truncate(string name, int max)
    => name.Length <= max ? name : string.Concat(name.AsSpan(0, max - 7), name.AsSpan(name.Length - 7));

// Probes whether an Azure resource already exists, using the deploy credential.
//   true  — found, reference it as existing.
//   false — nothing to reference (not found, or the config names no resource), so create it.
//   null  — the lookup itself failed (no credential, no read permission); callers keep the
//           existing-resource behaviour so a bad probe never duplicates infrastructure.
static async Task<bool?> ResourceExistsAsync(
    ArmClient arm, string? subscription, string? resourceGroup, string resourceType, string? name)
{
    if (string.IsNullOrWhiteSpace(subscription)
        || string.IsNullOrWhiteSpace(resourceGroup)
        || string.IsNullOrWhiteSpace(name))
    {
        return false;
    }

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var id = new Azure.Core.ResourceIdentifier(
            $"/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/{resourceType}/{name}");
        await arm.GetGenericResource(id).GetAsync(timeout.Token);
        return true;
    }
    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
    {
        // The resource group (or the whole path) isn't there either — nothing exists.
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"warn: could not check whether {resourceType}/{name} exists ({ex.GetType().Name}); " +
            "treating it as existing.");
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
