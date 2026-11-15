using '../main.bicep'

param envName = 'dev'
param regionShort = 'cin'
// Set before compiling, e.g. in PowerShell: $env:AZURELAB_OWNER = 'your-alias'
param owner = readEnvironmentVariable('AZURELAB_OWNER', 'unassigned')

param storageSku = 'Standard_LRS'
param blobSoftDeleteDays = 7
param quarantineRetentionDays = 7

param appServicePlanSku = 'P0v3'

param logRetentionDays = 30
param logDailyQuotaGb = 1

param deployKeyVault = false
param keyVaultSoftDeleteDays = 7
param keyVaultPurgeProtection = false

param apiBlobContainerName = 'bulk-player-enrollment'
param grantStagingSlotRoles = false
param apiDevEndpointsEnabled = false

// Set via environment / GitHub repo variables (public repo: no tenant or app IDs committed)
param azureAdTenantId = readEnvironmentVariable('AZURE_TENANT_ID', '')
param azureAdClientId = readEnvironmentVariable('AZURELAB_API_CLIENT_ID', '')


// ---------- Azure SQL (Step 5) ----------
param deploySql = true
// Entra admin: set before compiling, e.g. $env:AZURELAB_SQL_ADMIN_LOGIN = '<your UPN>'; $env:AZURELAB_SQL_ADMIN_OID = '<your object id>'
param sqlAdminLogin = readEnvironmentVariable('AZURELAB_SQL_ADMIN_LOGIN', '')
param sqlAdminObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')
param sqlAdminPrincipalType = 'User'

// Free offer: General Purpose serverless. BillOverUsage keeps the DB online after the monthly free vCore-seconds run out.
param sqlSkuName = 'GP_S_Gen5_1'
param sqlServerless = true
param sqlMinCapacity = '0.5'
param sqlAutoPauseDelayMinutes = 15
param sqlUseFreeLimit = true
param sqlFreeLimitExhaustionBehavior = 'BillOverUsage'
param sqlBackupStorageRedundancy = 'Local'
param sqlZoneRedundant = false

// possibleOutboundIpAddresses of asp-azurelab-dev-cin (captured 2026-11-12 from the API app).
// Re-capture if the plan SKU changes or the plan moves to another stamp. No 0.0.0.0 "Allow Azure services" rule.
param sqlAllowedIps = [
  '135.13.177.151'
  '135.13.180.237'
  '20.192.171.20'
  '20.204.211.52'
  '20.204.214.217'
  '20.204.214.68'
  '20.219.174.118'
  '20.235.194.47'
  '20.244.77.107'
  '4.187.190.117'
  '4.187.192.56'
  '4.187.192.83'
  '4.187.202.192'
  '4.187.204.15'
  '4.187.214.53'
  '4.188.115.70'
  '4.188.89.53'
  '4.188.89.72'
  '4.224.115.37'
  '4.224.115.87'
  '4.224.138.26'
  '4.224.139.74'
  '4.224.71.1'
  '4.224.71.224'
  '4.247.208.118'
  '4.247.208.154'
  '4.247.214.195'
  '4.247.215.225'
  '4.247.236.85'
  '98.70.233.34'
  '98.70.238.182'
]
