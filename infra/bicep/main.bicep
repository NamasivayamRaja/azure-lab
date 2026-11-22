targetScope = 'resourceGroup'

// ---------- Parameters (the only things that differ per environment) ----------
@description('Environment. Drives naming and, later, SKUs. Supplied by the .bicepparam file.')
@allowed([
  'dev'
  'prod'
])
param envName string

@description('Workload short name. Part of every resource name. Max 12 so storage names stay <= 24 chars.')
@minLength(3)
@maxLength(12)
param workload string = 'azurelab'

@description('Azure region. Defaults to the resource group region; never hardcode locations in modules.')
param location string = resourceGroup().location

@description('Short region code used in names (cin = Central India).')
param regionShort string = 'cin'

@description('Owner tag: who to ask about these resources and who pays.')
param owner string

@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
])
param storageSku string = 'Standard_LRS'

param blobSoftDeleteDays int = 7
param quarantineRetentionDays int = 7

@description('Container behind BlobStorage__ContainerName. See the container decision in Step 7.')
param apiBlobContainerName string = 'bulk-player-enrollment'

param grantStagingSlotRoles bool = false
param apiDevEndpointsEnabled bool = false

param azureAdTenantId string = ''
param azureAdClientId string = ''
param alertEmail string = ''

@allowed([
  'S1'
  'S2'
  'P0v3'
  'P1v3'
])
param appServicePlanSku string = 'S1'

@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

@description('-1 = no cap')
param logDailyQuotaGb int = 1

@description('Provision Key Vault only when the project has a real secret.')
param deployKeyVault bool = false

@minValue(7)
@maxValue(90)
param keyVaultSoftDeleteDays int = 7

param keyVaultPurgeProtection bool = false

// ---------- Azure SQL (Step 5) ----------
@description('Provision Azure SQL (logical server + database). When false, no ConnectionStrings__* app settings are written.')
param deploySql bool = true

@description('Entra admin login for the SQL server (UPN for a user, display name for a group). From the .bicepparam / environment, not hardcoded.')
param sqlAdminLogin string = ''

@description('Entra admin object ID (GUID).')
param sqlAdminObjectId string = ''

@allowed([
  'User'
  'Group'
  'Application'
])
param sqlAdminPrincipalType string = 'User'

param sqlSkuName string = 'GP_S_Gen5_1'
param sqlServerless bool = true
param sqlMinCapacity string = '0.5'
param sqlAutoPauseDelayMinutes int = 15
param sqlUseFreeLimit bool = false

@allowed([
  'AutoPause'
  'BillOverUsage'
])
param sqlFreeLimitExhaustionBehavior string = 'BillOverUsage'

@allowed([
  'Local'
  'Zone'
  'Geo'
  'GeoZone'
])
param sqlBackupStorageRedundancy string = 'Local'

param sqlZoneRedundant bool = false

@description('Single-IP firewall rules (the plan outbound IPs). Never the 0.0.0.0 Allow-Azure-services rule.')
param sqlAllowedIps array = []

// ---------- Azure OpenAI (Week 2, Day 1) ----------
param deployOpenAi bool = false
@description('Azure OpenAI region. Defaults to RG location; override in .bicepparam when primary region lacks pay-as-you-go quota.')
param openAiLocation string = location
param openAiRegionShort string = regionShort
param openAiChatCapacity int = 10
param openAiEmbeddingCapacity int = 10
@description('Optional Entra User object ID granted Cognitive Services OpenAI User for keyless local dev testing.')
param openAiDevUserObjectId string = ''

// ---------- Azure AI Search (Week 2, Day 2) ----------
param deploySearch bool = false
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param searchSkuName string = 'basic'
@allowed([
  'disabled'
  'free'
  'standard'
])
param searchSemanticSearch string = 'free'
param searchExpires string = ''
@description('Optional Entra User object ID granted Search Service Contributor + Search Index Data Contributor for deploy-time index/re-index CLI.')
param searchDevUserObjectId string = ''
@description('Enable AI/RAG in Azure API and Functions (requires both deployOpenAi and deploySearch).')
param aiEnabled bool = false
param aiSearchIndexName string = 'azurelab-docs'
param aiSearchMode string = 'HybridSemantic'

// ---------- Azure AI Content Safety (Week 2, Day 3 — southindia for Prompt Shields support) ----------
param deployContentSafety bool = false
param contentSafetyLocation string = openAiLocation
param contentSafetyRegionShort string = openAiRegionShort
@allowed([
  'F0'
  'S0'
])
param contentSafetySkuName string = 'S0'
@description('Optional Entra User object ID granted Cognitive Services User for local keyless Prompt Shields evaluation.')
param contentSafetyDevUserObjectId string = ''

// ---------- Azure AI Document Intelligence (Week 2, Day 4 — centralindia) ----------
param deployDocumentIntelligence bool = false
@allowed([
  'F0'
  'S0'
])
param docIntelligenceSkuName string = 'S0'
@description('Optional Entra User object ID granted Cognitive Services User for local keyless Document Intelligence evaluation.')
param docIntelligenceDevUserObjectId string = ''

// ---------- Networking & Private Endpoints (Week 2, Day 5) ----------
param deployNetwork bool = false
param vnetRouteAllEnabled bool = true
param privateEndpointExpires string = ''
param deployStoragePrivateEndpoints bool = false
@allowed([
  'Enabled'
  'Disabled'
])
param storagePublicNetworkAccess string = 'Enabled'
param deploySqlPrivateEndpoint bool = false
@allowed([
  'Enabled'
  'Disabled'
])
param sqlPublicNetworkAccess string = 'Enabled'
param deployOpenAiPrivateEndpoint bool = false
@allowed([
  'Enabled'
  'Disabled'
])
param openAiPublicNetworkAccess string = 'Enabled'
param deploySearchPrivateEndpoint bool = false
@allowed([
  'enabled'
  'disabled'
])
param searchPublicNetworkAccess string = 'enabled'

// ---------- Naming convention ----------
// Pattern: <type-abbrev>-<workload>-<component>-<env>-<region>[-<suffix>]
// Suffix = 6 chars derived from the RG id: stable across redeploys, unique across tenants.
// Only GLOBALLY-unique names (storage, SB namespace, web apps, KV) get the suffix.
var suffix = take(uniqueString(resourceGroup().id), 6)
var baseName = '${workload}-${envName}-${regionShort}'

var names = {
  storage: take('st${workload}${envName}${suffix}', 24)        // 3-24, lowercase alphanumeric only
  serviceBus: 'sb-${baseName}-${suffix}'                       // 6-50
  appPlan: 'asp-${baseName}'
  apiApp: 'app-${workload}-api-${envName}-${regionShort}-${suffix}'
  workerApp: 'app-${workload}-worker-${envName}-${regionShort}-${suffix}'
  functionApp: 'func-${baseName}-${suffix}'
  logAnalytics: 'log-${baseName}'
  appInsights: 'appi-${baseName}'
  keyVault: take('kv-${workload}-${envName}-${suffix}', 24)    // 3-24
  sqlServer: 'sql-${baseName}-${suffix}'                       // 1-63, lowercase, globally unique
  sqlDatabase: 'sqldb-${baseName}'
  openAi: 'oai-${workload}-${envName}-${openAiRegionShort}-${suffix}' // 2-64, globally unique customSubDomainName
  aiSearch: 'srch-${baseName}-${suffix}'                              // 2-60, lowercase/digits/hyphens, globally unique
  contentSafety: 'cs-${workload}-${envName}-${contentSafetyRegionShort}-${suffix}' // 2-64, globally unique customSubDomainName
  docIntelligence: 'di-${baseName}-${suffix}'                                      // 2-64, globally unique customSubDomainName
  vnet: 'vnet-${baseName}'
}

var storageBlobDnsZone = 'privatelink.blob.${environment().suffixes.storage}'
var storageQueueDnsZone = 'privatelink.queue.${environment().suffixes.storage}'
var storageTableDnsZone = 'privatelink.table.${environment().suffixes.storage}'
var sqlPrivateDnsZone = 'privatelink${environment().suffixes.sqlServerHostname}'
// environment().suffixes does not expose an Azure OpenAI DNS suffix.
#disable-next-line no-hardcoded-env-urls
var openAiPrivateDnsZone = 'privatelink.openai.azure.com'
// environment().suffixes does not expose an Azure AI Search DNS suffix.
#disable-next-line no-hardcoded-env-urls
var searchPrivateDnsZone = 'privatelink.search.windows.net'
var storageDnsZones = deployStoragePrivateEndpoints
  ? [storageBlobDnsZone, storageQueueDnsZone, storageTableDnsZone]
  : []
var sqlDnsZones = deploySql && deploySqlPrivateEndpoint ? [sqlPrivateDnsZone] : []
var openAiDnsZones = deployOpenAi && deployOpenAiPrivateEndpoint ? [openAiPrivateDnsZone] : []
var searchDnsZones = deploySearch && deploySearchPrivateEndpoint ? [searchPrivateDnsZone] : []
var privateDnsZoneNames = concat(storageDnsZones, sqlDnsZones, openAiDnsZones, searchDnsZones)

// Deterministic from the server name, so the apps can be wired without depending on the sql module
// (the firewall rules read the apps' outbound IPs from a parameter, so there is no dependency cycle).
var sqlServerFqdn = '${names.sqlServer}${environment().suffixes.sqlServerHostname}'

// ---------- Tagging convention (applied to every resource in later modules) ----------
var tags = {
  workload: workload
  environment: envName
  owner: owner
  managedBy: 'bicep'
}

// Modules
module network 'modules/network.bicep' = if (deployNetwork) {
  name: 'network'
  params: {
    vnetName: names.vnet
    location: location
    tags: tags
    privateDnsZoneNames: privateDnsZoneNames
    expires: privateEndpointExpires
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: names.storage
    location: location
    tags: tags
    skuName: storageSku
    blobSoftDeleteDays: blobSoftDeleteDays
    quarantineRetentionDays: quarantineRetentionDays
    publicNetworkAccess: storagePublicNetworkAccess
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    name: names.serviceBus
    location: location
    tags: tags
  }
}

module appService 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    planName: names.appPlan
    apiAppName: names.apiApp
    workerAppName: names.workerApp
    location: location
    tags: tags
    planSkuName: appServicePlanSku
    serviceBusId: serviceBus.outputs.id
    serviceBusFqdn: serviceBus.outputs.fullyQualifiedNamespace
    blobServiceUri: storage.outputs.blobServiceUri
    apiBlobContainerName: apiBlobContainerName
    sqlServerFqdn: deploySql ? sqlServerFqdn : ''
    sqlDatabaseName: names.sqlDatabase
    azureAdTenantId: azureAdTenantId
    azureAdClientId: azureAdClientId
    appInsightsName: monitoring.outputs.appInsightsName
    aiEnabled: aiEnabled
    openAiEndpoint: openAi.?outputs.endpoint ?? ''
    aiSearchEndpoint: aiSearch.?outputs.endpoint ?? ''
    aiSearchIndexName: aiSearchIndexName
    aiSearchMode: aiSearchMode
    contentSafetyEnabled: deployContentSafety
    contentSafetyEndpoint: contentSafety.?outputs.endpoint ?? ''
    vnetIntegrationSubnetId: network.?outputs.appsvcIntSubnetId ?? ''
    vnetRouteAllEnabled: vnetRouteAllEnabled
  }
}

module sql 'modules/sql.bicep' = if (deploySql) {
  name: 'sql'
  params: {
    serverName: names.sqlServer
    databaseName: names.sqlDatabase
    location: location
    tags: tags
    adminLogin: sqlAdminLogin
    adminObjectId: sqlAdminObjectId
    adminPrincipalType: sqlAdminPrincipalType
    skuName: sqlSkuName
    serverless: sqlServerless
    minCapacity: sqlMinCapacity
    autoPauseDelayMinutes: sqlAutoPauseDelayMinutes
    useFreeLimit: sqlUseFreeLimit
    freeLimitExhaustionBehavior: sqlFreeLimitExhaustionBehavior
    backupStorageRedundancy: sqlBackupStorageRedundancy
    zoneRedundant: sqlZoneRedundant
    allowedIps: sqlAllowedIps
    publicNetworkAccess: sqlPublicNetworkAccess
  }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    logAnalyticsName: names.logAnalytics
    appInsightsName: names.appInsights
    location: location
    tags: tags
    retentionDays: logRetentionDays
    dailyQuotaGb: logDailyQuotaGb
  }
}

module functionApp 'modules/functionapp.bicep' = {
  name: 'functionapp'
  params: {
    name: names.functionApp
    location: location
    tags: tags
    planId: appService.outputs.planId
    storageAccountName: storage.outputs.name
    serviceBusFqdn: serviceBus.outputs.fullyQualifiedNamespace
    blobServiceUri: storage.outputs.blobServiceUri
    appInsightsName: monitoring.outputs.appInsightsName
    sqlServerFqdn: deploySql ? sqlServerFqdn : ''
    sqlDatabaseName: names.sqlDatabase
    aiEnabled: aiEnabled
    openAiEndpoint: openAi.?outputs.endpoint ?? ''
    aiSearchEndpoint: aiSearch.?outputs.endpoint ?? ''
    aiSearchIndexName: aiSearchIndexName
    aiSearchMode: aiSearchMode
    docIntelligenceEndpoint: docIntelligence.?outputs.endpoint ?? ''
    vnetIntegrationSubnetId: network.?outputs.appsvcIntSubnetId ?? ''
    vnetRouteAllEnabled: vnetRouteAllEnabled
  }
}

module openAi 'modules/openai.bicep' = if (deployOpenAi) {
  name: 'openai'
  params: {
    name: names.openAi
    location: openAiLocation
    tags: tags
    chatCapacity: openAiChatCapacity
    embeddingCapacity: openAiEmbeddingCapacity
    publicNetworkAccess: openAiPublicNetworkAccess
  }
}

module aiSearch 'modules/aisearch.bicep' = if (deploySearch) {
  name: 'aisearch'
  params: {
    name: names.aiSearch
    location: location
    tags: tags
    skuName: searchSkuName
    semanticSearch: searchSemanticSearch
    publicNetworkAccess: searchPublicNetworkAccess
    expires: searchExpires
  }
}

module contentSafety 'modules/contentsafety.bicep' = if (deployContentSafety) {
  name: 'contentsafety'
  params: {
    name: names.contentSafety
    location: contentSafetyLocation
    tags: tags
    skuName: contentSafetySkuName
  }
}

module docIntelligence 'modules/docintelligence.bicep' = if (deployDocumentIntelligence) {
  name: 'docintelligence'
  params: {
    name: names.docIntelligence
    location: location
    tags: tags
    skuName: docIntelligenceSkuName
  }
}

module roleAssignments 'modules/roleassignments.bicep' = {
  name: 'roleassignments'
  params: {
    storageAccountName: storage.outputs.name
    serviceBusName: serviceBus.outputs.name
    apiBlobContainerName: apiBlobContainerName
    apiPrincipalId: appService.outputs.apiPrincipalId
    apiStagingPrincipalId: appService.outputs.apiStagingPrincipalId
    workerPrincipalId: appService.outputs.workerPrincipalId
    functionPrincipalId: functionApp.outputs.principalId
    functionStagingPrincipalId: functionApp.outputs.stagingPrincipalId
    grantStagingSlotRoles: grantStagingSlotRoles
    apiDevEndpointsEnabled: apiDevEndpointsEnabled
    deployOpenAi: deployOpenAi
    openAiAccountName: openAi.?outputs.name ?? ''
    openAiDevUserObjectId: openAiDevUserObjectId
    deploySearch: deploySearch
    searchServiceName: aiSearch.?outputs.name ?? ''
    searchDevUserObjectId: searchDevUserObjectId
    deployContentSafety: deployContentSafety
    contentSafetyAccountName: contentSafety.?outputs.name ?? ''
    contentSafetyDevUserObjectId: contentSafetyDevUserObjectId
    deployDocumentIntelligence: deployDocumentIntelligence
    docIntelligenceAccountName: docIntelligence.?outputs.name ?? ''
    docIntelligenceDevUserObjectId: docIntelligenceDevUserObjectId
  }
}

module diagnostics 'modules/diagnostics.bicep' = {
  name: 'diagnostics'
  params: {
    workspaceId: monitoring.outputs.workspaceId
    storageAccountName: storage.outputs.name
    serviceBusName: serviceBus.outputs.name
    apiAppName: names.apiApp
    workerAppName: names.workerApp
    functionAppName: names.functionApp
  }
  dependsOn: [
    appService
    functionApp
  ]
}

module keyVault 'modules/keyvault.bicep' = if (deployKeyVault) {
  name: 'keyvault'
  params: {
    name: names.keyVault
    location: location
    tags: tags
    workspaceId: monitoring.outputs.workspaceId
    softDeleteRetentionDays: keyVaultSoftDeleteDays
    enablePurgeProtection: keyVaultPurgeProtection
  }
}

module alerts 'modules/alerts.bicep' = {
  name: 'alerts'
  params: {
    location: location
    tags: tags
    alertEmail: alertEmail
    serviceBusName: serviceBus.outputs.name
    apiAppName: names.apiApp
    apiHostName: appService.outputs.apiDefaultHostName
    functionAppName: names.functionApp
    appInsightsName: monitoring.outputs.appInsightsName
    workspaceId: monitoring.outputs.workspaceId
    deployOpenAi: deployOpenAi
    openAiAccountName: openAi.?outputs.name ?? ''
  }
  dependsOn: [
    appService
    functionApp
  ]
}

module peStorageBlob 'modules/privateendpoint.bicep' = if (deployNetwork && deployStoragePrivateEndpoints) {
  name: 'pe-storage-blob'
  params: {
    name: 'pe-st-${baseName}-blob'
    location: location
    tags: tags
    subnetId: network!.outputs.peSubnetId
    privateLinkServiceId: storage.outputs.id
    groupId: 'blob'
    privateDnsZoneId: resourceId('Microsoft.Network/privateDnsZones', storageBlobDnsZone)
    expires: privateEndpointExpires
  }
}

module peStorageQueue 'modules/privateendpoint.bicep' = if (deployNetwork && deployStoragePrivateEndpoints) {
  name: 'pe-storage-queue'
  params: {
    name: 'pe-st-${baseName}-queue'
    location: location
    tags: tags
    subnetId: network!.outputs.peSubnetId
    privateLinkServiceId: storage.outputs.id
    groupId: 'queue'
    privateDnsZoneId: resourceId('Microsoft.Network/privateDnsZones', storageQueueDnsZone)
    expires: privateEndpointExpires
  }
}

module peStorageTable 'modules/privateendpoint.bicep' = if (deployNetwork && deployStoragePrivateEndpoints) {
  name: 'pe-storage-table'
  params: {
    name: 'pe-st-${baseName}-table'
    location: location
    tags: tags
    subnetId: network!.outputs.peSubnetId
    privateLinkServiceId: storage.outputs.id
    groupId: 'table'
    privateDnsZoneId: resourceId('Microsoft.Network/privateDnsZones', storageTableDnsZone)
    expires: privateEndpointExpires
  }
}

module peSql 'modules/privateendpoint.bicep' = if (deployNetwork && deploySql && deploySqlPrivateEndpoint) {
  name: 'pe-sql'
  params: {
    name: 'pe-sql-${baseName}'
    location: location
    tags: tags
    subnetId: network!.outputs.peSubnetId
    privateLinkServiceId: sql!.outputs.id
    groupId: 'sqlServer'
    privateDnsZoneId: resourceId('Microsoft.Network/privateDnsZones', sqlPrivateDnsZone)
    expires: privateEndpointExpires
  }
}

// Cross-region PE: placed in centralindia (VNet region) targeting the OpenAI account in southindia.
module peOpenAi 'modules/privateendpoint.bicep' = if (deployNetwork && deployOpenAi && deployOpenAiPrivateEndpoint) {
  name: 'pe-openai'
  params: {
    name: 'pe-oai-${baseName}'
    location: location
    tags: tags
    subnetId: network!.outputs.peSubnetId
    privateLinkServiceId: openAi!.outputs.id
    groupId: 'account'
    privateDnsZoneId: resourceId('Microsoft.Network/privateDnsZones', openAiPrivateDnsZone)
    expires: privateEndpointExpires
  }
}

module peSearch 'modules/privateendpoint.bicep' = if (deployNetwork && deploySearch && deploySearchPrivateEndpoint) {
  name: 'pe-search'
  params: {
    name: 'pe-srch-${baseName}'
    location: location
    tags: tags
    subnetId: network!.outputs.peSubnetId
    privateLinkServiceId: aiSearch!.outputs.id
    groupId: 'searchService'
    privateDnsZoneId: resourceId('Microsoft.Network/privateDnsZones', searchPrivateDnsZone)
    expires: privateEndpointExpires
  }
}

output resourceNames object = names
output appliedTags object = tags
output storageBlobServiceUri string = storage.outputs.blobServiceUri
output serviceBusFqdn string = serviceBus.outputs.fullyQualifiedNamespace
output apiHostName string = appService.outputs.apiDefaultHostName
output workerHostName string = appService.outputs.workerDefaultHostName
output functionHostName string = functionApp.outputs.defaultHostName
output keyVaultUri string = keyVault.?outputs.vaultUri ?? ''
output sqlServerFqdn string = deploySql ? sqlServerFqdn : ''
output sqlDatabaseName string = deploySql ? names.sqlDatabase : ''
output openAiName string = openAi.?outputs.name ?? ''
output openAiEndpoint string = openAi.?outputs.endpoint ?? ''
output openAiChatDeployment string = openAi.?outputs.chatDeploymentName ?? ''
output openAiEmbeddingDeployment string = openAi.?outputs.embeddingDeploymentName ?? ''
output aiSearchName string = aiSearch.?outputs.name ?? ''
output aiSearchEndpoint string = aiSearch.?outputs.endpoint ?? ''
output contentSafetyName string = contentSafety.?outputs.name ?? ''
output contentSafetyEndpoint string = contentSafety.?outputs.endpoint ?? ''
output docIntelligenceName string = docIntelligence.?outputs.name ?? ''
output docIntelligenceEndpoint string = docIntelligence.?outputs.endpoint ?? ''
output vnetName string = network.?outputs.vnetName ?? ''
