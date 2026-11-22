@description('Globally unique logical server name (lowercase letters, digits, hyphens; max 63).')
param serverName string

param databaseName string
param location string
param tags object

@description('Entra admin login: UPN for a user, display name for a group/app. Comes from the .bicepparam (environment variable), never hardcoded.')
param adminLogin string

@minLength(36)
@maxLength(36)
@description('Entra admin object ID (GUID) of the user/group/app named in adminLogin.')
param adminObjectId string

@allowed([
  'User'
  'Group'
  'Application'
])
@description('dev: User (lab). prod: Group (no personal admin).')
param adminPrincipalType string = 'User'

param tenantId string = subscription().tenantId

@description('Database SKU name. Serverless GP example: GP_S_Gen5_1. Provisioned GP example: GP_Gen5_2.')
param skuName string = 'GP_S_Gen5_1'

param skuTier string = 'GeneralPurpose'

@description('true = serverless (min vCores + auto-pause apply). false = provisioned.')
param serverless bool = true

@description('Serverless minimum vCores, as a string because ARM needs a decimal (e.g. 0.5).')
param minCapacity string = '0.5'

@description('Serverless auto-pause delay in minutes. General Purpose minimum is 15; -1 disables auto-pause.')
param autoPauseDelayMinutes int = 15

@description('32 GB. Matches the free-offer data allowance.')
param maxSizeBytes int = 34359738368

@allowed([
  'Local'
  'Zone'
  'Geo'
  'GeoZone'
])
@description('Backup redundancy. dev: Local (cheapest). prod: Zone or Geo.')
param backupStorageRedundancy string = 'Local'

param zoneRedundant bool = false

@description('Use the Azure SQL Database free offer (serverless General Purpose only).')
param useFreeLimit bool = false

@allowed([
  'AutoPause'
  'BillOverUsage'
])
@description('What happens when the monthly free vCore-seconds run out. BillOverUsage keeps the DB online and bills the overage.')
param freeLimitExhaustionBehavior string = 'BillOverUsage'

@description('Public IPv4 addresses allowed through the server firewall (single-IP rules). NOT the 0.0.0.0 "Allow Azure services" rule.')
param allowedIps array = []

@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

resource server 'Microsoft.Sql/servers@2024-05-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess // restricted with a Private Endpoint in cloud week 2
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true // no SQL logins, no passwords
      login: adminLogin
      sid: adminObjectId
      tenantId: tenantId
      principalType: adminPrincipalType
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2024-05-01-preview' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    maxSizeBytes: maxSizeBytes
    requestedBackupStorageRedundancy: backupStorageRedundancy
    zoneRedundant: zoneRedundant
    // Bicep rule: properties that do not apply must be omitted (null), not sent.
    minCapacity: serverless ? json(minCapacity) : null
    autoPauseDelay: serverless ? autoPauseDelayMinutes : null
    useFreeLimit: useFreeLimit ? true : null
    freeLimitExhaustionBehavior: useFreeLimit ? freeLimitExhaustionBehavior : null
  }
}

// One single-IP rule per allowed address. Sequential: parallel writes to one server's firewall can conflict.
@batchSize(1)
resource firewallRules 'Microsoft.Sql/servers/firewallRules@2024-05-01-preview' = [
  for (ip, i) in allowedIps: {
    parent: server
    name: 'app-outbound-${i}'
    properties: {
      startIpAddress: ip
      endIpAddress: ip
    }
  }
]

output id string = server.id
output name string = server.name
output databaseName string = database.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
