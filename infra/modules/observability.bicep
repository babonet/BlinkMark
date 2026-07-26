// =============================================================================
// observability.bicep — T012, T112, T126
//
// Application Insights over a Log Analytics workspace, with Entra-authenticated ingestion and
// a hard daily cap.
//
// Two decisions worth knowing about:
//
//   * The cap is set at provisioning time, not left for someone to notice on a bill. The plan
//     lists ingestion as the most likely cost overrun in a product whose entire runtime budget
//     is around $95-120/month.
//   * The audit trail is deliberately NOT routed here. Audit lives in Table Storage
//     (research.md R8) because it must outlive every file it describes; telemetry is sampled,
//     capped, and retained for 30 days, which is the opposite of what FR-043 needs.
// =============================================================================

targetScope = 'resourceGroup'

@description('Azure region.')
param location string

@description('Short environment discriminator, for example dev or prod.')
param environmentName string

@description('Daily ingestion cap in GB. Ingestion stops for the rest of the day when reached.')
param dailyIngestionCapGb int = 1

@description('Public hostname of the API, probed for availability against the 99.5% target (SC-023).')
param apiHostName string = ''

@description('Resource tags.')
param tags object = {}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-blinkmark-${environmentName}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      // --- SFI: no workspace keys. Ingestion is Entra-authenticated. --------
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    workspaceCapping: {
      dailyQuotaGb: dailyIngestionCapGb
    }
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-blinkmark-${environmentName}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    // Instrumentation keys are a bearer credential in a query string. Connection-string +
    // managed-identity ingestion is the credential-free path (Principle VII).
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Enabled'
    SamplingPercentage: 100
    RetentionInDays: 30
  }
}

// -----------------------------------------------------------------------------
// T126 — availability probe against the 99.5% target (SC-023)
//
// Probes the API health endpoint, which is unauthenticated by design and returns no content
// metadata. Nothing in the preview or file paths is probed, because doing so would require the
// probe to hold a credential.
// -----------------------------------------------------------------------------

resource availabilityTest 'Microsoft.Insights/webtests@2022-06-15' = if (!empty(apiHostName)) {
  name: 'wt-blinkmark-api-${environmentName}'
  location: location
  tags: union(tags, {
    'hidden-link:${appInsights.id}': 'Resource'
  })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: 'wt-blinkmark-api-${environmentName}'
    Name: 'BlinkMark API availability'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      { Id: 'us-va-ash-azr' }
      { Id: 'us-il-ch1-azr' }
      { Id: 'emea-nl-ams-azr' }
    ]
    Request: {
      RequestUrl: 'https://${apiHostName}/health'
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 14
    }
  }
}

// -----------------------------------------------------------------------------
// T112 — dashboard-backing saved queries and the ingestion-cap alert
// -----------------------------------------------------------------------------

resource ingestionCapAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-blinkmark-ingestion-cap-${environmentName}'
  location: location
  tags: tags
  properties: {
    displayName: 'Log ingestion daily cap reached'
    description: 'Ingestion has hit the daily cap. Telemetry is being dropped for the rest of the day; investigate before raising the cap.'
    severity: 3
    enabled: true
    scopes: [workspace.id]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    criteria: {
      allOf: [
        {
          query: '_LogOperation | where Category == "Ingestion" and Operation has "Data collection"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
  }
}

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
