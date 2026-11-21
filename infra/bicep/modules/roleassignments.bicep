param storageAccountName string
param serviceBusName string
param apiBlobContainerName string

param apiPrincipalId string
param apiStagingPrincipalId string
param workerPrincipalId string
param functionPrincipalId string
param functionStagingPrincipalId string

@description('Grant the staging slot identity the API roles. false = least privilege (warm-up only hits /health).')
param grantStagingSlotRoles bool = false

@description('true only if the dev/admin endpoints are reachable in Azure (they call Service Bus).')
param apiDevEndpointsEnabled bool = false

param deployOpenAi bool = false
param openAiAccountName string = ''
@description('Optional Entra User object ID granted Cognitive Services OpenAI User for local dev testing (no keys).')
param openAiDevUserObjectId string = ''

param deploySearch bool = false
param searchServiceName string = ''
@description('Optional Entra User object ID granted Search Service Contributor + Search Index Data Contributor for deploy-time index schema & re-index CLI.')
param searchDevUserObjectId string = ''

param deployContentSafety bool = false
param contentSafetyAccountName string = ''
@description('Optional Entra User object ID granted Cognitive Services User for local keyless Prompt Shields evaluation.')
param contentSafetyDevUserObjectId string = ''

param deployDocumentIntelligence bool = false
param docIntelligenceAccountName string = ''
@description('Optional Entra User object ID granted Cognitive Services User for local keyless Document Intelligence evaluation.')
param docIntelligenceDevUserObjectId string = ''

// Built-in role definition IDs. Cannot be validated offline: confirm in cloud week 1
// with: az role definition list --name "<role name>"
var roleIds = {
  sbSender: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  sbReceiver: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  blobDataContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  blobDelegator: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a')
  queueDataContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  openAiUser: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  searchIndexDataContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  searchIndexDataReader: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  searchServiceContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  cognitiveServicesUser: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
}

var apiPrincipalIds = grantStagingSlotRoles ? [apiPrincipalId, apiStagingPrincipalId] : [apiPrincipalId]

// ---------- Existing resources (the narrowest scopes) ----------
resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' existing = {
  parent: storage
  name: 'default'
}

resource apiContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' existing = {
  parent: blobService
  name: apiBlobContainerName
}

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2026-01-01' existing = {
  name: serviceBusName
}

resource redeemQueue 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' existing = {
  parent: sbNamespace
  name: 'redeem-requests'
}

resource notificationsQueue 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' existing = {
  parent: sbNamespace
  name: 'offer-notifications'
}

resource documentProcessedQueue 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' existing = {
  parent: sbNamespace
  name: 'document-processed'
}

resource offerTopic 'Microsoft.ServiceBus/namespaces/topics@2026-01-01' existing = {
  parent: sbNamespace
  name: 'offer-events'
}

resource ledgerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2026-01-01' existing = {
  parent: offerTopic
  name: 'ledger'
}

resource notificationSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2026-01-01' existing = {
  parent: offerTopic
  name: 'notification'
}

// ---------- API: Blob ----------
resource apiBlobDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: {
    name: guid(storage.id, pid, roleIds.blobDelegator)
    scope: storage
    properties: {
      roleDefinitionId: roleIds.blobDelegator
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

resource apiBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: {
    name: guid(apiContainer.id, pid, roleIds.blobDataContributor)
    scope: apiContainer
    properties: {
      roleDefinitionId: roleIds.blobDataContributor
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

// ---------- API: Service Bus (only if the dev/admin endpoints are exposed) ----------
resource apiRedeemReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: if (apiDevEndpointsEnabled) {
    name: guid(redeemQueue.id, pid, roleIds.sbReceiver)
    scope: redeemQueue
    properties: {
      roleDefinitionId: roleIds.sbReceiver
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

resource apiRedeemSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: if (apiDevEndpointsEnabled) {
    name: guid(redeemQueue.id, pid, roleIds.sbSender)
    scope: redeemQueue
    properties: {
      roleDefinitionId: roleIds.sbSender
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

// ---------- Worker: Service Bus ----------
resource workerRedeemSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(redeemQueue.id, workerPrincipalId, roleIds.sbSender)
  scope: redeemQueue
  properties: {
    roleDefinitionId: roleIds.sbSender
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerTopicSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(offerTopic.id, workerPrincipalId, roleIds.sbSender)
  scope: offerTopic
  properties: {
    roleDefinitionId: roleIds.sbSender
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerRedeemReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(redeemQueue.id, workerPrincipalId, roleIds.sbReceiver)
  scope: redeemQueue
  properties: {
    roleDefinitionId: roleIds.sbReceiver
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerLedgerReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(ledgerSubscription.id, workerPrincipalId, roleIds.sbReceiver)
  scope: ledgerSubscription
  properties: {
    roleDefinitionId: roleIds.sbReceiver
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerNotificationReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(notificationSubscription.id, workerPrincipalId, roleIds.sbReceiver)
  scope: notificationSubscription
  properties: {
    roleDefinitionId: roleIds.sbReceiver
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Functions: Service Bus ----------
resource functionNotificationsReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(notificationsQueue.id, functionPrincipalId, roleIds.sbReceiver)
  scope: notificationsQueue
  properties: {
    roleDefinitionId: roleIds.sbReceiver
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionDocumentProcessedSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentProcessedQueue.id, functionPrincipalId, roleIds.sbSender)
  scope: documentProcessedQueue
  properties: {
    roleDefinitionId: roleIds.sbSender
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// DocumentIngestionFunction has [ServiceBusTrigger("document-processed")] and dead-letters with reason "AiDisabled": both need Receive.
resource functionDocumentProcessedReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentProcessedQueue.id, functionPrincipalId, roleIds.sbReceiver)
  scope: documentProcessedQueue
  properties: {
    roleDefinitionId: roleIds.sbReceiver
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Functions: host storage + blob trigger + quarantine ----------
var functionStoragePrincipalIds = [functionPrincipalId, functionStagingPrincipalId]

resource functionBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in functionStoragePrincipalIds: {
    name: guid(storage.id, pid, roleIds.blobDataContributor)
    scope: storage
    properties: {
      roleDefinitionId: roleIds.blobDataContributor
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

resource functionQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in functionStoragePrincipalIds: {
    name: guid(storage.id, pid, roleIds.queueDataContributor)
    scope: storage
    properties: {
      roleDefinitionId: roleIds.queueDataContributor
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

// ---------- Azure OpenAI: Cognitive Services OpenAI User (API, Functions, optional dev user; Worker gets none) ----------
resource openAi 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: deployOpenAi ? openAiAccountName : 'placeholder-openai'
}

// API calls chat + embeddings at query time.
resource apiOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: if (deployOpenAi) {
    name: guid(openAi.id, pid, roleIds.openAiUser)
    scope: openAi
    properties: {
      roleDefinitionId: roleIds.openAiUser
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

// Functions calls embeddings during document ingestion.
resource functionOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployOpenAi) {
  name: guid(openAi.id, functionPrincipalId, roleIds.openAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: roleIds.openAiUser
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Local developer identity for keyless DefaultAzureCredential verification in dev.
resource devUserOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployOpenAi && !empty(openAiDevUserObjectId)) {
  name: guid(openAi.id, openAiDevUserObjectId, roleIds.openAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: roleIds.openAiUser
    principalId: openAiDevUserObjectId
    principalType: 'User'
  }
}

// ---------- Azure AI Search (least-privilege data-plane for runtime; control-plane only for deploy-time tool) ----------
resource searchService 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: deploySearch ? searchServiceName : 'placeholder-search'
}

// API queries the index read-only at /ask time (never mutates documents or index schema).
resource apiSearchIndexReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: if (deploySearch) {
    name: guid(searchService.id, pid, roleIds.searchIndexDataReader)
    scope: searchService
    properties: {
      roleDefinitionId: roleIds.searchIndexDataReader
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

// Functions upserts and deletes chunks during document ingestion (no index schema mutation).
resource functionSearchIndexContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch) {
  name: guid(searchService.id, functionPrincipalId, roleIds.searchIndexDataContributor)
  scope: searchService
  properties: {
    roleDefinitionId: roleIds.searchIndexDataContributor
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Dev user runs the deploy-time index schema + re-index CLI locally (in CI, assign to the deploy UAMI).
resource devUserSearchServiceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch && !empty(searchDevUserObjectId)) {
  name: guid(searchService.id, searchDevUserObjectId, roleIds.searchServiceContributor)
  scope: searchService
  properties: {
    roleDefinitionId: roleIds.searchServiceContributor
    principalId: searchDevUserObjectId
    principalType: 'User'
  }
}

resource devUserSearchIndexContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySearch && !empty(searchDevUserObjectId)) {
  name: guid(searchService.id, searchDevUserObjectId, roleIds.searchIndexDataContributor)
  scope: searchService
  properties: {
    roleDefinitionId: roleIds.searchIndexDataContributor
    principalId: searchDevUserObjectId
    principalType: 'User'
  }
}

// ---------- Azure AI Content Safety: Cognitive Services User (API + optional dev user; Worker & Functions get none) ----------
resource contentSafety 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: deployContentSafety ? contentSafetyAccountName : 'placeholder-contentsafety'
}

// API calls Prompt Shields + text moderation at query time.
resource apiContentSafetyUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pid in apiPrincipalIds: if (deployContentSafety) {
    name: guid(contentSafety.id, pid, roleIds.cognitiveServicesUser)
    scope: contentSafety
    properties: {
      roleDefinitionId: roleIds.cognitiveServicesUser
      principalId: pid
      principalType: 'ServicePrincipal'
    }
  }
]

// Local developer identity for keyless Prompt Shields evals in dev.
resource devUserContentSafetyUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployContentSafety && !empty(contentSafetyDevUserObjectId)) {
  name: guid(contentSafety.id, contentSafetyDevUserObjectId, roleIds.cognitiveServicesUser)
  scope: contentSafety
  properties: {
    roleDefinitionId: roleIds.cognitiveServicesUser
    principalId: contentSafetyDevUserObjectId
    principalType: 'User'
  }
}

// ---------- Azure AI Document Intelligence: Cognitive Services User (Functions + optional dev user; API & Worker get none) ----------
resource docIntelligence 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: deployDocumentIntelligence ? docIntelligenceAccountName : 'placeholder-docintelligence'
}

// Functions calls Document Intelligence during blob/queue document ingestion.
resource functionDocIntelligenceUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployDocumentIntelligence) {
  name: guid(docIntelligence.id, functionPrincipalId, roleIds.cognitiveServicesUser)
  scope: docIntelligence
  properties: {
    roleDefinitionId: roleIds.cognitiveServicesUser
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Local developer identity for keyless Document Intelligence comparison in dev.
resource devUserDocIntelligenceUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployDocumentIntelligence && !empty(docIntelligenceDevUserObjectId)) {
  name: guid(docIntelligence.id, docIntelligenceDevUserObjectId, roleIds.cognitiveServicesUser)
  scope: docIntelligence
  properties: {
    roleDefinitionId: roleIds.cognitiveServicesUser
    principalId: docIntelligenceDevUserObjectId
    principalType: 'User'
  }
}
