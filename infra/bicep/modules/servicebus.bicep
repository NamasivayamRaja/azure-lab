param name string
param location string
param tags object

// ---- Contract with the code / Config.json (NOT per-environment) ----
var topicName = 'offer-events'

var queues = [
  { name: 'redeem-requests', lockDuration: 'PT30S', deadLetterOnExpiry: true }
  { name: 'offer-notifications', lockDuration: 'PT1M', deadLetterOnExpiry: false }
  { name: 'document-processed', lockDuration: 'PT1M', deadLetterOnExpiry: false }
]

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2026-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'      // topics require Standard+; Private Endpoint (week 2) needs Premium
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true        // no SAS keys: Entra ID + RBAC only
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled' // restricted in cloud week 2
  }
}

resource queueResources 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' = [
  for q in queues: {
    parent: sbNamespace
    name: q.name
    properties: {
      lockDuration: q.lockDuration
      maxDeliveryCount: 3
      defaultMessageTimeToLive: 'PT1H'
      deadLetteringOnMessageExpiration: q.deadLetterOnExpiry
      requiresDuplicateDetection: false
      requiresSession: false
    }
  }
]

resource topic 'Microsoft.ServiceBus/namespaces/topics@2026-01-01' = {
  parent: sbNamespace
  name: topicName
  properties: {
    defaultMessageTimeToLive: 'PT1H'
    requiresDuplicateDetection: false
  }
}

resource ledgerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2026-01-01' = {
  parent: topic
  name: 'ledger'
  properties: {
    lockDuration: 'PT30S'
    maxDeliveryCount: 3
    deadLetteringOnMessageExpiration: true
    requiresSession: false
  }
}

resource notificationSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2026-01-01' = {
  parent: topic
  name: 'notification'
  properties: {
    lockDuration: 'PT30S'
    maxDeliveryCount: 3
    deadLetteringOnMessageExpiration: true
    requiresSession: false
  }
}

// Azure auto-creates '$Default' (match-all) on every subscription; this overwrites it
// with the same correlation filter Config.json defines. 'ledger' keeps the match-all default.
resource notificationRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2026-01-01' = {
  parent: notificationSubscription
  name: 'offer-redeemed-filter'
  properties: {
    filterType: 'CorrelationFilter'
    correlationFilter: {
      label: 'OfferRedeemed'
    }
  }
}

output id string = sbNamespace.id
output name string = sbNamespace.name
// Value for ServiceBus__FullyQualifiedNamespace. Derived (not hardcoded) to satisfy the no-hardcoded-env-urls rule.
output fullyQualifiedNamespace string = replace(replace(sbNamespace.properties.serviceBusEndpoint, 'https://', ''), ':443/', '')
