// =============================================================================
// keyvault.bicep — T131
//
// Holds exactly one thing: the preview-token signing *key*.
//
// A key, not a secret. The application never retrieves it — it calls the Key Vault sign
// operation and receives a signature back (research.md R13, R14). The identity is granted
// Key Vault Crypto User, which permits sign and verify and does not permit export.
//
// The distinction matters. A signing secret in configuration is a credential that can be read
// out of a memory dump, a log line, or an environment listing. A signing key used in place
// cannot leave the vault, so there is nothing to leak and nothing to rotate on compromise of
// the application tier.
// =============================================================================

targetScope = 'resourceGroup'

@description('Azure region.')
param location string

@description('Short environment discriminator, for example dev or prod.')
param environmentName string

@description('Globally unique suffix, normally derived from the resource group id.')
param uniqueSuffix string

@description('Resource tags.')
param tags object = {}

var vaultName = toLower('kv-bm-${environmentName}-${uniqueSuffix}')

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId

    // --- No access policies. RBAC only. -------------------------------------
    // Access policies are the pre-RBAC model: they are invisible to Azure RBAC tooling, they
    // are not covered by PIM, and they cannot be reviewed centrally. An empty array here is a
    // deliberate assertion, and the SFI gate fails the build if it ever becomes non-empty.
    enableRbacAuthorization: true
    accessPolicies: []

    // No standing human role assignments are declared anywhere in this template. Operator
    // access is expected to be just-in-time.

    enabledForDeployment: false
    enabledForTemplateDeployment: false
    enabledForDiskEncryption: false

    // Purge protection cannot be turned off once on. That is the point: it stops a compromised
    // or mistaken principal from destroying the signing key and, with it, every live preview.
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true

    // --- SFI-NS2.2.1 --------------------------------------------------------
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource previewSigningKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: 'preview-token-signing'
  properties: {
    kty: 'EC'
    curveName: 'P-256'
    // sign and verify only. No wrap, no encrypt, no export — this key has exactly one job.
    keyOps: [
      'sign'
      'verify'
    ]
    attributes: {
      enabled: true
      exportable: false
    }
  }
}

output keyVaultName string = vault.name
output keyVaultId string = vault.id
output keyVaultUri string = vault.properties.vaultUri
output previewSigningKeyName string = previewSigningKey.name
output previewSigningKeyUri string = previewSigningKey.properties.keyUriWithVersion
