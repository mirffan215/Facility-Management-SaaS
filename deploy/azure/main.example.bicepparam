using 'main.bicep'

param name = 'fmsprod'
param imageTag = '0.8.0'
param publicBaseUrl = 'https://fms.example.com'
param authProviders = [
  {
    name: 'entra'
    authority: 'https://login.microsoftonline.com/common/v2.0'
    audience: 'api://fms'
    clientId: '00000000-0000-0000-0000-000000000000'
    scope: 'openid profile email api://fms/access_as_user'
    issuerPrefix: 'https://login.microsoftonline.com/'
    externalTenantClaim: 'tid'
  }
]
// Supply these at deploy time instead of writing them here:  -p sqlAdminPassword=... sqlAppPassword=...
param sqlAdminPassword = readEnvironmentVariable('FMS_SQL_ADMIN_PASSWORD')
param sqlAppPassword = readEnvironmentVariable('FMS_SQL_APP_PASSWORD')
