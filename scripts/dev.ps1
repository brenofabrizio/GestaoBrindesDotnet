param(
    [ValidateSet('start', 'stop', 'status', 'migrate', 'test')]
    [string]$Action = 'start'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$pgBin = 'C:\Program Files\PostgreSQL\17\bin'
$dataDir = Join-Path $env:LOCALAPPDATA 'Gbrindes\postgres-data'
$logDir = Join-Path $env:LOCALAPPDATA 'Gbrindes\logs'
$logFile = Join-Path $logDir 'postgres.log'
$port = 55432
$dbName = 'gbrindes_dev'
$connection = "Host=127.0.0.1;Port=$port;Database=$dbName;Username=postgres;Timeout=5;Command Timeout=30"
$pgCtl = Join-Path $pgBin 'pg_ctl.exe'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$initDb = Join-Path $pgBin 'initdb.exe'
$psql = Join-Path $pgBin 'psql.exe'
$createdb = Join-Path $pgBin 'createdb.exe'
$dropdb = Join-Path $pgBin 'dropdb.exe'

if (-not (Test-Path $pgCtl)) {
    throw "PostgreSQL 17 binaries not found at '$pgBin'. Install PostgreSQL 17 or update pgBin in this script."
}

if ($Action -eq 'stop') {
    if (Test-Path $dataDir) { & $pgCtl -D $dataDir -m fast stop }
    exit $LASTEXITCODE
}

if ($Action -eq 'start' -and -not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
    New-Item -ItemType Directory -Force -Path $dataDir, $logDir | Out-Null
    & $initDb -D $dataDir -U postgres --auth-local=trust --auth-host=trust --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed.' }
    Add-Content -Path (Join-Path $dataDir 'postgresql.conf') -Value "`nlisten_addresses = '127.0.0.1'`nport = $port`n"
}

if ($Action -eq 'start' -or $Action -eq 'migrate' -or $Action -eq 'test') {
    if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
        throw "Local cluster not initialized. Run: .\scripts\dev.ps1 start"
    }
    & $pgCtl -D $dataDir status *> $null
    if ($LASTEXITCODE -ne 0) {
        & $pgCtl -D $dataDir -l $logFile -o "-h 127.0.0.1 -p $port" -w start
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL failed to start; inspect the local log file.' }
    }
    if ($Action -ne 'test') {
        $dbExists = & $psql -h 127.0.0.1 -p $port -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$dbName'"
        if (($dbExists | Out-String).Trim() -ne '1') {
            & $createdb -h 127.0.0.1 -p $port -U postgres $dbName
            if ($LASTEXITCODE -ne 0) { throw 'Could not create the local development database.' }
        }
    }
}

if ($Action -eq 'status') {
    if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) { Write-Output 'PostgreSQL cluster not initialized.'; exit 1 }
    & $pgCtl -D $dataDir status
    exit $LASTEXITCODE
}

if ($Action -eq 'migrate' -or $Action -eq 'test') {
    if ($Action -eq 'test') {
        $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
        $env:MSBUILDDISABLENODEREUSE = '1'
    }

    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Local .NET tools could not be restored.' }
    & $dotnet restore (Join-Path $repoRoot 'Gbrindes.slnx')
    if ($LASTEXITCODE -ne 0) { throw 'NuGet packages could not be restored.' }
}

if ($Action -eq 'migrate') {
    $env:ConnectionStrings__DefaultConnection = $connection
    & $dotnet ef database update --connection $connection --project (Join-Path $repoRoot 'src\Gbrindes.Web') --startup-project (Join-Path $repoRoot 'src\Gbrindes.Web')
    if ($LASTEXITCODE -ne 0) { throw 'EF Core migration failed.' }
}

if ($Action -eq 'test') {
    $validationDbName = "gbrindes_validation_$([Guid]::NewGuid().ToString('N'))"
    $validationDbCreated = $false
    $validationConnection = "Host=127.0.0.1;Port=$port;Database=$validationDbName;Username=postgres;Timeout=5;Command Timeout=30"
    try {
        & $createdb -h 127.0.0.1 -p $port -U postgres -T template0 $validationDbName
        if ($LASTEXITCODE -ne 0) { throw "Could not create the isolated PostgreSQL test database '$validationDbName'." }
        $validationDbCreated = $true

        $env:ConnectionStrings__DefaultConnection = $validationConnection
        & $dotnet ef database update --connection $validationConnection --project (Join-Path $repoRoot 'src\Gbrindes.Web') --startup-project (Join-Path $repoRoot 'src\Gbrindes.Web')
        if ($LASTEXITCODE -ne 0) { throw 'EF Core migration failed for the isolated PostgreSQL test database.' }

        Remove-Item Env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
        Remove-Item Env:GBRINDES_RUN_POSTGRES_TESTS -ErrorAction SilentlyContinue
        & $dotnet test (Join-Path $repoRoot 'Gbrindes.slnx') --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'The standard test suite failed.' }

        $env:ConnectionStrings__DefaultConnection = $validationConnection
        $env:GBRINDES_RUN_POSTGRES_TESTS = '1'
        & $dotnet test (Join-Path $repoRoot 'tests\Gbrindes.Tests\Gbrindes.Tests.csproj') --no-restore --filter 'FullyQualifiedName~PostgreSql'
        if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL integration test failed.' }
    }
    finally {
        Remove-Item Env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
        Remove-Item Env:GBRINDES_RUN_POSTGRES_TESTS -ErrorAction SilentlyContinue
        if ($validationDbCreated) {
            & $dropdb -h 127.0.0.1 -p $port -U postgres --if-exists --force $validationDbName
            if ($LASTEXITCODE -ne 0) { throw "Could not remove the isolated PostgreSQL test database '$validationDbName'." }
        }
    }
}

if ($Action -eq 'start') { Write-Output "PostgreSQL ready: 127.0.0.1:$port/$dbName" }
if ($Action -eq 'migrate') { Write-Output 'Migrations applied successfully.' }
if ($Action -eq 'test') { Write-Output 'Standard test suite and PostgreSQL integration test passed.' }
