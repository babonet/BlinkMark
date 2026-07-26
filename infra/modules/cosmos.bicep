// =============================================================================
// cosmos.bicep — T009, T121
//
// Serverless Cosmos holding the mutable working set: files, comments, notifications, and user
// preferences. Partition keys follow research.md R7 and are chosen around the two hot paths
// the success criteria put a latency budget on:
//
//   * files      /id           — preview is a point read (SC-002, p95 < 1 s)
//   * comments   /fileId       — "all comments for this file" is single-partition (SC-003, p95 < 300 ms)
//
// Per-item TTL is what makes Principle II real: Cosmos excludes expired items from query
// results *before* physically deleting them, so a file reads as gone the moment it expires
// without any application filtering (FR-032).
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

var accountName = toLower('cosmos-blinkmark-${environmentName}-${uniqueSuffix}')
var databaseName = 'blinkmark'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: accountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]

    // --- SFI-ID4.2.3: no local authentication -------------------------------
    // With this set, the account keys stop working entirely. Every caller must present an
    // Entra token and hold a sqlRoleAssignment (granted in identity.bicep).
    disableLocalAuth: true

    // --- SFI-NS2.2.1 --------------------------------------------------------
    publicNetworkAccess: 'Disabled'
    isVirtualNetworkFilterEnabled: false
    ipRules: []

    // --- FR-010: encryption and transport -----------------------------------
    minimalTlsVersion: 'Tls12'

    // Single region, no failover. Clarification Q3 accepted best-effort availability with no
    // DR; pretending otherwise here would cost money and still not be a backup.
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]

    consistencyPolicy: {
      // Session consistency is what makes "I just uploaded it, now list my files" behave.
      defaultConsistencyLevel: 'Session'
    }

    backupPolicy: {
      type: 'Periodic'
      periodicModeProperties: {
        // The minimum the platform allows. FR-076 tells users there is no backup, and the
        // audit trail — not Cosmos — is the record that outlives content.
        backupIntervalInMinutes: 1440
        backupRetentionIntervalInHours: 8
        backupStorageRedundancy: 'Local'
      }
    }
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

// -----------------------------------------------------------------------------
// Containers
//
// defaultTtl: -1 means "TTL is enabled, but only items that carry their own ttl expire".
// That is exactly what is needed: each file's ttl is derived from its expiresAt, and userPrefs
// must never expire at all.
// -----------------------------------------------------------------------------

var containerDefinitions = [
  {
    name: 'files'
    partitionKey: '/id'
    defaultTtl: -1
  }
  {
    name: 'comments'
    partitionKey: '/fileId'
    defaultTtl: -1
  }
  {
    name: 'notifications'
    partitionKey: '/recipientId'
    defaultTtl: -1
  }
  {
    name: 'userPrefs'
    partitionKey: '/id'
    defaultTtl: null
  }
]

resource containers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for definition in containerDefinitions: {
    parent: database
    name: definition.name
    properties: {
      resource: {
        id: definition.name
        partitionKey: {
          paths: [definition.partitionKey]
          kind: 'Hash'
          version: 2
        }
        defaultTtl: definition.defaultTtl
        indexingPolicy: {
          indexingMode: 'consistent'
          automatic: true
          includedPaths: [
            {
              path: '/*'
            }
          ]
          excludedPaths: [
            {
              // Anchors are resolved client-side against the text projection; indexing the
              // quoted passage would cost RUs on every comment write to serve no query.
              path: '/anchor/*'
            }
            {
              path: '/"_etag"/?'
            }
          ]
        }
      }
    }
  }
]

output accountName string = account.name
output accountId string = account.id
output endpoint string = account.properties.documentEndpoint
output databaseName string = database.name
