// =============================================================================
// redis.bicep — T010
//
// Azure Cache for Redis Basic C0. The single deviation from the constitution's named platform
// services, carried in the plan's Complexity Tracking table.
//
// It exists because three separate requirements all need state shared across API replicas, and
// the constitution's own stateless rule forbids holding any of it in process:
//
//   * presence            FR-059 to FR-069
//   * rate limiting       FR-054, FR-085
//   * quota counters      FR-083
//
// Basic C0 has no SLA and restarts without warning. That is deliberate, not an oversight:
// FR-069 requires preview and commenting to keep working when presence is unavailable, so a
// Redis outage must degrade one feature and nothing else. Quickstart check four verifies it.
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

resource redis 'Microsoft.Cache/redis@2024-11-01' = {
  name: toLower('redis-blinkmark-${environmentName}-${uniqueSuffix}')
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    sku: {
      name: 'Basic'
      family: 'C'
      capacity: 0
    }

    // --- SFI-ID4.2.7 / C+E FUN Security P0: no access keys -------------------
    // Entra authentication only. Access policy assignments are granted in identity.bicep.
    disableAccessKeyAuthentication: true
    redisConfiguration: {
      'aad-enabled': 'True'
      // Presence is transient by construction (FR-067) — there is nothing here worth keeping,
      // and eviction under memory pressure is the correct behaviour rather than a failure.
      'maxmemory-policy': 'volatile-ttl'
    }

    // --- Transport ----------------------------------------------------------
    minimumTlsVersion: '1.2'
    enableNonSslPort: false

    // --- SFI-NS2.2.1 --------------------------------------------------------
    publicNetworkAccess: 'Disabled'
  }
}

output redisName string = redis.name
output redisId string = redis.id
output redisHostName string = redis.properties.hostName
output redisSslPort int = redis.properties.sslPort
