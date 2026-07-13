// CopilotScope na Azure Container Apps.
// az deployment group create -g <rg> -f infra/main.bicep \
//   -p containerImage=<acr>.azurecr.io/copilotscope:latest ingestApiKey=<sekret>

@description('Lokalizacja zasobów')
param location string = resourceGroup().location

@description('Pełna nazwa obrazu kontenera (ACR lub inny rejestr)')
param containerImage string

@description('Klucz API wymagany na /v1/* (nagłówek x-api-key lub Bearer)')
@secure()
param ingestApiKey string

@description('Opcjonalny endpoint OTLP do forwardowania (puste = wyłączone)')
param forwardEndpoint string = ''

param appName string = 'copilotscope'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${appName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: true
        targetPort: 4318
        transport: 'http'
        allowInsecure: false
      }
      secrets: [
        { name: 'ingest-api-key', value: ingestApiKey }
      ]
    }
    template: {
      containers: [
        {
          name: appName
          image: containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://0.0.0.0:4318' }
            { name: 'CopilotScope__Ingest__ApiKey', secretRef: 'ingest-api-key' }
            { name: 'CopilotScope__Forward__Endpoint', value: forwardEndpoint }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/api/health', port: 4318 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 } // stan w pamięci → 1 replika
    }
  }
}

output dashboardUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
output otlpEndpoint string = 'https://${app.properties.configuration.ingress.fqdn}'
