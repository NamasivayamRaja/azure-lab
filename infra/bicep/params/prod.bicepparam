using '../main.bicep'

param envName = 'prod'
param regionShort = 'cin'
param owner = readEnvironmentVariable('AZURELAB_OWNER', 'unassigned')

param storageSku = 'Standard_ZRS'
param blobSoftDeleteDays = 30
param quarantineRetentionDays = 30

param appServicePlanSku = 'P1v3'

param logRetentionDays = 90
param logDailyQuotaGb = -1 // no cap: do not lose logs during an incident; use a budget alert instead

param deployKeyVault = false // flips to true when the project has a real secret
param keyVaultSoftDeleteDays = 90
param keyVaultPurgeProtection = true

param apiBlobContainerName = 'bulk-player-enrollment'
param grantStagingSlotRoles = false
param apiDevEndpointsEnabled = false


// ---------- Azure SQL (Step 5) ----------
// Not deployed in cloud week 1. Values below are the intended shape; validate SKU and zone support before any prod deployment.
param deploySql = true
param sqlAdminLogin = readEnvironmentVariable('AZURELAB_SQL_ADMIN_LOGIN', '') // prod: an Entra GROUP display name
param sqlAdminObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')
param sqlAdminPrincipalType = 'Group'

param sqlSkuName = 'GP_Gen5_2'
param sqlServerless = false
param sqlMinCapacity = '0.5' // ignored when sqlServerless = false
param sqlAutoPauseDelayMinutes = -1
param sqlUseFreeLimit = false
param sqlFreeLimitExhaustionBehavior = 'BillOverUsage'
param sqlBackupStorageRedundancy = 'Zone'
param sqlZoneRedundant = true

// To be filled from possibleOutboundIpAddresses of the prod plan (P1v3). Empty = no app can reach SQL.
param sqlAllowedIps = []
