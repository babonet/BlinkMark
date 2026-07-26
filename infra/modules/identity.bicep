// =============================================================================
// identity.bicep — T129
//
// The user-assigned managed identity every BlinkMark Container App runs as, plus every
// data-plane grant it needs.
//
// Constitution Principle VII: BlinkMark holds no retrievable secret. Every grant below is an
// identity-based role assignment. There is no key to rotate, no connection string to leak, and
// nothing an operator can copy out of the portal and paste somewhere else.
//
// Grants are declared here rather than inside each resource module so that the complete answer
// to "what can this application reach?" is one file, not six.
// =============================================================================

targetScope = 'resourceGroup'

@description('Azure region for the managed identity.')
param location string

@description('Short environment discriminator, for example dev or prod.')
param environmentName string

@description('Name of the hierarchical-namespace storage account holding file content.')
param contentStorageAccountName string

@description('Name of the standard storage account holding the audit table and notification queue.')
param opsStorageAccountName string

@description('Name of the Cosmos DB account.')
param cosmosAccountName string

@description('Name of the Azure Cache for Redis instance.')
param redisName string

@description('Name of the Key Vault holding the preview-token signing key.')
param keyVaultName string

@description('Resource tags.')
param tags object = {}

// -----------------------------------------------------------------------------
// The identity itself
// -----------------------------------------------------------------------------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-blinkmark-${environmentName}'
  location: location
  tags: tags
}

// -----------------------------------------------------------------------------
// Built-in role definition IDs
// -----------------------------------------------------------------------------

var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageTableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var keyVaultCryptoUser = '12338af0-0e69-4776-bea7-57ae8d297424'

// The Cosmos data-plane role. Note this is *not* an Azure RBAC role assignment: Cosmos has its
// own data-plane role model exposed through sqlRoleAssignments, and Azure RBAC roles such as
// "Cosmos DB Account Reader" grant control-plane access only. Using the wrong one is the most
// common reason disableLocalAuth: true appears to "break" an application.
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

// -----------------------------------------------------------------------------
// Existing resources the grants are scoped to.
//
// These are references, not declarations — each resource is owned by its own module. Scoping
// each assignment to the individual resource keeps the blast radius of a compromise to exactly
// the data BlinkMark needs, rather than the whole resource group.
// -----------------------------------------------------------------------------

resource contentStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: contentStorageAccountName
}

resource opsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: opsStorageAccountName
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
}

resource redis 'Microsoft.Cache/redis@2024-11-01' existing = {
  name: redisName
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

// -----------------------------------------------------------------------------
// Blob — file content, sanitized renders, and text projections
// -----------------------------------------------------------------------------

resource contentBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: contentStorage
  name: guid(contentStorage.id, identity.id, storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Table — the audit trail (append-only by application contract, research.md R8)
// Queue — notification dispatch
// -----------------------------------------------------------------------------

resource opsTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: opsStorage
  name: guid(opsStorage.id, identity.id, storageTableDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource opsQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: opsStorage
  name: guid(opsStorage.id, identity.id, storageQueueDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Cosmos — data-plane contributor over the whole account
// -----------------------------------------------------------------------------

resource cosmosDataRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, identity.id, cosmosDataContributorRoleId)
  properties: {
    principalId: identity.properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    scope: cosmos.id
  }
}

// -----------------------------------------------------------------------------
// Redis — Entra access policy assignment
//
// Redis does not use Azure RBAC for its data plane. Access policy assignments are the
// mechanism that makes disableAccessKeyAuthentication: true survivable.
// -----------------------------------------------------------------------------

resource redisDataOwner 'Microsoft.Cache/redis/accessPolicyAssignments@2024-11-01' = {
  parent: redis
  name: 'blinkmark-data-owner'
  properties: {
    accessPolicyName: 'Data Owner'
    objectId: identity.properties.principalId
    objectIdAlias: identity.name
  }
}

// -----------------------------------------------------------------------------
// Key Vault — Crypto User, not Crypto Officer
//
// Crypto User permits sign and verify. It does not permit export, create, or delete. The
// preview-token signing key is signed in place and never leaves the vault (research.md R13).
// -----------------------------------------------------------------------------

resource keyVaultSignerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultCryptoUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------

output identityId string = identity.id
output identityName string = identity.name
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
