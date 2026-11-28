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

// Cleared on Week 2, Day 5 (closes rebuild gap 1): apps reach SQL over Private Endpoint (pe-sql-azurelab-dev-cin),
// and publicNetworkAccess is Disabled. CI/admin migrations use Option A (VNet container/runner in snet-aci).
param sqlAllowedIps = []

// ---------- Azure OpenAI (Week 2, Day 1 — Option A: southindia) ----------
param deployOpenAi = true
param openAiLocation = 'southindia'
param openAiRegionShort = 'sin'
param openAiChatCapacity = 10
param openAiEmbeddingCapacity = 10
param openAiDevUserObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')

// ---------- Azure AI Search (Week 2, Day 2/3 — deleted at Day 6 EOD; set true to recreate) ----------
param deploySearch = false
param searchSkuName = 'basic'
param searchSemanticSearch = 'free'
param searchExpires = ''
param searchDevUserObjectId = readEnvironmentVariable('AZURELAB_SQL_ADMIN_OID', '')
param aiEnabled = false
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

// ---------- Networking & Private Endpoints (Week 2, Day 5 — PEs deleted at Day 6 EOD; VNet kept at $0) ----------
param deployNetwork = true
param vnetRouteAllEnabled = true
param privateEndpointExpires = ''
param deployStoragePrivateEndpoints = false
param storagePublicNetworkAccess = 'Enabled'
param deploySqlPrivateEndpoint = false
param sqlPublicNetworkAccess = 'Enabled'
param deployOpenAiPrivateEndpoint = false
param openAiPublicNetworkAccess = 'Enabled'
param deploySearchPrivateEndpoint = false
param searchPublicNetworkAccess = 'enabled'

// ---------- API Management (Week 2, Day 6 — deleted at EOD; set true to recreate) ----------
param deployApim = false
param apimSkuName = 'StandardV2'
param apimPublisherEmail = readEnvironmentVariable('ALERT_EMAIL', 'admin@azurelab.local')
param apimExpires = ''
param lockApiToApim = false
param useApimAiGateway = false
