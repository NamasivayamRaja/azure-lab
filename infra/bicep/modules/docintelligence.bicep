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

resource docIntelligence 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: name
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: {
    name: skuName
  }
  properties: {
    customSubDomainName: name
    disableLocalAuth: true
    publicNetworkAccess: publicNetworkAccess
  }
}

output id string = docIntelligence.id
output name string = docIntelligence.name
output endpoint string = docIntelligence.properties.endpoint
