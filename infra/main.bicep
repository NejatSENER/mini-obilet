// Mini Obilet infrastructure: every Azure resource in one deployment
// az deployment group create -g rg-miniobilet-se -f infra/main.bicep -p githubRepo=... githubSubject=...

@description('Region for all resources')
param location string = resourceGroup().location

@description('Resource name prefix')
param prefix string = 'miniobilet'

@description('GitHub repository, e.g. NejatSENER/mini-obilet')
param githubRepo string

@description('GitHub OIDC subject. New repos include owner and repo ids: repo:owner@id/repo@id:environment:production')
param githubSubject string = 'repo:${githubRepo}:environment:production'

@description('Use the Azure SQL free offer (one per subscription)')
param useFreeSqlOffer bool = true

@description('App Service .NET runtime')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Alert email (no email notification when empty)')
param alertEmail string = ''

@secure()
@description('Sample secret: payment provider API key, stored in Key Vault')
param paymentApiKey string = newGuid()

var suffix = take(uniqueString(resourceGroup().id), 6)
var webAppName = '${prefix}-api-${suffix}'
var dbName = 'miniobilet'
var sqlServerName = '${prefix}-sql-${suffix}'
var sqlFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'   // avoids a web <-> sql dependency cycle

// ---------- Monitoring ----------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-log-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-ai-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ---------- App ----------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${prefix}-plan-${suffix}'
  location: location
  kind: 'linux'
  sku: { name: 'F1', tier: 'Free' }
  properties: { reserved: true }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  identity: { type: 'SystemAssigned' }   // passwordless access to SQL and Key Vault
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'Db__Provider', value: 'SqlServer' }
        { name: 'ConnectionStrings__Db', value: 'Server=tcp:${sqlFqdn},1433;Database=${dbName};Authentication=Active Directory Default;Encrypt=True;' }
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ---------- Database ----------
resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Demo simplification: the app's identity is the Entra admin so it can run migrations.
    // In production the admin is an Entra group and the app only gets the roles it needs.
    administrators: {
      administratorType: 'ActiveDirectory'
      login: web.name
      sid: web.identity.principalId
      tenantId: subscription().tenantId
      principalType: 'Application'
      azureADOnlyAuthentication: true   // no SQL passwords, Entra identities only
    }
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAllWindowsAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: dbName
  location: location
  sku: useFreeSqlOffer
    ? { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
    : { name: 'Basic', tier: 'Basic' }
  properties: useFreeSqlOffer
    ? {
        useFreeLimit: true
        freeLimitExhaustionBehavior: 'AutoPause'   // pause instead of billing when the free limit runs out
        autoPauseDelay: 60
        minCapacity: json('0.5')
        requestedBackupStorageRedundancy: 'Local'
      }
    : {
        requestedBackupStorageRedundancy: 'Local'
      }
}

// ---------- Secrets ----------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv-${suffix}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
  }
}

resource paymentSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'PaymentProvider--ApiKey'   // read as PaymentProvider:ApiKey
  properties: { value: paymentApiKey }
}

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
resource webKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, web.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Passwordless deploy identity for GitHub Actions (OIDC) ----------
resource githubIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-github-${suffix}'
  location: location
}

resource githubTrust 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: githubIdentity
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubject   // only this repo's production environment
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

var websiteContributor = 'de139f84-1756-47ae-9be6-808fbbe84772'
resource githubDeployAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, githubIdentity.id, websiteContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributor)
    principalId: githubIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Alert: more than 5 failed requests in 5 minutes ----------
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: '${prefix}-ag-${suffix}'
  location: 'global'
  properties: {
    groupShortName: 'miniobilet'
    enabled: true
    emailReceivers: [ { name: 'developer', emailAddress: alertEmail, useCommonAlertSchema: true } ]
  }
}

resource failedRequestsAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-failed-requests-${suffix}'
  location: 'global'
  properties: {
    severity: 2
    enabled: true
    scopes: [ appInsights.id ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'failed-requests'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'requests/failed'
          metricNamespace: 'microsoft.insights/components'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Count'
        }
      ]
    }
    actions: empty(alertEmail) ? [] : [ { actionGroupId: actionGroup.id } ]
  }
}

output webAppName string = web.name
output webAppUrl string = 'https://${web.properties.defaultHostName}'
output sqlServerName string = sql.name
output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output sqlDbName string = dbName
output keyVaultName string = keyVault.name
output githubClientId string = githubIdentity.properties.clientId
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId
