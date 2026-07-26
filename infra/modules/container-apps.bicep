// =============================================================================
// container-apps.bicep — T011, T121
//
// One VNet-integrated environment and four deployables.
//
// They are four rather than one because they have genuinely different constraints, and
// collapsing them would compromise the isolation Principle IV exists to create:
//
//   api            must stay warm for the latency budgets in SC-002 and SC-003
//   preview        must be a separate origin and must never receive a session token
//   notifications  scales from zero on queue depth (FR-037: delivery never blocks the user)
//   reconciliation cron-scheduled backstop for retention (FR-033) and comment TTL sync
//
// There is no `secrets` block anywhere in this file. Every app authenticates to every
// dependency with the user-assigned managed identity (Principle VII).
// =============================================================================

targetScope = 'resourceGroup'

@description('Azure region.')
param location string

@description('Short environment discriminator, for example dev or prod.')
param environmentName string

@description('Subnet the Container Apps environment is injected into.')
param infrastructureSubnetId string

@description('Resource id of the user-assigned managed identity every app runs as.')
param userAssignedIdentityId string

@description('Client id of that identity, passed to DefaultAzureCredential.')
param userAssignedIdentityClientId string

@description('Log Analytics workspace resource id.')
param logAnalyticsWorkspaceId string

@description('Application Insights connection string. Contains no key: ingestion is Entra-authenticated.')
param appInsightsConnectionString string

@description('Container registry login server, for example myregistry.azurecr.io.')
param containerRegistryServer string

@description('Fully qualified image reference for the API.')
param apiImage string

@description('Fully qualified image reference for the preview origin.')
param previewImage string

@description('Fully qualified image reference for the notification dispatcher.')
param notificationsImage string

@description('Fully qualified image reference for the reconciliation job.')
param reconciliationImage string

@description('Cosmos DB account endpoint.')
param cosmosEndpoint string

@description('Cosmos DB database name.')
param cosmosDatabaseName string

@description('Blob service endpoint of the content storage account.')
param contentBlobEndpoint string

@description('Table service endpoint of the audit storage account.')
param opsTableEndpoint string

@description('Queue service endpoint of the audit storage account.')
param opsQueueEndpoint string

@description('Redis host name.')
param redisHostName string

@description('Key Vault URI holding the preview-token signing key.')
param keyVaultUri string

@description('Name of the preview-token signing key.')
param previewSigningKeyName string

@description('Entra tenant id. Single-tenant by construction (Principle I).')
param tenantId string

@description('Application (client) id of the API app registration.')
param apiClientId string

@description('Audience the API validates access tokens against.')
param apiAudience string

@description('Application (client) id of the SPA, used to distinguish direct user actions from agent-initiated ones.')
param spaClientId string

@description('Client applications permitted to obtain a token for the API.')
param allowedClientIds array

@description('Service Tree id of the owning service.')
param serviceTreeId string

@description('Public origin of the SPA, used for CORS and for the preview frame-ancestors policy.')
param appOrigin string

@description('Resource tags.')
param tags object = {}

var identityConfig = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${userAssignedIdentityId}': {}
  }
}

var registries = [
  {
    server: containerRegistryServer
    // Image pull uses the managed identity. There is no registry username or password.
    identity: userAssignedIdentityId
  }
]

// Configuration shared by every deployable. Note what is absent: no connection string, no
// account key, no client secret. Each value below is an endpoint or an identifier, and the
// credential is always the ambient managed identity.
var commonEnvironment = [
  {
    name: 'AZURE_CLIENT_ID'
    value: userAssignedIdentityClientId
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'BlinkMark__Cosmos__Endpoint'
    value: cosmosEndpoint
  }
  {
    name: 'BlinkMark__Cosmos__Database'
    value: cosmosDatabaseName
  }
  {
    name: 'BlinkMark__Blob__ServiceUri'
    value: contentBlobEndpoint
  }
  {
    name: 'BlinkMark__Audit__TableServiceUri'
    value: opsTableEndpoint
  }
  {
    name: 'BlinkMark__Notifications__QueueServiceUri'
    value: opsQueueEndpoint
  }
  {
    name: 'BlinkMark__Redis__Host'
    value: redisHostName
  }
  {
    name: 'BlinkMark__KeyVault__Uri'
    value: keyVaultUri
  }
  {
    name: 'BlinkMark__Preview__SigningKeyName'
    value: previewSigningKeyName
  }
  {
    name: 'BlinkMark__Entra__TenantId'
    value: tenantId
  }
  {
    name: 'BlinkMark__Entra__ServiceTreeId'
    value: serviceTreeId
  }
]

// -----------------------------------------------------------------------------
// Environment
// -----------------------------------------------------------------------------

resource environmentResource 'Microsoft.App/managedEnvironments@2024-10-02-preview' = {
  name: 'cae-blinkmark-${environmentName}'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnetId
      // Ingress stays public — the SPA and reviewers reach it over the internet. It is the
      // *data plane* that [SFI-NS2.2.1] takes off the internet, not the application.
      internal: false
    }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        // Entra-authenticated ingestion. The workspace has disableLocalAuth: true, so there is
        // no shared key here.
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

// -----------------------------------------------------------------------------
// api — REST, SSE presence, and the MCP endpoint
//
// minReplicas: 1 because SC-002 and SC-003 put p95 budgets of 1 s and 300 ms on paths that a
// cold start would blow through on its own.
// -----------------------------------------------------------------------------

resource api 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: 'ca-blinkmark-api-${environmentName}'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    environmentId: environmentResource.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        corsPolicy: {
          allowedOrigins: [appOrigin]
          allowedMethods: ['GET', 'POST', 'PATCH', 'DELETE', 'OPTIONS']
          allowedHeaders: ['*']
          allowCredentials: true
        }
      }
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
          env: concat(commonEnvironment, [
            {
              name: 'BlinkMark__Entra__ClientId'
              value: apiClientId
            }
            {
              name: 'BlinkMark__Entra__Audience'
              value: apiAudience
            }
            {
              name: 'BlinkMark__Entra__SpaClientId'
              value: spaClientId
            }
            {
              name: 'BlinkMark__Cors__AllowedOrigin'
              value: appOrigin
            }
          ], map(range(0, length(allowedClientIds)), index => {
            // Indexed environment variables bind to the string[] the options type exposes.
            // Listing approved clients explicitly is what stops an unrelated internal app from
            // reaching this API just because one of its users consented.
            name: 'BlinkMark__Entra__AllowedClientIds__${index}'
            value: allowedClientIds[index]
          }))
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 10
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

// -----------------------------------------------------------------------------
// preview — the isolated origin (Principle IV)
//
// A separate app so it gets a separate hostname. It validates a preview token and nothing
// else, and it never receives an Entra token, a session cookie, or a refresh token. Note the
// absence of any CORS policy: nothing is supposed to call this from script.
// -----------------------------------------------------------------------------

resource preview 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: 'ca-blinkmark-preview-${environmentName}'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    environmentId: environmentResource.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'preview'
          image: previewImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(commonEnvironment, [
            {
              name: 'BlinkMark__Preview__FrameAncestor'
              value: appOrigin
            }
          ])
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 5
      }
    }
  }
}

// -----------------------------------------------------------------------------
// notifications — KEDA queue-scaled, scales to zero
//
// No ingress at all. It is reachable only through the queue, which is the shape FR-037
// requires: a notification failure cannot affect the comment that triggered it because the
// comment path never waits on this app.
// -----------------------------------------------------------------------------

resource notifications 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: 'ca-blinkmark-notifications-${environmentName}'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    environmentId: environmentResource.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'notifications'
          image: notificationsImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(commonEnvironment, [
            {
              name: 'BlinkMark__Jobs__Role'
              value: 'notifications'
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 5
        rules: [
          {
            name: 'notification-queue-depth'
            custom: {
              type: 'azure-queue'
              // Managed-identity KEDA authentication. The alternative — a queue connection
              // string in a secret — is precisely what Principle VII forbids.
              identity: userAssignedIdentityId
              metadata: {
                accountName: split(split(opsQueueEndpoint, '//')[1], '.')[0]
                queueName: 'notifications'
                queueLength: '10'
                cloud: 'AzurePublicCloud'
              }
            }
          }
        ]
      }
    }
  }
}

// -----------------------------------------------------------------------------
// reconciliation — cron-scheduled retention backstop
//
// Blob expiry and Cosmos TTL do the real work. This job exists for what the platform does not
// cascade: comments whose parent file was extended or deleted, and any blob or document the
// platform has not yet swept. Verifying that deletion is physical rather than logical is what
// makes SC-006 defensible.
// -----------------------------------------------------------------------------

resource reconciliation 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: 'cj-blinkmark-reconciliation-${environmentName}'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    environmentId: environmentResource.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 900
      replicaRetryLimit: 1
      registries: registries
      scheduleTriggerConfig: {
        // Every 15 minutes. Frequent enough that the window between logical and physical
        // deletion stays short, cheap enough that it is not a cost line.
        cronExpression: '*/15 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'reconciliation'
          image: reconciliationImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(commonEnvironment, [
            {
              name: 'BlinkMark__Jobs__Role'
              value: 'reconciliation'
            }
          ])
        }
      ]
    }
  }
}

output environmentId string = environmentResource.id
output apiFqdn string = api.properties.configuration.ingress.fqdn
output previewFqdn string = preview.properties.configuration.ingress.fqdn
output notificationsAppName string = notifications.name
output reconciliationJobName string = reconciliation.name
