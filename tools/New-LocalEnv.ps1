<#
.SYNOPSIS
    Создаёт или дополняет файл .env для docker compose (SolidBrew PDM).
.DESCRIPTION
    Новый файл создаётся целиком. В существующий дописываются только недостающие ключи;
    имеющиеся значения не меняются. Секреты генерируются криптографически стойким генератором.
    PDM_ARCHIVE_DIR — папка данных MinIO (служба Windows вне Docker); папка создаётся.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$ArchiveDir = (Join-Path $HOME 'SolidBrewArchive'),
    [string]$S3Endpoint = 'http://host.docker.internal:9000'
)
$ErrorActionPreference = 'Stop'
if (-not $Output) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Output = Join-Path $scriptDir '..\.env'
}

function New-Secret([int]$Bytes = 32) {
    $buffer = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

$path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$archive = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArchiveDir)

# Порядок ключей — порядок записи в новый файл.
$defaults = [ordered]@{
    PDM_DB_PASSWORD              = { New-Secret }
    PDM_APP_DB_PASSWORD          = { New-Secret }
    PDM_BOOTSTRAP_ADMIN_PASSWORD = { New-Secret 12 }
    PDM_MINIO_ROOT_PASSWORD      = { New-Secret }
    PDM_S3_SECRET_KEY            = { New-Secret }
    PDM_S3_ENDPOINT              = { $S3Endpoint }
    PDM_ARCHIVE_DIR              = { $archive.Replace('\', '/') }
}

$existing = @{}
$exists = Test-Path -LiteralPath $path
if ($exists) {
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=') { $existing[$Matches[1]] = $true }
    }
}

$added = @()
$lines = foreach ($key in $defaults.Keys) {
    if ($existing.ContainsKey($key)) { continue }
    $added += $key
    "$key=$(& $defaults[$key])"
}

if ($exists) {
    if ($added.Count -gt 0) {
        $text = [IO.File]::ReadAllText($path)
        $prefix = if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { "`n" } else { '' }
        [IO.File]::AppendAllText($path, $prefix + ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
        Write-Output "Дополнен $path ключами: $($added -join ', ')."
    } else {
        Write-Output "В $path уже есть все ключи — ничего не изменено."
    }
    if ($existing.ContainsKey('PDM_API_TOKEN')) {
        Write-Warning 'Ключ PDM_API_TOKEN больше не используется (вход по учётным записям) — его можно удалить из .env.'
    }
} else {
    $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $data = (New-Object Text.UTF8Encoding $false).GetBytes(($lines -join "`n") + "`n")
        $file.Write($data, 0, $data.Length)
    } finally { $file.Dispose() }
    Write-Output "Создан $path. Храните файл в секрете."
}

if ($added -contains 'PDM_ARCHIVE_DIR') {
    New-Item -ItemType Directory -Force -Path $archive | Out-Null
    Write-Output "Папка архива MinIO: $archive"
}
if ($added -contains 'PDM_BOOTSTRAP_ADMIN_PASSWORD') {
    Write-Output 'Пароль первого администратора (вход «admin») — значение PDM_BOOTSTRAP_ADMIN_PASSWORD в .env. Смените его после первого входа.'
}
