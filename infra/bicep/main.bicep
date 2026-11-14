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
}

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
module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: names.storage
    location: location
    tags: tags
    skuName: storageSku
    blobSoftDeleteDays: blobSoftDeleteDays
    quarantineRetentionDays: quarantineRetentionDays
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
    serviceBusFqdn: serviceBus.outputs.fullyQualifiedNamespace
    blobServiceUri: storage.outputs.blobServiceUri
    apiBlobContainerName: apiBlobContainerName
    sqlServerFqdn: deploySql ? sqlServerFqdn : ''
    sqlDatabaseName: names.sqlDatabase
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
    grantStagingSlotRoles: grantStagingSlotRoles
    apiDevEndpointsEnabled: apiDevEndpointsEnabled
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
