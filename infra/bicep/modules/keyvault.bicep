param name string
param location string
param tags object
param workspaceId string

@description('Soft-delete retention in days (7-90). Soft delete itself cannot be disabled.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionDays int = 7

@description('false for dev (deploy/delete labs reuse the name); true for prod. Cannot be turned off once enabled.')
param enablePurgeProtection bool = false

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true // RBAC only; no access policies
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionDays
    // Bicep rule: a false value must be omitted, not sent, or ARM rejects it.
    enablePurgeProtection: enablePurgeProtection ? true : null
    enabledForDeployment: false
    enabledForTemplateDeployment: false
    enabledForDiskEncryption: false
    publicNetworkAccess: 'Enabled' // restricted with a Private Endpoint in cloud week 2
  }
}

#disable-next-line use-recent-api-versions
resource diag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: vault
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        categoryGroup: 'audit' // AuditEvent: who read/wrote which secret
        enabled: true
      }
    ]
  }
}

output id string = vault.id
output name string = vault.name
output vaultUri string = vault.properties.vaultUri
