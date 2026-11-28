param name string
param location string
param tags object

@allowed([
  'BasicV2'
  'StandardV2'
])
param skuName string = 'StandardV2'
param publisherEmail string
param publisherName string = 'AzureLab'
param subnetResourceId string = ''
param apiHostName string
param openAiEndpoint string = ''
param appInsightsName string
param azureAdTenantId string
param azureAdClientId string
param expires string = ''

var effectiveTags = empty(expires) ? tags : union(tags, { expires: expires })

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: name
  location: location
  tags: effectiveTags
  sku: {
    name: skuName
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
    virtualNetworkType: empty(subnetResourceId) ? 'None' : 'External'
    virtualNetworkConfiguration: empty(subnetResourceId)
      ? null
      : {
          subnetResourceId: subnetResourceId
        }
  }
}

resource apiBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'azurelab-api'
  properties: {
    title: 'AzureLab API'
    protocol: 'http'
    url: 'https://${apiHostName}'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
  }
}

resource appInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'appinsights-logger'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: appInsights.id
    credentials: {
      instrumentationKey: appInsights.properties.InstrumentationKey
    }
    isBuffered: true
  }
}

resource appInsightsDiagnostics 'Microsoft.ApiManagement/service/diagnostics@2024-05-01' = {
  parent: apim
  name: 'applicationinsights'
  properties: {
    alwaysLog: 'allErrors'
    httpCorrelationProtocol: 'W3C'
    loggerId: appInsightsLogger.id
    logClientIp: true
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
    frontend: {
      request: {
        headers: [
          'X-Correlation-Id'
          'traceparent'
        ]
      }
      response: {
        headers: [
          'X-Correlation-Id'
        ]
      }
    }
    backend: {
      request: {
        headers: [
          'X-Correlation-Id'
          'traceparent'
        ]
      }
    }
  }
}

resource azureLabApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'azurelab-api'
  properties: {
    displayName: 'AzureLab API'
    path: 'api'
    protocols: [
      'https'
    ]
    subscriptionRequired: false
    format: 'openapi+json'
    value: loadTextContent('../apim/azurelab-api.openapi.json')
  }
}

var loginEndpoint = endsWith(environment().authentication.loginEndpoint, '/')
  ? environment().authentication.loginEndpoint
  : '${environment().authentication.loginEndpoint}/'

var apiPolicyXml = replace(
  replace(
    replace(loadTextContent('../apim/api-policy.xml'), '__LOGIN_ENDPOINT__', loginEndpoint),
    '__TENANT_ID__',
    azureAdTenantId
  ),
  '__CLIENT_ID__',
  azureAdClientId
)

resource azureLabApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: azureLabApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: apiPolicyXml
  }
  dependsOn: [
    apiBackend
  ]
}

// ---------- AI Gateway (Week 2, Day 6 Step 5 — keyless MI to private Azure OpenAI) ----------
var normalizedOpenAiEndpoint = endsWith(openAiEndpoint, '/')
  ? substring(openAiEndpoint, 0, length(openAiEndpoint) - 1)
  : openAiEndpoint

resource openAiBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = if (!empty(openAiEndpoint)) {
  parent: apim
  name: 'azure-openai'
  properties: {
    title: 'Azure OpenAI (Private)'
    protocol: 'http'
    url: normalizedOpenAiEndpoint
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
  }
}

resource openAiGatewayApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = if (!empty(openAiEndpoint)) {
  parent: apim
  name: 'azure-openai-gw'
  properties: {
    displayName: 'Azure OpenAI AI Gateway'
    path: 'openai-gw'
    protocols: [
      'https'
    ]
    subscriptionRequired: false
  }
}

resource openAiChatOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = if (!empty(openAiEndpoint)) {
  parent: openAiGatewayApi
  name: 'post-chat-completions'
  properties: {
    displayName: 'Chat Completions'
    method: 'POST'
    urlTemplate: '/openai/deployments/{deployment-id}/chat/completions'
    templateParameters: [
      {
        name: 'deployment-id'
        type: 'string'
        required: true
      }
    ]
  }
}

resource openAiEmbeddingsOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = if (!empty(openAiEndpoint)) {
  parent: openAiGatewayApi
  name: 'post-embeddings'
  properties: {
    displayName: 'Embeddings'
    method: 'POST'
    urlTemplate: '/openai/deployments/{deployment-id}/embeddings'
    templateParameters: [
      {
        name: 'deployment-id'
        type: 'string'
        required: true
      }
    ]
  }
}

var openAiGwPolicyXml = replace(
  replace(loadTextContent('../apim/openai-gw-policy.xml'), '__LOGIN_ENDPOINT__', loginEndpoint),
  '__TENANT_ID__',
  azureAdTenantId
)

resource openAiGatewayPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = if (!empty(openAiEndpoint)) {
  parent: openAiGatewayApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: openAiGwPolicyXml
  }
  dependsOn: [
    openAiBackend
  ]
}

output id string = apim.id
output name string = apim.name
output gatewayUrl string = apim.properties.gatewayUrl
output principalId string = apim.identity.principalId
