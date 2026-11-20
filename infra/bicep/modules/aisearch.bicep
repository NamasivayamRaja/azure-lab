param name string
param location string
param tags object

@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param skuName string = 'basic'

@minValue(1)
@maxValue(3)
param replicaCount int = 1

@minValue(1)
@maxValue(1)
param partitionCount int = 1

@allowed([
  'disabled'
  'free'
  'standard'
])
param semanticSearch string = 'free'

@allowed([
  'enabled'
  'disabled'
])
param publicNetworkAccess string = 'enabled'

@description('Expiry tag for billable lab resources so teardown is auditable.')
param expires string = ''

var effectiveTags = empty(expires) ? tags : union(tags, { expires: expires })

resource searchService 'Microsoft.Search/searchServices@2025-05-01' = {
  name: name
  location: location
  tags: effectiveTags
  sku: {
    name: skuName
  }
  properties: {
    replicaCount: replicaCount
    partitionCount: partitionCount
    hostingMode: 'default'
    publicNetworkAccess: publicNetworkAccess
    // Enforces Entra ID only. authOptions must be omitted when disableLocalAuth is true.
    disableLocalAuth: true
    semanticSearch: semanticSearch
  }
}

output id string = searchService.id
output name string = searchService.name
output endpoint string = 'https://${searchService.name}.search.windows.net'
