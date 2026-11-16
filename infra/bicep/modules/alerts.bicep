param location string
param tags object
param alertEmail string = ''

param serviceBusName string
param apiAppName string
param apiHostName string
param functionAppName string
param appInsightsName string
param workspaceId string

resource serviceBus 'Microsoft.ServiceBus/namespaces@2026-01-01' existing = {
  name: serviceBusName
}

resource apiApp 'Microsoft.Web/sites@2024-11-01' existing = {
  name: apiAppName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-azurelab-ops'
  location: 'Global'
  tags: tags
  properties: {
    groupShortName: 'AzureLabOps'
    enabled: true
    emailReceivers: empty(alertEmail)
      ? []
      : [
          {
            name: 'OpsEmail'
            emailAddress: alertEmail
            useCommonAlertSchema: true
          }
        ]
  }
}

var sbDlqEntities = [
  {
    suffix: 'redeem-requests'
    entityName: 'redeem-requests'
  }
  {
    suffix: 'offer-events-ledger'
    entityName: 'offer-events/Subscriptions/ledger'
  }
  {
    suffix: 'offer-events-notification'
    entityName: 'offer-events/Subscriptions/notification'
  }
]

resource sbDlqAlerts 'Microsoft.Insights/metricAlerts@2018-03-01' = [
  for item in sbDlqEntities: {
    name: 'alert-sb-dlq-${item.suffix}'
    location: 'global'
    tags: tags
    properties: {
      description: 'Runbook: Run DlqTool peek on ${item.entityName}, inspect DeadLetterReason + CorrelationId in KQL, fix root cause, then resubmit.'
      severity: 1
      enabled: true
      scopes: [
        serviceBus.id
      ]
      evaluationFrequency: 'PT1M'
      windowSize: 'PT5M'
      criteria: {
        'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
        allOf: [
          {
            name: 'DeadletteredMessages'
            metricName: 'DeadletteredMessages'
            metricNamespace: 'Microsoft.ServiceBus/namespaces'
            timeAggregation: 'Maximum'
            operator: 'GreaterThan'
            threshold: 0
            criterionType: 'StaticThresholdCriterion'
            dimensions: [
              {
                name: 'EntityName'
                operator: 'Include'
                values: [
                  item.entityName
                ]
              }
            ]
          }
        ]
      }
      actions: [
        {
          actionGroupId: actionGroup.id
        }
      ]
    }
  }
]

// Service Bus has no native message-age metric; Minimum > 0 over 15m proves active messages never drained.
resource sbActiveStuckAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-sb-active-stuck-redeem'
  location: 'global'
  tags: tags
  properties: {
    description: 'Runbook: Check Worker health (/health/ready) and AppTraces for consumer errors or lock loss on redeem-requests.'
    severity: 2
    enabled: true
    scopes: [
      serviceBus.id
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ActiveMessagesStuck'
          metricName: 'ActiveMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          timeAggregation: 'Minimum'
          operator: 'GreaterThan'
          threshold: 0
          criterionType: 'StaticThresholdCriterion'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: [
                'redeem-requests'
              ]
            }
          ]
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

resource api5xxAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-api-http-5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'Runbook: Run KQL Failed Requests & Dependencies; check SQL connectivity, Storage 403s, or recent slot swap.'
    severity: 1
    enabled: true
    scopes: [
      apiApp.id
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'Http5xx'
          metricName: 'Http5xx'
          metricNamespace: 'Microsoft.Web/sites'
          timeAggregation: 'Total'
          operator: 'GreaterThan'
          threshold: 2
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

resource apiLatencyAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-api-latency'
  location: 'global'
  tags: tags
  properties: {
    description: 'Runbook: Run KQL Slow SQL Dependencies (>500ms); check serverless SQL auto-resume or DTU/vCore saturation.'
    severity: 2
    enabled: true
    scopes: [
      apiApp.id
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'HttpResponseTime'
          metricName: 'HttpResponseTime'
          metricNamespace: 'Microsoft.Web/sites'
          timeAggregation: 'Average'
          operator: 'GreaterThan'
          threshold: 2
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

resource apiHealthWebTest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: 'webtest-api-health-live'
  location: location
  kind: 'standard'
  tags: union(tags, {
    'hidden-link:${appInsights.id}': 'Resource'
  })
  properties: {
    Name: 'webtest-api-health-live'
    Description: 'Standard availability test on API /health/live'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      {
        Id: 'us-tx-sn1-azr'
      }
      {
        Id: 'us-il-ch1-azr'
      }
    ]
    Request: {
      HttpVerb: 'GET'
      RequestUrl: 'https://${apiHostName}/health/live'
      FollowRedirects: false
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}

resource apiAvailabilityAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-api-health-availability'
  location: location
  tags: tags
  properties: {
    displayName: 'API /health/live availability failed'
    description: 'Runbook: Check App Service status, recent deployment/swap, and /health/live HTTP logs.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      workspaceId
    ]
    criteria: {
      allOf: [
        {
          query: 'AppAvailabilityResults | where Name == "webtest-api-health-live" and Success == false'
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
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

resource outboxLagAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-outbox-lag'
  location: location
  tags: tags
  properties: {
    displayName: 'Outbox dispatch lag > 60s or Worker stopped'
    description: 'Runbook: Check Worker app status (running?), OutboxDispatcher logs for SQL/SB errors, and OutboxMessages.RetryCount.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspaceId
    ]
    criteria: {
      allOf: [
        {
          query: '''
            let accepted = AppTraces
              | where TimeGenerated > ago(15m)
              | where Message has "Redeem accepted"
              | extend CorrelationId = extract(@"CorrelationId=([^\s,]+)", 1, Message)
              | where isnotempty(CorrelationId)
              | summarize AcceptedTime = min(TimeGenerated) by CorrelationId;
            let sent = AppTraces
              | where TimeGenerated > ago(15m)
              | where Message has "Outbox message sent"
              | extend CorrelationId = extract(@"CorrelationId=([^\s,]+)", 1, Message)
              | where isnotempty(CorrelationId)
              | summarize SentTime = min(TimeGenerated) by CorrelationId;
            accepted
            | join kind=leftouter sent on CorrelationId
            | where (isnull(SentTime) and AcceptedTime < ago(60s)) or datetime_diff('second', SentTime, AcceptedTime) > 60
          '''
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
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

resource functionFailuresAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-function-failures'
  location: location
  tags: tags
  properties: {
    displayName: 'Azure Functions execution failure'
    description: 'Runbook: Query AppRequests (Success==false) and AppExceptions for func-azurelab-dev-cin-5kaayi; check Storage/SQL RBAC.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      workspaceId
    ]
    criteria: {
      allOf: [
        {
          query: '''
            union
              (AppRequests | where AppRoleName == "${functionAppName}" and Success == false),
              (AppExceptions | where AppRoleName == "${functionAppName}")
          '''
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
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}
