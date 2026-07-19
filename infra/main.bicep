// LlmProxy service-mode infrastructure — Azure App Service, Free (F1) tier.
//
// Region fallback: the default region is `swedencentral`. F1 (Free) tier quota is sometimes
// refused there; if `az deployment group create` fails with a quota/SKU-not-available error,
// redeploy with `-p location=westeurope` (the documented fallback — see brief.md "Constraints"
// and prd.md "Risks & open questions"). No code change is required, only the parameter.
//
// NVIDIA_API_KEY env var naming: the app reads its upstream credential via `ApiKeyEnv` in
// appsettings.json, which is set to the literal string "NVIDIA_API_KEY" for the "nvidia"
// provider (see ProviderOptions.ResolveApiKey() in ProxyOptions.cs — it calls
// Environment.GetEnvironmentVariable(ApiKeyEnv) using that name verbatim). This is NOT a
// double-underscore-bound `Proxy__Providers__nvidia__ApiKey` setting — it must be an App Service
// application setting literally named `NVIDIA_API_KEY`. Getting this wrong silently leaves the
// nvidia provider unauthenticated. This template wires the `nvidiaApiKey` secure parameter to an
// app setting named exactly `NVIDIA_API_KEY`.
//
// Inbound keys (Proxy:InboundKeys:<KeyId>:{App,Aliases,RequestsPerMinute}): this first-cut
// template does NOT flatten a keys parameter into `Proxy__InboundKeys__<KeyId>__App` /
// `__Aliases__0__` / `__RequestsPerMinute` app-setting entries. That mapping is a `for` loop over
// `items()` of a keys object/array and is mechanically straightforward, but the shape (one App
// key, a variable-length Aliases array, one numeric budget) is exactly the kind of operator data
// that changes across every deploy and rotation — baking it into the Bicep template (or its
// params file) means every key issuance/rotation is an infra change and a redeploy. Simpler and
// safer for a first cut: leave inbound-key app settings unset here (auth stays open, matching
// local no-keys behaviour, until keys are configured) and configure them post-deploy with
// `az webapp config appsettings set --name <app> --resource-group <rg> --settings
// "Proxy__InboundKeys__<KeyId>__App=<app-name>" "Proxy__InboundKeys__<KeyId>__Aliases__0=<alias>"
// "Proxy__InboundKeys__<KeyId>__RequestsPerMinute=<n>"` (or via the Portal's Configuration blade).
// This keeps key material and rotation entirely out of source control and out of `bicep`/`az
// deployment` history. Revisit flattening only if key management via CLI/portal becomes a
// friction point worth trading for infra-as-code churn.

@description('Base name used to derive resource names (plan, app). Must be globally unique for the web app hostname.')
param appName string = 'llmproxy'

@description('Azure region. Default swedencentral; fall back to westeurope if F1 quota is refused in the default region (see header comment).')
param location string = 'swedencentral'

@description('NVIDIA NIM API key. Supply at deploy time, e.g. --parameters nvidiaApiKey=$NVIDIA_API_KEY. Never bake into a committed params file.')
@secure()
param nvidiaApiKey string

var planName = '${appName}-plan'
var siteName = '${appName}-app'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: 'F1'
    tier: 'Free'
  }
  properties: {
    reserved: true
  }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      // Self-contained linux-x64 publish carries its own .NET runtime, so no
      // `DOTNETCORE|X.Y` linuxFxVersion stack is needed; leaving it empty lets the platform
      // just execute the deployed apphost binary instead of provisioning a managed runtime.
      linuxFxVersion: ''
      // F1 (Free) tier does not support Always On and rejects `alwaysOn: true` outright.
      // Cold starts after ~20 min idle are mitigated by the T9 keep-warm workflow.
      alwaysOn: false
      appSettings: [
        {
          // GitHub Actions zip-deploy convention: run the app directly from the uploaded
          // package rather than expanding it on disk.
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          // Must be named exactly NVIDIA_API_KEY — see header comment.
          name: 'NVIDIA_API_KEY'
          value: nvidiaApiKey
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        // Inbound-key settings (Proxy__InboundKeys__...) are intentionally not declared here.
        // See header comment — configure post-deploy via `az webapp config appsettings set`.
      ]
    }
  }
}

@description('The web app default hostname (e.g. llmproxy-app.azurewebsites.net).')
output defaultHostName string = site.properties.defaultHostName
