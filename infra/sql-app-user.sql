-- Run once as a member of the SQL admin group:
-- sqlcmd -S <sqlServerFqdn> -d miniobilet --authentication-method ActiveDirectoryAzCli -v WebAppName=<webAppName> -i infra/sql-app-user.sql
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$(WebAppName)')
    CREATE USER [$(WebAppName)] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$(WebAppName)];
ALTER ROLE db_datawriter ADD MEMBER [$(WebAppName)];
ALTER ROLE db_ddladmin ADD MEMBER [$(WebAppName)];   -- needed for startup migrations
