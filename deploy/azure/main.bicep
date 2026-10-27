// Facility Management System on Azure: Container Apps (API, worker, web, migrator job), Azure SQL, Blob storage, Key Vault, Log Analytics.
// Deploy:  az deployment group create -g <rg> -f deploy/azure/main.bicep -p deploy/azure/main.example.bicepparam
// Secrets: the SQL admin password is a secure parameter; both connection strings are written to Key Vault and read by the apps through a managed identity.
// Nothing here is a hard dependency on Azure services in the application: the same images run on Kubernetes (deploy/helm) and Docker.

targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource (3 to 12 letters or digits).')
@minLength(3)
@maxLength(12)
param name string

param location string = resourceGroup().location

@description('Image registry and tag, for example ghcr.io/mirffan215 and 0.8.0.')
param imageRegistry string = 'ghcr.io/mirffan215'
param imageTag string

@description('SaaS serves many organisations; OnPrem is one organisation (not typical on Azure, but supported).')
@allowed(['SaaS', 'OnPrem'])
param deploymentMode string = 'SaaS'

@description('OnPrem only: the one organisation id.')
param singleTenantId string = ''

@description('Public address people open the web app at, for QR labels, for example https://fms.example.com.')
param publicBaseUrl string

@description('Identity providers. Each: { name, authority, audience, clientId, scope, issuerPrefix, externalTenantClaim } (the last two only for multi-organisation providers such as Entra common).')
param authProviders array

@description('SQL administrator login and password. The password is only used to build the connection strings stored in Key Vault.')
param sqlAdminLogin string = 'fmsadmin'
@secure()
param sqlAdminPassword string

@description('Password for the least-privileged login the application uses (created by the migrator).')
@secure()
param sqlAppPassword string

@description('First administrator as <provider>:<subject>, made by the migrator.')
param initialAdmin string = ''

@description('Web content-security connect-src, normally your identity provider origins.')
param cspConnectSrc string = 'https:'

param apiMinReplicas int = 2
param apiMaxReplicas int = 6

var prefix = toLower(name)
var uniq = uniqueString(resourceGroup().id)
var storageName = take('${prefix}${uniq}', 24)
var vaultName = take('${prefix}-kv-${uniq}', 24)
var sqlServerName = '${prefix}-sql-${uniq}'
var dbName = 'Fms'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 90 }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id'
  location: location
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_ZRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    encryption: { services: { blob: { enabled: true } }, keySource: 'Microsoft.Storage' }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: { deleteRetentionPolicy: { enabled: true, days: 14 } }
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'fms'
  properties: { publicAccess: 'None' }
}

var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, storageBlobDataContributor)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'   // tighten with a private endpoint or firewall rules for production; see docs/deployment.md
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: dbName
  location: location
  sku: { name: 'GP_S_Gen5_2', tier: 'GeneralPurpose' }
  properties: { autoPauseDelay: -1, minCapacity: json('0.5'), zoneRedundant: false, requestedBackupStorageRedundancy: 'Zone' }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
  }
}

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, keyVaultSecretsUser)
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}

resource appConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'sql-app'
  properties: { value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${dbName};User Id=fms_app;Password=${sqlAppPassword};Encrypt=True;TrustServerCertificate=False' }
}

resource migratorConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'sql-migrator'
  properties: { value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${dbName};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False' }
}

resource appPassword 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'sql-app-password'
  properties: { value: sqlAppPassword }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}

var providerSettingGroups = [for (p, i) in authProviders: concat(
  [
    { name: 'Authentication__Providers__${i}__Name', value: p.name }
    { name: 'Authentication__Providers__${i}__Authority', value: p.authority }
    { name: 'Authentication__Providers__${i}__Audience', value: p.audience }
  ],
  contains(p, 'clientId') ? [{ name: 'Authentication__Providers__${i}__ClientId', value: p.clientId }] : [],
  contains(p, 'scope') ? [{ name: 'Authentication__Providers__${i}__Scope', value: p.scope }] : [],
  contains(p, 'issuerPrefix') ? [{ name: 'Authentication__Providers__${i}__IssuerPrefix', value: p.issuerPrefix }] : [],
  contains(p, 'externalTenantClaim') ? [{ name: 'Authentication__Providers__${i}__ExternalTenantClaim', value: p.externalTenantClaim }] : []
)]
var providerSettings = flatten(providerSettingGroups)

var commonSettings = concat([
  { name: 'Deployment__Mode', value: deploymentMode }
  { name: 'Deployment__SingleTenantId', value: singleTenantId }
  { name: 'Storage__Provider', value: 'AzureBlob' }
  { name: 'Storage__AzureBlob__AccountUrl', value: storage.properties.primaryEndpoints.blob }
  { name: 'Storage__AzureBlob__Container', value: 'fms' }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'Facilities__PublicBaseUrl', value: publicBaseUrl }
  { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
  { name: 'Security__TrustedProxyCidrs__0', value: '100.64.0.0/10' }
  { name: 'Security__TrustedProxyCidrs__1', value: '10.0.0.0/8' }
], providerSettings)

var registrySecrets = [
  { name: 'sql-app', keyVaultUrl: '${vault.properties.vaultUri}secrets/sql-app', identity: identity.id }
  { name: 'sql-migrator', keyVaultUrl: '${vault.properties.vaultUri}secrets/sql-migrator', identity: identity.id }
  { name: 'sql-app-password', keyVaultUrl: '${vault.properties.vaultUri}secrets/sql-app-password', identity: identity.id }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [vaultRole, appConnection]
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: false, targetPort: 8080, transport: 'auto', allowInsecure: true }   // only reachable inside the environment; the web app is the single public door
      secrets: registrySecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${imageRegistry}/fms-api:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(commonSettings, [{ name: 'ConnectionStrings__Default', secretRef: 'sql-app' }])
          probes: [
            { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 20 }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: { minReplicas: apiMinReplicas, maxReplicas: apiMaxReplicas, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '60' } } }] }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-worker'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [vaultRole, appConnection]
  properties: {
    managedEnvironmentId: env.id
    configuration: { activeRevisionsMode: 'Single', secrets: registrySecrets }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${imageRegistry}/fms-worker:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(commonSettings, [{ name: 'ConnectionStrings__Default', secretRef: 'sql-app' }])
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-web'
  location: location
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${imageRegistry}/fms-web:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'API_UPSTREAM', value: 'http://${api.properties.configuration.ingress.fqdn}' }
            { name: 'CSP_CONNECT_SRC', value: cspConnectSrc }
            { name: 'NGINX_ENVSUBST_FILTER', value: 'API_UPSTREAM|CSP_CONNECT_SRC' }
          ]
        }
      ]
      scale: { minReplicas: 2, maxReplicas: 4 }
    }
  }
}

// Run once per release before moving traffic: az containerapp job start -n <name>-migrate -g <rg>
resource migrate 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-migrate'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [vaultRole, migratorConnection, appPassword]
  properties: {
    environmentId: env.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      secrets: registrySecrets
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: '${imageRegistry}/fms-migrator:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'Deployment__Mode', value: deploymentMode }
            { name: 'Deployment__SingleTenantId', value: singleTenantId }
            { name: 'ConnectionStrings__Migrator', secretRef: 'sql-migrator' }
            { name: 'FMS_APP_LOGIN', value: 'fms_app' }
            { name: 'FMS_APP_LOGIN_PASSWORD', secretRef: 'sql-app-password' }
            { name: 'FMS_INITIAL_ADMIN', value: initialAdmin }
          ]
        }
      ]
    }
  }
}

output webUrl string = 'https://${web.properties.configuration.ingress.fqdn}'
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output keyVault string = vault.name
output migrateJob string = migrate.name
