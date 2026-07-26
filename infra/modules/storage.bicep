// =============================================================================
// storage.bicep — T008, T121
//
// Two storage accounts, because one is impossible.
//
// Set Blob Expiry — the mechanism the constitution mandates so the *platform*, not a job,
// performs deletion — requires a hierarchical-namespace account. Hierarchical-namespace
// accounts do not support the Table or Queue services. The constitution names Blob (HNS),
// Table, and Queue together without noting they cannot coexist. Two accounts is therefore
// forced, not chosen (research.md R6).
//
// It turns out well: the immutable audit trail ends up in a different account from user
// content, with its own access policy and its own resource lock, which strengthens FR-043.
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

var contentAccountName = toLower('stbmc${environmentName}${uniqueSuffix}')
var opsAccountName = toLower('stbmo${environmentName}${uniqueSuffix}')

// -----------------------------------------------------------------------------
// Account A — file content, sanitized renders, text projections. Hierarchical namespace.
// -----------------------------------------------------------------------------

resource contentStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: contentAccountName
  location: location
  tags: tags
  sku: {
    // Locally redundant. Clarification Q3 accepted best-effort durability with no DR, and
    // FR-076 tells users so rather than implying a guarantee that does not exist.
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Required for Set Blob Expiry (FR-033: the platform deletes, not a job).
    isHnsEnabled: true

    // --- SFI-ID4.2.1: no local authentication -------------------------------
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false

    // --- FR-010 / Principle I: encryption and transport ---------------------
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    encryption: {
      requireInfrastructureEncryption: true
      keySource: 'Microsoft.Storage'
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
        file: {
          enabled: true
          keyType: 'Account'
        }
      }
    }

    // --- SFI-NS2.2.1: reachable only through a private endpoint -------------
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource contentBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: contentStorage
  name: 'default'
  properties: {
    // No soft delete. Principle II means an expired file is gone; a recycle bin would quietly
    // keep content past its expiry and make SC-006 untrue (FR-031).
    deleteRetentionPolicy: {
      enabled: false
    }
    containerDeleteRetentionPolicy: {
      enabled: false
    }
    isVersioningEnabled: false
  }
}

var contentContainers = [
  'originals' // bytes exactly as uploaded, never served to a browser
  'renders' // sanitized HTML, the only thing the preview origin serves
  'projections' // normalized text: what anchors resolve against and what agents read
]

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [
  for name in contentContainers: {
    parent: contentBlobService
    name: name
    properties: {
      publicAccess: 'None'
    }
  }
]

// -----------------------------------------------------------------------------
// Account B — audit table and notification queue. Standard StorageV2, no HNS.
// -----------------------------------------------------------------------------

resource opsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: opsAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    isHnsEnabled: false

    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false

    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    encryption: {
      requireInfrastructureEncryption: true
      keySource: 'Microsoft.Storage'
      services: {
        table: {
          enabled: true
          keyType: 'Account'
        }
        queue: {
          enabled: true
          keyType: 'Account'
        }
      }
    }

    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: opsStorage
  name: 'default'
}

resource auditTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'AuditEntry'
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: opsStorage
  name: 'default'
}

resource notificationQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'notifications'
}

// -----------------------------------------------------------------------------
// T113 — resource lock on the audit account.
//
// Table Storage has no platform-enforced WORM policy (research.md R8). Append-only is upheld
// at the application layer; this lock is the platform contribution to that guarantee, and it
// is what stops an accidental resource-group delete from taking the audit trail with it.
// -----------------------------------------------------------------------------

resource auditLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: opsStorage
  name: 'blinkmark-audit-cannot-delete'
  properties: {
    level: 'CanNotDelete'
    notes: 'The audit trail outlives every file it describes (FR-043). Removing this lock is a compliance decision, not a cleanup step.'
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------

output contentStorageAccountName string = contentStorage.name
output contentStorageAccountId string = contentStorage.id
output contentBlobEndpoint string = contentStorage.properties.primaryEndpoints.blob
output contentDfsEndpoint string = contentStorage.properties.primaryEndpoints.dfs

output opsStorageAccountName string = opsStorage.name
output opsStorageAccountId string = opsStorage.id
output opsTableEndpoint string = opsStorage.properties.primaryEndpoints.table
output opsQueueEndpoint string = opsStorage.properties.primaryEndpoints.queue

output auditTableName string = auditTable.name
output notificationQueueName string = notificationQueue.name
