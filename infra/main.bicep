// Mini Obilet altyapısı: tek komutla bütün Azure kaynakları
// az deployment group create -g rg-miniobilet -f infra/main.bicep -p githubRepo=...

@description('Kaynakların bölgesi')
param location string = resourceGroup().location

@description('Kaynak adlarının ön eki')
param ad string = 'miniobilet'

@description('GitHub deposu, ör. NejatSENER/mini-obilet (şifresiz deploy için)')
param githubRepo string

@description('Azure SQL ücretsiz teklifini kullan (abonelik başına bir kez)')
param ucretsizSql bool = true

@description('App Service .NET sürümü')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Alarm e-postası (boş bırakılırsa e-posta gönderilmez)')
param alarmEposta string = ''

@secure()
@description('Örnek sır: ödeme sağlayıcısı API anahtarı (Key Vault\'a yazılır)')
param odemeApiKey string = newGuid()

var ek = take(uniqueString(resourceGroup().id), 6)
var webAdi = '${ad}-api-${ek}'
var dbAdi = 'miniobilet'
var sqlAdi = '${ad}-sql-${ek}'
var sqlFqdn = '${sqlAdi}${environment().suffixes.sqlServerHostname}'   // web <-> sql döngüsel bağımlılığını önler

// ---------- İzleme ----------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${ad}-log-${ek}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${ad}-ai-${ek}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ---------- Uygulama ----------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${ad}-plan-${ek}'
  location: location
  kind: 'linux'
  sku: { name: 'F1', tier: 'Free' }
  properties: { reserved: true }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webAdi
  location: location
  identity: { type: 'SystemAssigned' }   // Managed Identity: SQL ve Key Vault'a şifresiz erişim
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
        { name: 'ConnectionStrings__Db', value: 'Server=tcp:${sqlFqdn},1433;Database=${dbAdi};Authentication=Active Directory Default;Encrypt=True;' }
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ---------- Veritabanı ----------
resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlAdi
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Demo sadeliği: uygulamanın Managed Identity'si Entra yöneticisi (şema migration'ı da yapabilsin).
    // Gerçek ortamda yönetici bir Entra grubu olur, uygulamaya sadece gereken roller verilir.
    administrators: {
      administratorType: 'ActiveDirectory'
      login: web.name
      sid: web.identity.principalId
      tenantId: subscription().tenantId
      principalType: 'Application'
      azureADOnlyAuthentication: true   // SQL şifresi yok, sadece Entra kimlikleri
    }
  }
}

resource sqlAzureErisimi 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAllWindowsAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: dbAdi
  location: location
  sku: ucretsizSql
    ? { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
    : { name: 'Basic', tier: 'Basic' }
  properties: ucretsizSql
    ? {
        useFreeLimit: true
        freeLimitExhaustionBehavior: 'AutoPause'   // ücretsiz kota bitince para yazmak yerine durakla
        autoPauseDelay: 60
        minCapacity: json('0.5')
        requestedBackupStorageRedundancy: 'Local'
      }
    : {
        requestedBackupStorageRedundancy: 'Local'
      }
}

// ---------- Sırlar ----------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${ad}-kv-${ek}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
  }
}

resource odemeSirri 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'OdemeSaglayici--ApiKey'   // config'te OdemeSaglayici:ApiKey olarak okunur
  properties: { value: odemeApiKey }
}

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
resource webKeyVaultYetkisi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, web.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- GitHub Actions için şifresiz deploy kimliği (OIDC) ----------
resource githubKimligi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${ad}-github-${ek}'
  location: location
}

resource githubGuveni 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: githubKimligi
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepo}:environment:production'   // sadece bu reponun production ortamı
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

var websiteContributor = 'de139f84-1756-47ae-9be6-808fbbe84772'
resource githubDeployYetkisi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, githubKimligi.id, websiteContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributor)
    principalId: githubKimligi.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Alarm: 5 dakikada 5'ten fazla başarısız istek ----------
resource aksiyonGrubu 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alarmEposta)) {
  name: '${ad}-ag-${ek}'
  location: 'global'
  properties: {
    groupShortName: 'miniobilet'
    enabled: true
    emailReceivers: [ { name: 'gelistirici', emailAddress: alarmEposta, useCommonAlertSchema: true } ]
  }
}

resource hataAlarmi 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${ad}-hata-alarmi-${ek}'
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
          name: 'basarisiz-istekler'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'requests/failed'
          metricNamespace: 'microsoft.insights/components'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Count'
        }
      ]
    }
    actions: empty(alarmEposta) ? [] : [ { actionGroupId: aksiyonGrubu.id } ]
  }
}

output webAppName string = web.name
output webAppUrl string = 'https://${web.properties.defaultHostName}'
output sqlServerName string = sql.name
output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output sqlDbName string = dbAdi
output keyVaultName string = keyVault.name
output githubClientId string = githubKimligi.properties.clientId
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId
