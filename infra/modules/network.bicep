// =============================================================================
// network.bicep — T130
//
// VNet, subnets, private DNS zones, and a private endpoint for every data service.
//
// This module exists because constitution v1.2.0 makes [SFI-NS2.2.1] "Secure PaaS Resources"
// binding: public network access must be disabled on Blob, Table/Queue, Cosmos, Redis, and Key
// Vault. Each of those resources already sets publicNetworkAccess: 'Disabled' in its own
// module, which means that without the endpoints below nothing can reach them at all.
//
// It costs roughly $40-50/month — around a third of total runtime cost — for a product whose
// data plane was already authenticated, already encrypted, and already keyless. That is the
// price of a non-risk-based corporate control, and the plan records it as such rather than
// dressing it up as an engineering preference.
//
// Resource IDs arrive as parameters rather than as `existing` references so that this module
// makes no claim to own resources another module declares.
// =============================================================================

targetScope = 'resourceGroup'

@description('Azure region.')
param location string

@description('Short environment discriminator, for example dev or prod.')
param environmentName string

@description('Address space for the virtual network.')
param vnetAddressPrefix string = '10.20.0.0/16'

@description('Subnet for the Container Apps environment. Workload profiles require at least /27; /23 leaves room to grow.')
param containerAppsSubnetPrefix string = '10.20.0.0/23'

@description('Subnet holding every private endpoint.')
param privateEndpointSubnetPrefix string = '10.20.2.0/24'

@description('Resource id of the hierarchical-namespace content storage account.')
param contentStorageAccountId string

@description('Resource id of the standard audit and queue storage account.')
param opsStorageAccountId string

@description('Resource id of the Cosmos DB account.')
param cosmosAccountId string

@description('Resource id of the Azure Cache for Redis instance.')
param redisId string

@description('Resource id of the Key Vault.')
param keyVaultId string

@description('Resource tags.')
param tags object = {}

// -----------------------------------------------------------------------------
// Virtual network
// -----------------------------------------------------------------------------

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-blinkmark-${environmentName}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: 'snet-container-apps'
        properties: {
          addressPrefix: containerAppsSubnetPrefix
          delegations: [
            {
              name: 'container-apps-delegation'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-container-apps'
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-private-endpoints'
}

// -----------------------------------------------------------------------------
// Private DNS
//
// One zone per service family. Without these, name resolution returns the public IP and every
// call fails at the firewall rather than at DNS — a failure mode that is genuinely hard to
// diagnose, which is why they are declared alongside the endpoints rather than separately.
// -----------------------------------------------------------------------------

var privateDnsZoneNames = [
  'privatelink.blob.${environment().suffixes.storage}'
  'privatelink.dfs.${environment().suffixes.storage}'
  'privatelink.table.${environment().suffixes.storage}'
  'privatelink.queue.${environment().suffixes.storage}'
  'privatelink.documents.azure.com'
  'privatelink.redis.cache.windows.net'
  'privatelink.vaultcore.azure.net'
]

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for zoneName in privateDnsZoneNames: {
    name: zoneName
    location: 'global'
    tags: tags
  }
]

resource privateDnsZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (zoneName, index) in privateDnsZoneNames: {
    parent: privateDnsZones[index]
    name: 'link-${vnet.name}'
    location: 'global'
    properties: {
      registrationEnabled: false
      virtualNetwork: {
        id: vnet.id
      }
    }
  }
]

// -----------------------------------------------------------------------------
// Private endpoints
//
// The zone index refers to the privateDnsZoneNames array above. Keeping the two lists adjacent
// is what stops an endpoint from being created against the wrong zone, which produces an
// endpoint that provisions successfully and never resolves.
// -----------------------------------------------------------------------------

var privateEndpointDefinitions = [
  {
    name: 'pe-blinkmark-content-blob'
    serviceId: contentStorageAccountId
    groupId: 'blob'
    zoneIndex: 0
  }
  {
    name: 'pe-blinkmark-content-dfs'
    serviceId: contentStorageAccountId
    groupId: 'dfs'
    zoneIndex: 1
  }
  {
    name: 'pe-blinkmark-ops-table'
    serviceId: opsStorageAccountId
    groupId: 'table'
    zoneIndex: 2
  }
  {
    name: 'pe-blinkmark-ops-queue'
    serviceId: opsStorageAccountId
    groupId: 'queue'
    zoneIndex: 3
  }
  {
    name: 'pe-blinkmark-cosmos'
    serviceId: cosmosAccountId
    groupId: 'Sql'
    zoneIndex: 4
  }
  {
    name: 'pe-blinkmark-redis'
    serviceId: redisId
    groupId: 'redisCache'
    zoneIndex: 5
  }
  {
    name: 'pe-blinkmark-keyvault'
    serviceId: keyVaultId
    groupId: 'vault'
    zoneIndex: 6
  }
]

resource privateEndpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [
  for definition in privateEndpointDefinitions: {
    name: definition.name
    location: location
    tags: tags
    properties: {
      subnet: {
        id: privateEndpointSubnet.id
      }
      privateLinkServiceConnections: [
        {
          name: '${definition.name}-connection'
          properties: {
            privateLinkServiceId: definition.serviceId
            groupIds: [definition.groupId]
          }
        }
      ]
    }
  }
]

resource privateEndpointDnsGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [
  for (definition, index) in privateEndpointDefinitions: {
    parent: privateEndpoints[index]
    name: 'default'
    properties: {
      privateDnsZoneConfigs: [
        {
          name: replace(privateDnsZoneNames[definition.zoneIndex], '.', '-')
          properties: {
            privateDnsZoneId: privateDnsZones[definition.zoneIndex].id
          }
        }
      ]
    }
    dependsOn: [
      privateDnsZoneLinks
    ]
  }
]

output vnetId string = vnet.id
output vnetName string = vnet.name
output containerAppsSubnetId string = containerAppsSubnet.id
output privateEndpointSubnetId string = privateEndpointSubnet.id
