param name string
param location string
param tags object

@allowed([
  'F0'
  'S0'
])
param skuName string = 'S0'

@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

resource contentSafety 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: name
  location: location
  tags: tags
  kind: 'ContentSafety'
  sku: {
    name: skuName
  }
  properties: {
    customSubDomainName: name
    disableLocalAuth: true
    publicNetworkAccess: publicNetworkAccess
  }
}

output id string = contentSafety.id
output name string = contentSafety.name
output endpoint string = contentSafety.properties.endpoint
