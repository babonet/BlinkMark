// =============================================================================
// main.bicep — T007
//
// Composes every BlinkMark resource for one environment.
//
// Deployment target (plan.md):
//   subscription   46a174f6-0602-4df8-9fb0-f8e8248bcb8f  ("Commerce AI Assistant")
//   resource group rg-blinkmark
//   region         eastus
//   tenant         72f988bf-86f1-41af-91ab-2d7cd011db47
//
// Ordering note: the data resources do not depend on the network. Each one disables public
// network access on its own, so they can be created first and reached later once the private
// endpoints exist. Only the Container Apps environment genuinely needs a subnet up front.
// =============================================================================

targetScope = 'resourceGroup'

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('Short environment discriminator, for example dev or prod.')
@minLength(2)
@maxLength(8)
param environmentName string = 'dev'

@description('Entra tenant that owns the application. Single-tenant by construction (Principle I).')
param tenantId string = subscription().tenantId

@description('''
Service Tree id of the owning service.

Required, and validated as a GUID rather than accepted as free text. Ownership metadata is not
decoration: an unowned resource is one nobody patches, nobody is paged for, and nobody
decommissions. It is stamped onto every resource as a tag and passed to the application so the
running service can report which registered service it belongs to.
''')
@minLength(36)
@maxLength(36)
param serviceTreeId string

@description('Application (client) id of the API app registration created by infra/entra/setup-app-registrations.ps1.')
param apiClientId string

@description('Application (client) id of the SPA app registration. Used to tell direct user actions from agent-initiated ones.')
param spaClientId string

@description('''
Client applications permitted to obtain a token for the API.

Any application in the tenant can request a token for a resource its users consent to, so without
this list an unrelated internal app could read BlinkMark drafts simply by asking its own users to
sign in. The SPA is added automatically; list any approved agent clients here.
''')
param approvedAgentClientIds array = []

@description('Audience the API validates access tokens against, normally api://<apiClientId>.')
param apiAudience string = 'api://${apiClientId}'

@description('Public origin of the SPA, for example https://app.blinkmark.example.com.')
param appOrigin string

@description('Container registry login server holding the four application images.')
param containerRegistryServer string

@description('Image tag applied to all four images. Normally the commit SHA.')
param imageTag string = 'latest'

@description('Daily Log Analytics ingestion cap in GB. App Insights is the most likely cost overrun in this budget.')
param dailyIngestionCapGb int = 1

@description('Resource tags applied to everything.')
param tags object = {
  application: 'blinkmark'
  environment: environmentName
  managedBy: 'bicep'
  serviceTreeId: serviceTreeId
}

var uniqueSuffix = take(uniqueString(resourceGroup().id), 6)

var images = {
  api: '${containerRegistryServer}/blinkmark-api:${imageTag}'
  preview: '${containerRegistryServer}/blinkmark-preview:${imageTag}'
  notifications: '${containerRegistryServer}/blinkmark-notifications:${imageTag}'
  reconciliation: '${containerRegistryServer}/blinkmark-reconciliation:${imageTag}'
}

// -----------------------------------------------------------------------------
// Observability first, so the Container Apps environment can log into it.
// -----------------------------------------------------------------------------

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    environmentName: environmentName
    dailyIngestionCapGb: dailyIngestionCapGb
    tags: tags
  }
}

// -----------------------------------------------------------------------------
// Data services. Independent of each other and of the network.
// -----------------------------------------------------------------------------

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    environmentName: environmentName
    uniqueSuffix: uniqueSuffix
    tags: tags
  }
}

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    location: location
    environmentName: environmentName
    uniqueSuffix: uniqueSuffix
    tags: tags
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis'
  params: {
    location: location
    environmentName: environmentName
    uniqueSuffix: uniqueSuffix
    tags: tags
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    environmentName: environmentName
    uniqueSuffix: uniqueSuffix
    tags: tags
  }
}

// -----------------------------------------------------------------------------
// Identity and its data-plane grants.
//
// Declared after the resources it grants on, because a role assignment needs its scope to
// exist. This is the whole of BlinkMark's authorization to Azure: seven grants, no keys.
// -----------------------------------------------------------------------------

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: {
    location: location
    environmentName: environmentName
    contentStorageAccountName: storage.outputs.contentStorageAccountName
    opsStorageAccountName: storage.outputs.opsStorageAccountName
    cosmosAccountName: cosmos.outputs.accountName
    redisName: redis.outputs.redisName
    keyVaultName: keyVault.outputs.keyVaultName
    tags: tags
  }
}

// -----------------------------------------------------------------------------
// Network. Every data service above already refuses public traffic, so nothing can reach
// them until this exists.
// -----------------------------------------------------------------------------

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    environmentName: environmentName
    contentStorageAccountId: storage.outputs.contentStorageAccountId
    opsStorageAccountId: storage.outputs.opsStorageAccountId
    cosmosAccountId: cosmos.outputs.accountId
    redisId: redis.outputs.redisId
    keyVaultId: keyVault.outputs.keyVaultId
    tags: tags
  }
}

// -----------------------------------------------------------------------------
// Compute.
// -----------------------------------------------------------------------------

module containerApps 'modules/container-apps.bicep' = {
  name: 'container-apps'
  params: {
    location: location
    environmentName: environmentName
    infrastructureSubnetId: network.outputs.containerAppsSubnetId
    userAssignedIdentityId: identity.outputs.identityId
    userAssignedIdentityClientId: identity.outputs.clientId
    logAnalyticsWorkspaceId: observability.outputs.workspaceId
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    containerRegistryServer: containerRegistryServer
    apiImage: images.api
    previewImage: images.preview
    notificationsImage: images.notifications
    reconciliationImage: images.reconciliation
    cosmosEndpoint: cosmos.outputs.endpoint
    cosmosDatabaseName: cosmos.outputs.databaseName
    contentBlobEndpoint: storage.outputs.contentBlobEndpoint
    opsTableEndpoint: storage.outputs.opsTableEndpoint
    opsQueueEndpoint: storage.outputs.opsQueueEndpoint
    redisHostName: redis.outputs.redisHostName
    keyVaultUri: keyVault.outputs.keyVaultUri
    previewSigningKeyName: keyVault.outputs.previewSigningKeyName
    tenantId: tenantId
    apiClientId: apiClientId
    apiAudience: apiAudience
    spaClientId: spaClientId
    // The SPA is always approved; anything else has to be listed deliberately.
    allowedClientIds: union([spaClientId], approvedAgentClientIds)
    serviceTreeId: serviceTreeId
    appOrigin: appOrigin
    tags: tags
  }
}

// -----------------------------------------------------------------------------
// Outputs — what the deployment pipeline and quickstart.md need.
// -----------------------------------------------------------------------------

output apiFqdn string = containerApps.outputs.apiFqdn
output previewFqdn string = containerApps.outputs.previewFqdn
output cosmosEndpoint string = cosmos.outputs.endpoint
output contentStorageAccountName string = storage.outputs.contentStorageAccountName
output opsStorageAccountName string = storage.outputs.opsStorageAccountName
output redisHostName string = redis.outputs.redisHostName
output keyVaultUri string = keyVault.outputs.keyVaultUri
output managedIdentityClientId string = identity.outputs.clientId
output managedIdentityPrincipalId string = identity.outputs.principalId
output appInsightsName string = observability.outputs.appInsightsName
