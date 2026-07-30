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
// The event-driven ingest worker. The quickstart placeholder exits immediately,
// which is fine for a job — executions only spawn once real messages arrive.
param workerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
@secure()
param pipelineSecrets object = {}

var jobsQueueName = 'pipeline-jobs'

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

resource queueServices 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Ingest dispatch: the API enqueues one message per pipeline run; KEDA scales
// the worker job on the queue depth. pipeline_jobs in Supabase holds the state.
resource jobsQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueServices
  name: jobsQueueName
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

// Workload-profiles environment: the serverless Consumption profile then
// allows up to 4 vCPU / 8Gi per replica (a plain consumption-only environment
// caps at 2 / 4Gi), and a GPU profile could be added here later without
// recreating anything.
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
    workloadProfiles: [{
      name: 'Consumption'
      workloadProfileType: 'Consumption'
    }]
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

// The queue connection is a secret alongside the .env mirror on every app that
// touches the jobs queue (API enqueues, worker dequeues; KEDA authenticates
// with the same secret).
var queueConnection = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
var secretsWithQueue = concat(secretDefs, [{
  name: 'jobs-queue-connection'
  value: queueConnection
}])

// FQDNs are deterministic (app name + environment default domain), which lets
// each app reference the other without a circular dependency.
var apiUrl = 'https://${apiAppName}.${env.properties.defaultDomain}'
var webUrl = 'https://${webAppName}.${env.properties.defaultDomain}'

// Scales on HTTP traffic: ingest (the heavy work) is dispatched to the worker
// job via the storage queue (Pipeline__DistributedIngest), job state lives in
// the pipeline_jobs table, and job logs live on the shared outputs mount — so
// any replica can answer any request. Light verbs (revise, publish, exports)
// still run as subprocesses of whichever replica accepted them.
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
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: registries
      secrets: secretsWithQueue
    }
    template: {
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [{
          name: 'http'
          http: { metadata: { concurrentRequests: '50' } }
        }]
      }
      containers: [{
        name: 'api'
        image: apiImage
        resources: { cpu: json('2.0'), memory: '4Gi' }
        env: concat(secretEnvVars, [
          { name: 'Api__CorsOrigins__0', value: webUrl }
          { name: 'HIGHLIGHTER_MEDIA_BASE', value: '${apiUrl}/media' }
          { name: 'Pipeline__DistributedIngest', value: 'true' }
          { name: 'JOBS_QUEUE_CONNECTION', secretRef: 'jobs-queue-connection' }
          { name: 'JOBS_QUEUE_NAME', value: jobsQueueName }
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
    workloadProfileName: 'Consumption'
    configuration: {
      // Sticky sessions require single-revision mode (the default, but the
      // platform rejects the combination unless it's explicit).
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        // Blazor Server circuits are stateful; keeps a browser pinned to one
        // replica now that this scales past one.
        stickySessions: { affinity: 'sticky' }
      }
      registries: registries
      secrets: secretDefs
    }
    template: {
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [{
          name: 'http'
          http: { metadata: { concurrentRequests: '50' } }
        }]
      }
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

// The per-run ingest worker: an event-driven Container Apps Job. KEDA polls the
// pipeline-jobs queue and spawns one execution per pending message (capped at
// maxExecutions); each execution drains messages until the queue is empty and
// exits. CPU-only by design — TransNetV2 runs on 48x27 frames and doesn't need
// a GPU (see docker/worker.Dockerfile).
resource workerJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'highlighter-worker'
  location: location
  dependsOn: [acrPull, outputsStorage]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${pullIdentity.id}': {} }
  }
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Event'
      // A live-stream capture can run for hours; the timeout is the backstop
      // for a hung run, after which the queue redelivers and the wrapper marks
      // the job failed rather than re-running half-done media work.
      replicaTimeout: 43200
      replicaRetryLimit: 0
      eventTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          minExecutions: 0
          maxExecutions: 10
          pollingInterval: 30
          rules: [{
            name: 'jobs-queue'
            type: 'azure-queue'
            metadata: {
              queueName: jobsQueueName
              queueLength: '1'
              accountName: storage.name
            }
            auth: [{
              secretRef: 'jobs-queue-connection'
              triggerParameter: 'connection'
            }]
          }]
        }
      }
      registries: registries
      secrets: secretsWithQueue
    }
    template: {
      containers: [{
        name: 'worker'
        image: workerImage
        // Consumption maximum. One ffmpeg-heavy stream per execution; raising
        // throughput means more executions, not bigger ones.
        resources: { cpu: json('4.0'), memory: '8Gi' }
        env: concat(secretEnvVars, [
          { name: 'HIGHLIGHTER_MEDIA_BASE', value: '${apiUrl}/media' }
          { name: 'JOBS_QUEUE_CONNECTION', secretRef: 'jobs-queue-connection' }
          { name: 'JOBS_QUEUE_NAME', value: jobsQueueName }
        ])
        volumeMounts: [{ volumeName: 'outputs', mountPath: '/app/outputs' }]
      }]
      volumes: [{ name: 'outputs', storageType: 'AzureFile', storageName: outputsStorage.name }]
    }
  }
}

output apiUrl string = apiUrl
output webUrl string = webUrl
output acrLoginServer string = acr.properties.loginServer
