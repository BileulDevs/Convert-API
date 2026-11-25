// ConvertAPI on Azure Container Apps, sized to stay within the free grants:
// both apps scale to zero, files live on a 5 GiB Azure Files share, PostgreSQL is Burstable B1ms.
// Deployed per environment (test, prod) into a resource group of its own:
//   az deployment group create -g <group> --parameters infra/test.bicepparam
targetScope = 'resourceGroup'

@description('Environment name, used in resource names.')
@allowed(['test', 'prod'])
param environmentName string

param location string = resourceGroup().location

@description('API image; deployments pin the commit SHA tag, never latest.')
param apiImage string

@description('Worker image; deployments pin the commit SHA tag, never latest.')
param workerImage string

@description('PostgreSQL administrator password: long and random, letters and digits only (it goes into connection strings).')
@secure()
@minLength(16)
param postgresPassword string

@description('Client API keys, as stored by the API: hashes only, never the keys themselves.')
param apiKeys {
  id: string
  keyId: string
  name: string
  secretHash: string
}[] = []

@description('Webhook signing secret of each API key, by keyId.')
@secure()
param webhookSecrets object = {}

var suffix = take(uniqueString(resourceGroup().id), 8)
var postgresUser = 'convertapi'
var databaseName = 'convertapi'
var keyVaultSecretsUserRole = '4633458b-17de-408a-b874-0445c86b69e6'
var tags = { application: 'convertapi', environment: environmentName }

// --- Telemetry: OpenTelemetry from both apps reaches Application Insights through the
// environment's managed agent, which sets OTEL_EXPORTER_OTLP_ENDPOINT in the apps.

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-convertapi-${environmentName}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // About 4.5 GB a month at most, under the 5 GB free ingestion.
    workspaceCapping: { dailyQuotaGb: json('0.15') }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-convertapi-${environmentName}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// --- Files: uploads and results, shared by the API and the workers like the Compose volume.

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stcvt${environmentName}${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    // Container Apps mounts Azure Files with the account key.
    allowSharedKeyAccess: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource filesShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: 'files'
  properties: {
    // Results expire after 24 h, so a few GiB is plenty; 5 GiB is the free grant.
    shareQuota: 5
  }
}

// --- Database: the jobs, which are also the queue.

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-convertapi-${environmentName}-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '17'
    administratorLogin: postgresUser
    administratorLoginPassword: postgresPassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
}

// shortcut: Container Apps reaches the server over its public endpoint, open to Azure services
// only (TLS and password still required); a VNet with private access would close it further.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  // The server takes one change at a time: creating both at once fails with "server busy".
  dependsOn: [database]
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// --- Secrets: kept in Key Vault, read by the apps through managed identities. The worker, which
// hands untrusted files to FFmpeg and ImageMagick, gets an identity of its own that can only read
// the database secrets, never the webhook signing secrets.

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-convertapi-api-${environmentName}'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-convertapi-worker-${environmentName}'
  location: location
  tags: tags
}

var vaultName = 'kvcvt${environmentName}${suffix}'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, keyVaultSecretsUserRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRole)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The apps' connection string (Npgsql format). The password is quoted in both formats.
resource databaseSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'database-connection'
  properties: {
    value: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${postgresUser};Password="${postgresPassword}";Ssl Mode=Require'
  }
}

// The same database for the KEDA scalers, which expect the libpq format.
resource scalerSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'scaler-database-connection'
  properties: {
    value: 'host=${postgres.properties.fullyQualifiedDomainName} port=5432 user=${postgresUser} password=\'${postgresPassword}\' dbname=${databaseName} sslmode=require'
  }
}

resource workerReadsDatabase 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: databaseSecret
  name: guid(databaseSecret.id, workerIdentity.id, keyVaultSecretsUserRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRole)
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workerReadsScaler 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: scalerSecret
  name: guid(scalerSecret.id, workerIdentity.id, keyVaultSecretsUserRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRole)
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource webhookSecretValues 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [for key in apiKeys: {
  parent: vault
  name: 'webhook-secret-${key.keyId}'
  properties: {
    value: webhookSecrets[key.keyId]
  }
}]

// --- Container Apps.

// A preview API version: the managed OpenTelemetry agent (appInsights/openTelemetry configuration)
// has no GA version yet. It saves adding the Azure Monitor exporter to the apps.
resource appEnvironment 'Microsoft.App/managedEnvironments@2025-10-02-preview' = {
  name: 'cae-convertapi-${environmentName}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    appInsightsConfiguration: {
      connectionString: insights.properties.ConnectionString
    }
    openTelemetryConfiguration: {
      tracesConfiguration: { destinations: ['appInsights'] }
      logsConfiguration: { destinations: ['appInsights'] }
    }
  }
}

resource environmentFiles 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  parent: appEnvironment
  name: 'files'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: filesShare.name
      accessMode: 'ReadWrite'
    }
  }
}

// Key Vault secrets as each app reads them, through its own identity.
var apiVaultSecrets = [
  {
    name: 'database-connection'
    keyVaultUrl: databaseSecret.properties.secretUri
    identity: identity.id
  }
  {
    name: 'scaler-database-connection'
    keyVaultUrl: scalerSecret.properties.secretUri
    identity: identity.id
  }
]

var workerVaultSecrets = [
  {
    name: 'database-connection'
    keyVaultUrl: databaseSecret.properties.secretUri
    identity: workerIdentity.id
  }
  {
    name: 'scaler-database-connection'
    keyVaultUrl: scalerSecret.properties.secretUri
    identity: workerIdentity.id
  }
]

var sharedEnv = [
  { name: 'ConnectionStrings__convertapi', secretRef: 'database-connection' }
  { name: 'Storage__Root', value: '/files' }
]

// Owned by the images' non-root user (APP_UID 1654): an SMB mount is root-owned otherwise.
var filesVolume = {
  name: 'files'
  storageType: 'AzureFile'
  storageName: environmentFiles.name
  mountOptions: 'uid=1654,gid=1654,dir_mode=0770,file_mode=0660,mfsymlinks,nobrl'
}

// The URL is built from the vault's name, known before deployment, as a loop variable must be;
// the API depends on webhookSecretValues so the secrets exist first.
var webhookVaultSecrets = [for key in apiKeys: {
  name: 'webhook-secret-${key.keyId}'
  keyVaultUrl: 'https://${vaultName}${environment().suffixes.keyvaultDns}/secrets/webhook-secret-${key.keyId}'
  identity: identity.id
}]

// Each key's settings, as the API binds them from "ApiKeys:Keys".
var apiKeyEnv = flatten(map(range(0, length(apiKeys)), i => [
  { name: 'ApiKeys__Keys__${i}__Id', value: apiKeys[i].id }
  { name: 'ApiKeys__Keys__${i}__KeyId', value: apiKeys[i].keyId }
  { name: 'ApiKeys__Keys__${i}__Name', value: apiKeys[i].name }
  { name: 'ApiKeys__Keys__${i}__SecretHash', value: apiKeys[i].secretHash }
  { name: 'ApiKeys__Keys__${i}__WebhookSecret', secretRef: 'webhook-secret-${apiKeys[i].keyId}' }
]))

// Note for a first deployment: a new role assignment can take a few minutes to apply, so the
// apps' Key Vault references may be refused once; deploying again then succeeds. The Jobs table
// only exists once the API has started and migrated, so the scalers see errors until a first request.
resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'ca-convertapi-api-${environmentName}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [vaultReader, webhookSecretValues]
  properties: {
    managedEnvironmentId: appEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      secrets: concat(apiVaultSecrets, webhookVaultSecrets)
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(sharedEnv, apiKeyEnv, [
            { name: 'Database__MigrateOnStartup', value: 'true' }
          ])
          volumeMounts: [{ volumeName: 'files', mountPath: '/files' }]
          probes: [
            {
              // Leaves time for a cold database and the migrations at startup.
              type: 'Startup'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 3
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
            }
            {
              // The database check may take up to its own 5 s timeout.
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              timeoutSeconds: 5
            }
          ]
        }
      ]
      volumes: [filesVolume]
      scale: {
        minReplicas: 0
        maxReplicas: 3
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '20' } }
          }
          {
            // The API sends the webhooks: wake it up when some are due, even without requests.
            name: 'due-webhooks'
            custom: {
              type: 'postgresql'
              metadata: {
                query: 'SELECT COUNT(*) FROM "Jobs" WHERE "NotificationDueAt" <= now()'
                targetQueryValue: '10'
                activationTargetQueryValue: '0'
              }
              auth: [{ secretRef: 'scaler-database-connection', triggerParameter: 'connection' }]
            }
          }
        ]
      }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'ca-convertapi-worker-${environmentName}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${workerIdentity.id}': {} }
  }
  dependsOn: [workerReadsDatabase, workerReadsScaler]
  properties: {
    managedEnvironmentId: appEnvironment.id
    configuration: {
      secrets: workerVaultSecrets
    }
    template: {
      // A replica removed by scale-in gets time to hand its job back (see ConversionWorker).
      terminationGracePeriodSeconds: 120
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: {
            cpu: json('1')
            memory: '2Gi'
          }
          env: concat(sharedEnv, [
            { name: 'Conversion__WorkRoot', value: '/work' }
            { name: 'HostOptions__ShutdownTimeout', value: '00:01:30' }
          ])
          volumeMounts: [
            { volumeName: 'files', mountPath: '/files' }
            { volumeName: 'work', mountPath: '/work' }
          ]
        }
      ]
      volumes: [
        filesVolume
        // Scratch space for conversions, private to each replica (up to 4 GiB with 1 vCPU).
        { name: 'work', storageType: 'EmptyDir' }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 5
        // Down to zero one minute after the work is done, not the default five.
        cooldownPeriod: 60
        pollingInterval: 15
        rules: [
          {
            // One replica per job waiting or running (a running job keeps its worker alive), plus
            // one while expired results wait for the sweep, which only a running worker does.
            name: 'jobs'
            custom: {
              type: 'postgresql'
              metadata: {
                query: 'SELECT (SELECT COUNT(*) FROM "Jobs" WHERE "Status" IN (\'Queued\', \'Processing\')) + (EXISTS (SELECT 1 FROM "Jobs" WHERE "Status" = \'Succeeded\' AND "ExpiresAt" <= now()))::int'
                targetQueryValue: '1'
                activationTargetQueryValue: '0'
              }
              auth: [{ secretRef: 'scaler-database-connection', triggerParameter: 'connection' }]
            }
          }
        ]
      }
    }
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
