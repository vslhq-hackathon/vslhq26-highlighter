// Minimal Azure footprint for Highlighter: one Container Apps environment, an
// ACR the GitHub pipeline pushes to, an Azure Files share so renders and
// DataProtection keys survive restarts, and the two apps (api + web).
// Supabase stays external — nothing here replaces it.
//
// First deploy (resource-group scope; the apps come up on a placeholder image
// until the pipeline pushes real ones):
//   az group create -n highlighter-rg -l <region>
//   az deployment group create -g highlighter-rg -f infra/main.bicep \
//     -p acrName=<globallyUniqueAcrName> pipelineSecrets=@secrets.json
// where secrets.json is {"SUPABASE_URL": "...", "AZURE_OPENAI_API_KEY": "...", ...}
// mirroring .env — every key becomes a Container Apps secret + env var on both apps.

param location string = resourceGroup().location
param acrName string
param apiImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
param webImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
@secure()
param pipelineSecrets object = {}

var apiAppName = 'highlighter-api'
var webAppName = 'highlighter-web'
// Container Apps secret names must be lowercase alphanumeric + dashes.
var secretDefs = [for s in items(pipelineSecrets): {
  name: toLower(replace(s.key, '_', '-'))
  value: s.value
}]
var secretEnvVars = [for s in items(pipelineSecrets): {
  name: s.key
  secretRef: toLower(replace(s.key, '_', '-'))
}]

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'highlighter-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

// One identity shared by both apps, used only to pull from ACR. User-assigned
// so the AcrPull grant can exist before the apps are created (a system
// identity can't pull the image its own app is being created from).
resource pullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'highlighter-pull'
  location: location
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, pullIdentity.id, 'acrpull')
  scope: acr
  properties: {
    principalId: pullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    // AcrPull built-in role
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: uniqueString(resourceGroup().id, 'highlighter')
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: { allowBlobPublicAccess: false }
}

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Worker renders + the API's /media mirror.
resource outputsShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileServices
  name: 'outputs'
  properties: { shareQuota: 100 }
}

// Blazor Server DataProtection key ring (keeps sessions valid across restarts).
resource keysShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileServices
  name: 'dpkeys'
  properties: { shareQuota: 1 }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'highlighter-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource outputsStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: env
  name: 'outputs'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: outputsShare.name
      accessMode: 'ReadWrite'
    }
  }
}

resource keysStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: env
  name: 'dpkeys'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: keysShare.name
      accessMode: 'ReadWrite'
    }
  }
}

var registries = [{
  server: '${acrName}.azurecr.io'
  identity: pullIdentity.id
}]

// FQDNs are deterministic (app name + environment default domain), which lets
// each app reference the other without a circular dependency.
var apiUrl = 'https://${apiAppName}.${env.properties.defaultDomain}'
var webUrl = 'https://${webAppName}.${env.properties.defaultDomain}'

// Single replica by design: pipeline jobs are tracked in API process memory
// and renders land on the replica-local (shared-mounted) outputs dir. Scaling
// out needs a real job queue first.
resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiAppName
  location: location
  dependsOn: [acrPull, outputsStorage]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${pullIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: registries
      secrets: secretDefs
    }
    template: {
      scale: { minReplicas: 1, maxReplicas: 1 }
      containers: [{
        name: 'api'
        image: apiImage
        resources: { cpu: json('2.0'), memory: '4Gi' }
        env: concat(secretEnvVars, [
          { name: 'Api__CorsOrigins__0', value: webUrl }
          { name: 'HIGHLIGHTER_MEDIA_BASE', value: '${apiUrl}/media' }
        ])
        volumeMounts: [{ volumeName: 'outputs', mountPath: '/app/outputs' }]
        // /livez, not /healthz: the deep check flips 503 when Supabase is
        // unreachable, which must not restart the container.
        probes: [{
          type: 'Liveness'
          httpGet: { path: '/livez', port: 8080 }
          initialDelaySeconds: 10
          periodSeconds: 30
        }]
      }]
      volumes: [{ name: 'outputs', storageType: 'AzureFile', storageName: outputsStorage.name }]
    }
  }
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: webAppName
  location: location
  dependsOn: [acrPull, keysStorage]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${pullIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        // Blazor Server circuits are stateful; keep a browser pinned to one
        // replica if this ever scales past one.
        stickySessions: { affinity: 'sticky' }
      }
      registries: registries
      secrets: secretDefs
    }
    template: {
      scale: { minReplicas: 1, maxReplicas: 1 }
      containers: [{
        name: 'web'
        image: webImage
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(secretEnvVars, [
          { name: 'Api__BaseUrl', value: apiUrl }
          { name: 'DataProtection__KeysDir', value: '/keys' }
        ])
        volumeMounts: [{ volumeName: 'dpkeys', mountPath: '/keys' }]
      }]
      volumes: [{ name: 'dpkeys', storageType: 'AzureFile', storageName: keysStorage.name }]
    }
  }
}

output apiUrl string = apiUrl
output webUrl string = webUrl
output acrLoginServer string = acr.properties.loginServer
