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
param alertEmail = readEnvironmentVariable('ALERT_EMAIL', '')


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

// possibleOutboundIpAddresses of asp-azurelab-dev-cin (captured 2026-11-18 from the API app (rg dev2)).
// Re-capture if the plan SKU changes or the plan moves to another stamp. No 0.0.0.0 "Allow Azure services" rule.
param sqlAllowedIps = [
  '135.235.252.23'
  '20.192.98.161'
  '20.193.229.53'
  '20.193.232.42'
  '20.193.239.122'
  '20.193.240.182'
  '20.198.118.232'
  '20.204.10.63'
  '20.204.105.172'
  '20.204.11.162'
  '20.204.110.167'
  '20.204.111.176'
  '20.204.144.238'
  '20.204.148.42'
  '20.204.187.145'
  '20.204.222.100'
  '20.204.222.102'
  '20.204.222.116'
  '20.204.222.124'
  '20.204.222.129'
  '20.204.222.130'
  '20.204.238.157'
  '20.204.238.159'
  '20.204.238.18'
  '20.204.238.251'
  '20.204.239.171'
  '20.204.239.74'
  '20.204.65.82'
  '20.204.68.117'
  '4.187.134.248'
  '4.187.138.65'
  '4.187.153.9'
  '4.187.185.72'
  '4.187.232.178'
  '4.188.68.72'
  '4.224.109.147'
  '4.224.164.142'
  '4.247.240.182'
  '40.80.84.70'
  '40.81.250.84'
  '52.172.249.184'
  '52.172.252.64'
  '52.172.254.209'
]

// ---------- Azure OpenAI (Week 2, Day 1 — Option A: southindia) ----------
param deployOpenAi = true
param openAiLocation = 'southindia'
param openAiRegionShort = 'sin'
param openAiChatCapacity = 10
param openAiEmbeddingCapacity = 10
param openAiDevUserObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')

// ---------- Azure AI Search (Week 2, Day 2/3 — Basic in centralindia; recreated for Day 3) ----------
param deploySearch = true
param searchSkuName = 'basic'
param searchSemanticSearch = 'free'
param searchExpires = '2026-11-21'
param searchDevUserObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')
param aiEnabled = true
param aiSearchIndexName = 'azurelab-docs-v2'
param aiSearchMode = 'HybridSemantic'

// ---------- Azure AI Content Safety (Week 2, Day 3 — S0 in southindia for Prompt Shields) ----------
param deployContentSafety = true
param contentSafetyLocation = 'southindia'
param contentSafetyRegionShort = 'sin'
param contentSafetySkuName = 'S0'
param contentSafetyDevUserObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')

// ---------- Azure AI Document Intelligence (Week 2, Day 4 — S0 in centralindia) ----------
param deployDocumentIntelligence = true
param docIntelligenceSkuName = 'S0'
param docIntelligenceDevUserObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')
