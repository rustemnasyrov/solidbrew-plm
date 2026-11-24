<#
.SYNOPSIS
    Настраивает запущенный MinIO для SolidBrew PDM: бакет, политика, пользователь API.
.DESCRIPTION
    Идемпотентно, через mc.exe:
      - бакет pdm-objects (закрытый);
      - политика pdm-objects-rw из infra/minio/pdm-policy.json (только GetObject/PutObject в objects/*, без удаления);
      - пользователь pdm-api с секретом PDM_S3_SECRET_KEY из .env и этой политикой.
    Учётная запись root (PDM_MINIO_ROOT_USER / PDM_MINIO_ROOT_PASSWORD) передаётся mc через переменную окружения
    MC_HOST_<псевдоним>, секрет pdm-api — через стандартный ввод: в командной строке процессов паролей нет.
    Конфигурация mc — во временной папке, удаляется после работы. Пароли на экран не выводятся.
    Права администратора не нужны: вызывается из Install-Server.ps1, а для проверки — против MinIO, запущенного вручную.
.EXAMPLE
    .\tools\Initialize-MinIO.ps1 -McPath C:\SolidBrew\minio\mc.exe
#>
[CmdletBinding()]
param(
    [string]$McPath = 'C:\SolidBrew\minio\mc.exe',
    [string]$Endpoint = 'http://127.0.0.1:9000',
    [string]$EnvFile,
    [string]$Bucket = 'pdm-objects',
    [string]$ApiUser = 'pdm-api',
    [string]$PolicyName = 'pdm-objects-rw',
    [string]$PolicyFile,
    [int]$WaitSeconds = 60
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $EnvFile) { $EnvFile = Join-Path $scriptDir '..\.env' }
if (-not $PolicyFile) { $PolicyFile = Join-Path $scriptDir '..\infra\minio\pdm-policy.json' }
foreach ($file in @($McPath, $EnvFile, $PolicyFile)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Не найден файл $file." }
}

$values = @{}
foreach ($line in [IO.File]::ReadAllLines([IO.Path]::GetFullPath($EnvFile))) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { $values[$Matches[1]] = $Matches[2].Trim() }
}
$rootUser = if ($values['PDM_MINIO_ROOT_USER']) { $values['PDM_MINIO_ROOT_USER'] } else { 'pdm-root' }
$rootPassword = $values['PDM_MINIO_ROOT_PASSWORD']
$apiSecret = $values['PDM_S3_SECRET_KEY']
if (-not $rootPassword -or -not $apiSecret) { throw "В $EnvFile нет PDM_MINIO_ROOT_PASSWORD или PDM_S3_SECRET_KEY (tools\New-LocalEnv.ps1)." }

# Ожидание готовности MinIO.
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ($true) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri ($Endpoint.TrimEnd('/') + '/minio/health/live') -TimeoutSec 3
        if ($r.StatusCode -eq 200) { break }
    } catch {
        if ((Get-Date) -gt $deadline) { throw "MinIO не отвечает на $Endpoint за $WaitSeconds с: $($_.Exception.Message)" }
    }
    Start-Sleep -Seconds 1
}

$alias = 'pdmsetup'
$uri = [Uri]$Endpoint
$hostUrl = '{0}://{1}:{2}@{3}' -f $uri.Scheme, [Uri]::EscapeDataString($rootUser), [Uri]::EscapeDataString($rootPassword), $uri.Authority
$configDir = Join-Path ([IO.Path]::GetTempPath()) ('sb-mc-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $configDir | Out-Null
$previousHost = [Environment]::GetEnvironmentVariable("MC_HOST_$alias", 'Process')
[Environment]::SetEnvironmentVariable("MC_HOST_$alias", $hostUrl, 'Process')

function Invoke-Mc {
    param([string[]]$Arguments, [string]$InputText, [switch]$AllowFailure)
    $all = @('--config-dir', $configDir, '--no-color', '--disable-pager') + $Arguments
    if ($PSBoundParameters.ContainsKey('InputText')) {
        $output = $InputText | & $McPath @all 2>&1
    } else {
        $output = & $McPath @all 2>&1
    }
    $code = $LASTEXITCODE
    $text = ($output | ForEach-Object { "$_" }) -join "`n"
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "mc $($Arguments[0..([Math]::Min(2, $Arguments.Count - 1))] -join ' '): код $code. $text"
    }
    [pscustomobject]@{ Code = $code; Text = $text }
}

try {
    Invoke-Mc @('mb', '--ignore-existing', "$alias/$Bucket") | Out-Null
    Invoke-Mc @('anonymous', 'set', 'none', "$alias/$Bucket") | Out-Null
    Write-Output "Бакет ${Bucket}: есть, анонимный доступ закрыт."

    Invoke-Mc @('admin', 'policy', 'create', $alias, $PolicyName, ([IO.Path]::GetFullPath($PolicyFile))) | Out-Null
    Write-Output "Политика ${PolicyName}: из $PolicyFile."

    # Секрет — через стандартный ввод (mc спрашивает Secret Key, если он не указан в командной строке).
    Invoke-Mc -Arguments @('admin', 'user', 'add', $alias, $ApiUser) -InputText $apiSecret | Out-Null
    $info = Invoke-Mc @('admin', 'user', 'info', $alias, $ApiUser, '--json')
    $policy = ($info.Text | ConvertFrom-Json).policyName
    if (-not $policy -or (($policy -split ',') -notcontains $PolicyName)) {
        Invoke-Mc @('admin', 'policy', 'attach', $alias, $PolicyName, '--user', $ApiUser) | Out-Null
    }
    Write-Output "Пользователь ${ApiUser}: секрет из PDM_S3_SECRET_KEY, политика $PolicyName."

    # Проверка: под pdm-api можно записать и прочитать объект в objects/, но нельзя удалить.
    $apiAlias = 'pdmapicheck'
    $apiHost = '{0}://{1}:{2}@{3}' -f $uri.Scheme, [Uri]::EscapeDataString($ApiUser), [Uri]::EscapeDataString($apiSecret), $uri.Authority
    [Environment]::SetEnvironmentVariable("MC_HOST_$apiAlias", $apiHost, 'Process')
    try {
        $probe = Join-Path $configDir 'probe.txt'
        [IO.File]::WriteAllText($probe, 'solidbrew-minio-probe')
        $key = "$apiAlias/$Bucket/objects/00/solidbrew-initialize-probe"
        Invoke-Mc @('cp', '--quiet', $probe, $key) | Out-Null
        $read = Invoke-Mc @('cat', $key)
        if ($read.Text -ne 'solidbrew-minio-probe') { throw "Проверочный объект прочитан неверно." }
        $delete = Invoke-Mc -Arguments @('rm', $key) -AllowFailure
        if ($delete.Code -eq 0) { Write-Warning "Пользователь $ApiUser смог удалить объект — проверьте политику $PolicyName." }
        else { Invoke-Mc @('rm', "$alias/$Bucket/objects/00/solidbrew-initialize-probe") | Out-Null }
        Write-Output "Проверка ${ApiUser}: запись и чтение — да, удаление — нет."
    } finally {
        [Environment]::SetEnvironmentVariable("MC_HOST_$apiAlias", $null, 'Process')
    }
} finally {
    [Environment]::SetEnvironmentVariable("MC_HOST_$alias", $previousHost, 'Process')
    Remove-Item -LiteralPath $configDir -Recurse -Force -ErrorAction SilentlyContinue
}
