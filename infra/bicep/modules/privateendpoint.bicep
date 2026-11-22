param name string
param location string
param tags object
param subnetId string
param privateLinkServiceId string
param groupId string
param privateDnsZoneId string
param expires string = ''

var effectiveTags = empty(expires) ? tags : union(tags, { expires: expires })

resource pe 'Microsoft.Network/privateEndpoints@2025-01-01' = {
  name: name
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-conn'
        properties: {
          privateLinkServiceId: privateLinkServiceId
          groupIds: [
            groupId
          ]
        }
      }
    ]
  }
}

resource dnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-01-01' = {
  parent: pe
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: replace(groupId, '_', '-')
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output id string = pe.id
output name string = pe.name
