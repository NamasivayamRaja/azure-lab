@description('Globally unique storage account name (3-24 lowercase alphanumeric).')
param name string

param location string
param tags object

@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
])
@description('Redundancy. LRS for dev; ZRS or GRS for prod (set in the .bicepparam).')
param skuName string = 'Standard_LRS'

@minValue(1)
@maxValue(365)
@description('Soft-delete retention for blobs and containers, in days.')
param blobSoftDeleteDays int = 7

@minValue(1)
@description('Days after creation before quarantined files are deleted.')
param quarantineRetentionDays int = 7

var quarantineContainer = 'event-docs-quarantine'

// Names are fixed by the code: API option, blob trigger path, quarantine constant.
var containerNames = [
  'bulk-player-enrollment'
  'event-docs'
  quarantineContainer
]

resource storage 'Microsoft.Storage/storageAccounts@2026-04-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: skuName
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false          // Entra ID (Managed Identity) only
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'       // locked down with Private Endpoint in cloud week 2
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-04-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteDays
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteDays
    }
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-04-01' = [
  for containerName in containerNames: {
    parent: blobService
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2026-04-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          enabled: true
          name: 'delete-quarantine-after-n-days'
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                '${quarantineContainer}/'   // trailing slash: do NOT match event-docs/
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterCreationGreaterThan: quarantineRetentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

output id string = storage.id
output name string = storage.name
output blobServiceUri string = storage.properties.primaryEndpoints.blob
