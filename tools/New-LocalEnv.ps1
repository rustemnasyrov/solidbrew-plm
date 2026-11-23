[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\.env'))
$ErrorActionPreference = 'Stop'
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $lines = foreach ($name in 'PDM_DB_PASSWORD','PDM_MINIO_ROOT_PASSWORD','PDM_S3_SECRET_KEY','PDM_API_TOKEN') {
        $bytes = New-Object byte[] 32
        $rng.GetBytes($bytes)
        $name + '=' + (-join ($bytes | ForEach-Object { $_.ToString('x2') }))
    }
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
    $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $data = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
        $file.Write($data, 0, $data.Length)
    } finally { $file.Dispose() }
    Write-Output "Created $path. Keep this file private."
} finally { $rng.Dispose() }
