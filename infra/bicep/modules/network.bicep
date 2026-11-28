param vnetName string
param location string
param tags object

param vnetAddressPrefix string = '10.10.0.0/22'
param appsvcIntSubnetPrefix string = '10.10.0.0/26'
param peSubnetPrefix string = '10.10.1.0/25'
param aciSubnetPrefix string = '10.10.1.128/25'
param apimIntSubnetPrefix string = '10.10.2.0/26'
param privateDnsZoneNames array = []
param expires string = ''

var effectiveTags = empty(expires) ? tags : union(tags, { expires: expires })

resource nsgAppsvcInt 'Microsoft.Network/networkSecurityGroups@2025-01-01' = {
  name: 'nsg-snet-appsvc-int'
  location: location
  tags: effectiveTags
  properties: {
    securityRules: [
      {
        name: 'allow-pe-https'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: appsvcIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '443'
        }
      }
      {
        name: 'allow-pe-sql'
        properties: {
          priority: 110
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: appsvcIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '1433'
        }
      }
    ]
  }
}

resource nsgApimInt 'Microsoft.Network/networkSecurityGroups@2025-01-01' = {
  name: 'nsg-snet-apim-int'
  location: location
  tags: effectiveTags
  properties: {
    securityRules: [
      {
        name: 'allow-pe-https'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: apimIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '443'
        }
      }
      {
        name: 'allow-keyvault-https'
        properties: {
          priority: 110
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: apimIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: 'AzureKeyVault'
          destinationPortRange: '443'
        }
      }
    ]
  }
}

resource nsgPe 'Microsoft.Network/networkSecurityGroups@2025-01-01' = {
  name: 'nsg-snet-pe'
  location: location
  tags: effectiveTags
  properties: {
    securityRules: [
      {
        name: 'allow-appsvc-https'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: appsvcIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '443'
        }
      }
      {
        name: 'allow-appsvc-sql'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: appsvcIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '1433'
        }
      }
      {
        name: 'allow-aci-sql'
        properties: {
          priority: 120
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: aciSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '1433'
        }
      }
      {
        name: 'allow-apim-https'
        properties: {
          priority: 130
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: apimIntSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: peSubnetPrefix
          destinationPortRange: '443'
        }
      }
      {
        name: 'deny-other-vnet'
        properties: {
          priority: 4000
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2025-01-01' = {
  name: vnetName
  location: location
  tags: effectiveTags
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'snet-appsvc-int'
        properties: {
          addressPrefix: appsvcIntSubnetPrefix
          networkSecurityGroup: {
            id: nsgAppsvcInt.id
          }
          delegations: [
            {
              name: 'Microsoft.Web.serverFarms'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-pe'
        properties: {
          addressPrefix: peSubnetPrefix
          networkSecurityGroup: {
            id: nsgPe.id
          }
          privateEndpointNetworkPolicies: 'Enabled'
        }
      }
      {
        name: 'snet-aci'
        properties: {
          addressPrefix: aciSubnetPrefix
          delegations: [
            {
              name: 'Microsoft.ContainerInstance.containerGroups'
              properties: {
                serviceName: 'Microsoft.ContainerInstance/containerGroups'
              }
            }
          ]
        }
      }
      {
        name: 'snet-apim-int'
        properties: {
          addressPrefix: apimIntSubnetPrefix
          networkSecurityGroup: {
            id: nsgApimInt.id
          }
          delegations: [
            {
              name: 'Microsoft.Web.serverFarms'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
          serviceEndpoints: [
            {
              service: 'Microsoft.Web'
            }
          ]
        }
      }
    ]
  }
}

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for zoneName in privateDnsZoneNames: {
    name: zoneName
    location: 'global'
    tags: effectiveTags
  }
]

resource privateDnsZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for i in range(0, length(privateDnsZoneNames)): {
    parent: privateDnsZones[i]
    name: 'link-${vnetName}'
    location: 'global'
    tags: effectiveTags
    properties: {
      registrationEnabled: false
      virtualNetwork: {
        id: vnet.id
      }
    }
  }
]

output vnetId string = vnet.id
output vnetName string = vnet.name
output appsvcIntSubnetId string = vnet.properties.subnets[0].id
output peSubnetId string = vnet.properties.subnets[1].id
output aciSubnetId string = vnet.properties.subnets[2].id
output apimIntSubnetId string = vnet.properties.subnets[3].id
