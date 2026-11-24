<#
.SYNOPSIS
    Создаёт самоподписанный сертификат HTTPS для API SolidBrew PDM и прописывает его в .env.
.DESCRIPTION
    Сертификат: RSA 3072, SHA-256, срок 5 лет, назначение «проверка подлинности сервера»,
    альтернативные имена (SAN) — имена и адреса из -DnsNames и -IpAddresses.
    PFX сохраняется вне репозитория (по умолчанию C:\SolidBrew\certs\pdm-api.pfx, если туда нельзя писать —
    %LOCALAPPDATA%\SolidBrew\certs\pdm-api.pfx); доступ к файлу — только текущему пользователю, SYSTEM и администраторам.
    В .env дописываются PDM_TLS_PFX_PATH и PDM_TLS_PFX_PASSWORD (существующие значения не меняются).
    Если сертификат уже есть — только печатает его отпечаток; -Force выпускает новый с тем же паролем
    (после этого клиенты увидят новый отпечаток и спросят, доверять ли ему).
    Права администратора не нужны. Пароль на экран не выводится.
.EXAMPLE
    .\tools\New-ServerCert.ps1
#>
[CmdletBinding()]
param(
    [string]$EnvFile,
    [string]$PfxPath,
    [string[]]$DnsNames = @('D-VPAVLOV', 'localhost'),
    [string[]]$IpAddresses = @('10.0.1.9', '127.0.0.1'),
    [int]$Years = 5,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $EnvFile) { $EnvFile = Join-Path $scriptDir '..\.env' }
$EnvFile = [IO.Path]::GetFullPath($EnvFile)
if (-not (Test-Path -LiteralPath $EnvFile)) { throw "Нет файла $EnvFile. Сначала выполните tools\New-LocalEnv.ps1." }

function Read-DotEnv([string]$Path) {
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { $values[$Matches[1]] = $Matches[2].Trim() }
    }
    $values
}

function Add-DotEnv([string]$Path, [string[]]$Lines) {
    if ($Lines.Count -eq 0) { return }
    $text = [IO.File]::ReadAllText($Path)
    $prefix = if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { "`n" } else { '' }
    [IO.File]::AppendAllText($Path, $prefix + ($Lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
}

function New-Secret([int]$Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

function Get-Sha256Thumbprint([byte[]]$Raw) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($Raw) } finally { $sha.Dispose() }
    ($hash | ForEach-Object { $_.ToString('X2') }) -join ':'
}

function Set-PrivateAcl([string]$Path) {
    # Только текущий пользователь (Docker Desktop читает файл от его имени), SYSTEM и администраторы.
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($sid in @($me, (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $sid, 'FullControl', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

$envValues = Read-DotEnv $EnvFile
$append = @()

# Путь к PFX: из .env, из параметра или по умолчанию.
if ($envValues['PDM_TLS_PFX_PATH']) {
    $pfx = $envValues['PDM_TLS_PFX_PATH'].Replace('/', '\')
    if ($PfxPath -and ([IO.Path]::GetFullPath($PfxPath) -ne [IO.Path]::GetFullPath($pfx))) {
        Write-Warning "В .env уже указан PDM_TLS_PFX_PATH=$pfx — параметр -PfxPath не используется."
    }
} else {
    $candidates = if ($PfxPath) { @($PfxPath) } else { @('C:\SolidBrew\certs\pdm-api.pfx', (Join-Path $env:LOCALAPPDATA 'SolidBrew\certs\pdm-api.pfx')) }
    $pfx = $null
    foreach ($candidate in $candidates) {
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $candidate) | Out-Null
            $probe = Join-Path (Split-Path -Parent $candidate) ('.write-test-' + [Guid]::NewGuid().ToString('N'))
            [IO.File]::WriteAllText($probe, '')
            Remove-Item -LiteralPath $probe -Force
            $pfx = [IO.Path]::GetFullPath($candidate)
            break
        } catch {
            Write-Verbose "Нельзя писать в $(Split-Path -Parent $candidate): $($_.Exception.Message)"
        }
    }
    if (-not $pfx) { throw "Не удалось создать папку для сертификата: $($candidates -join '; ')." }
    $append += "PDM_TLS_PFX_PATH=$($pfx.Replace('\', '/'))"
}

$repo = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if ($pfx.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "PFX не должен лежать в репозитории ($pfx). Укажите папку вне $repo."
}

$password = $envValues['PDM_TLS_PFX_PASSWORD']
if (-not $password) {
    $password = New-Secret
    $append += "PDM_TLS_PFX_PASSWORD=$password"
}

if ((Test-Path -LiteralPath $pfx) -and -not $Force) {
    $existing = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(
        $pfx, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    Add-DotEnv $EnvFile $append
    Write-Output "Сертификат уже есть: $pfx (действителен до $($existing.NotAfter.ToString('dd.MM.yyyy'))). Новый не создаётся (-Force — выпустить заново)."
    Write-Output "Отпечаток SHA-256: $(Get-Sha256Thumbprint $existing.RawData)"
    $existing.Dispose()
    return
}

# Выпуск: временно в хранилище текущего пользователя (ключ экспортируемый), экспорт в PFX, удаление из хранилища.
$san = (@($DnsNames | ForEach-Object { "DNS=$_" }) + @($IpAddresses | ForEach-Object { "IPAddress=$_" })) -join '&'
$cert = New-SelfSignedCertificate `
    -Subject "CN=SolidBrew PDM $($DnsNames[0])" `
    -FriendlyName 'SolidBrew PDM API' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable -KeyUsage DigitalSignature, KeyEncipherment `
    -NotBefore (Get-Date).AddDays(-1) -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @("2.5.29.17={text}$san", '2.5.29.37={text}1.3.6.1.5.5.7.3.1', '2.5.29.19={text}false')
try {
    $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
    $temp = "$pfx.tmp"
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    Export-PfxCertificate -Cert $cert -FilePath $temp -Password $secure -CryptoAlgorithmOption AES256_SHA256 | Out-Null
    Set-PrivateAcl $temp
    Move-Item -LiteralPath $temp -Destination $pfx -Force
    $thumb = Get-Sha256Thumbprint $cert.RawData
    $notAfter = $cert.NotAfter
} finally {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
}

Add-DotEnv $EnvFile $append
Write-Output "Сертификат HTTPS: $pfx (RSA 3072, до $($notAfter.ToString('dd.MM.yyyy')); имена: $(($DnsNames + $IpAddresses) -join ', '))."
if ($append.Count -gt 0) { Write-Output "В $EnvFile дописаны: $((($append | ForEach-Object { ($_ -split '=', 2)[0] })) -join ', ')." }
Write-Output "Отпечаток SHA-256: $thumb"
Write-Output 'Сообщите отпечаток конструкторам: клиент покажет его при первом подключении. Перезапустите API: docker compose up -d.'
