<#
.SYNOPSIS
    Установка серверной части SolidBrew PDM, которой нужны права администратора: MinIO и правило брандмауэра.
.DESCRIPTION
    Запускать от администратора на сервере PDM. Повторный запуск безопасен (идемпотентно).
      1. Папка бинарников MinIO (-MinioDir) закрывается от изменения: полный доступ — SYSTEM и администраторам,
         остальным — чтение и запуск. Затем SHA-256 minio.exe и mc.exe сверяются с infra/minio/versions.json.
      2. Папка архива PDM_ARCHIVE_DIR (данные MinIO) создаётся и закрывается: доступ только SYSTEM и администраторам.
      3. <MinioDir>\config.env: MINIO_ROOT_USER, MINIO_ROOT_PASSWORD (из .env), MINIO_VOLUMES = PDM_ARCHIVE_DIR;
         доступ только SYSTEM и администраторам. <MinioDir>\Start-MinIO.ps1 — запуск MinIO с перезапуском при падении.
      4. Задача планировщика SolidBrewMinIO: при запуске системы, от SYSTEM, перезапуск при сбое.
         MinIO слушает 127.0.0.1:9000 (API) и 127.0.0.1:9001 (консоль): из ЛВС недоступен, а контейнер API
         достаёт его через host.docker.internal (Docker Desktop пересылает на loopback хоста).
      5. Задача запускается, затем tools\Initialize-MinIO.ps1: бакет pdm-objects, политика, пользователь pdm-api.
      6. Правило брандмауэра «SolidBrew PDM API»: входящий TCP на порт HTTPS API (PDM_HTTPS_PORT, 8443)
         только из -LanSubnet (10.0.0.0/16), профили Domain и Private.
    Пароли на экран не выводятся.
    -Uninstall: останавливает и удаляет задачу, правило брандмауэра, config.env и Start-MinIO.ps1.
    Бинарники и данные архива не удаляются.
.EXAMPLE
    # PowerShell от имени администратора, из папки репозитория:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-Server.ps1
#>
[CmdletBinding()]
param(
    [string]$MinioDir = 'C:\SolidBrew\minio',
    [string]$EnvFile,
    [string]$ListenAddress = '127.0.0.1:9000',
    [string]$ConsoleAddress = '127.0.0.1:9001',
    [string]$LanSubnet = '10.0.0.0/16',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repo = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $EnvFile) { $EnvFile = Join-Path $repo '.env' }
$taskName = 'SolidBrewMinIO'
$ruleName = 'SolidBrew PDM API'

$principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите скрипт от имени администратора (PowerShell → «Запуск от имени администратора»).'
}

$minioDirFull = [IO.Path]::GetFullPath($MinioDir)
$minioExe = Join-Path $minioDirFull 'minio.exe'
$mcExe = Join-Path $minioDirFull 'mc.exe'
$configEnv = Join-Path $minioDirFull 'config.env'
$launcher = Join-Path $minioDirFull 'Start-MinIO.ps1'
$logDir = Join-Path $minioDirFull 'logs'

$sidSystem = New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'
$sidAdmins = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
$sidUsers = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'

function Set-ProtectedAcl {
    # Наследование отключено; полный доступ SYSTEM и администраторам; -ReadSid — чтение и запуск.
    param([string]$Path, [Security.Principal.SecurityIdentifier]$ReadSid, [switch]$ResetChildren)
    $isDir = Test-Path -LiteralPath $Path -PathType Container
    $acl = if ($isDir) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($sidAdmins)
    $inherit = if ($isDir) { [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' } else { [Security.AccessControl.InheritanceFlags]::None }
    $none = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @($sidSystem, $sidAdmins)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $sid, 'FullControl', $inherit, $none, 'Allow'))
    }
    if ($ReadSid) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $ReadSid, 'ReadAndExecute', $inherit, $none, 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    if ($isDir -and $ResetChildren) {
        # Вложенные файлы и папки — только по наследованию от новой ACL (явные права снимаются).
        # config.env (секреты) не трогаем: у него своя закрытая ACL без наследования.
        Get-ChildItem -LiteralPath $Path -Recurse -Force | Where-Object { $_.Name -ne 'config.env' } | ForEach-Object {
            $child = Get-Acl -LiteralPath $_.FullName
            $child.SetAccessRuleProtection($false, $false)
            foreach ($rule in @($child.Access | Where-Object { -not $_.IsInherited })) { [void]$child.RemoveAccessRule($rule) }
            $child.SetOwner($sidAdmins)
            Set-Acl -LiteralPath $_.FullName -AclObject $child
        }
    }
}

function Stop-MinIO {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    Get-Process -Name minio -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -eq $minioExe) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

if ($Uninstall) {
    Stop-MinIO
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Output "Задача $taskName удалена."
    }
    $rules = @(Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)
    if ($rules.Count -gt 0) {
        $rules | Remove-NetFirewallRule
        Write-Output "Правило брандмауэра «$ruleName» удалено."
    }
    foreach ($file in @($configEnv, $launcher)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force; Write-Output "Удалён $file." }
    }
    Write-Output "Бинарники ($minioDirFull) и данные архива не тронуты."
    return
}

# --- .env ---------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $EnvFile)) { throw "Нет файла $EnvFile. Сначала выполните tools\New-LocalEnv.ps1." }
$values = @{}
foreach ($line in [IO.File]::ReadAllLines([IO.Path]::GetFullPath($EnvFile))) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { $values[$Matches[1]] = $Matches[2].Trim() }
}
foreach ($key in @('PDM_MINIO_ROOT_PASSWORD', 'PDM_S3_SECRET_KEY', 'PDM_ARCHIVE_DIR')) {
    if (-not $values[$key]) { throw "В $EnvFile нет $key (tools\New-LocalEnv.ps1)." }
}
$rootUser = if ($values['PDM_MINIO_ROOT_USER']) { $values['PDM_MINIO_ROOT_USER'] } else { 'pdm-root' }
$archive = [IO.Path]::GetFullPath($values['PDM_ARCHIVE_DIR'].Replace('/', '\'))
$httpsPort = if ($values['PDM_HTTPS_PORT']) { [int]$values['PDM_HTTPS_PORT'] } else { 8443 }
if ($archive.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Папка архива $archive внутри репозитория — укажите PDM_ARCHIVE_DIR вне $repo."
}
if ($archive -match '\s') { throw "Путь архива не должен содержать пробелов: $archive." }

# --- 1. Бинарники: сначала закрыть папку от изменения, затем сверить хеши ---------------------------------
if (-not (Test-Path -LiteralPath $minioDirFull)) { throw "Нет папки $minioDirFull с minio.exe и mc.exe." }
Stop-MinIO
Set-ProtectedAcl -Path $minioDirFull -ReadSid $sidUsers -ResetChildren
$versions = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repo 'infra\minio\versions.json') | ConvertFrom-Json
foreach ($item in @(@{ Name = 'minio'; Path = $minioExe }, @{ Name = 'mc'; Path = $mcExe })) {
    $expected = $versions.($item.Name).sha256
    if (-not $expected) { throw "В infra\minio\versions.json не зафиксирован SHA-256 для $($item.Name).exe — установка остановлена." }
    if (-not (Test-Path -LiteralPath $item.Path)) { throw "Нет файла $($item.Path)." }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.Path).Hash
    if ($actual -ne $expected.ToUpperInvariant()) {
        throw "SHA-256 $($item.Path) не совпадает с infra\minio\versions.json (ожидается $expected, получено $actual). Установка остановлена."
    }
    Write-Output "$($item.Name).exe $($versions.($item.Name).version): SHA-256 совпадает."
}

# --- 2. Папка архива -----------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $archive | Out-Null
Set-ProtectedAcl -Path $archive
Write-Output "Папка архива: $archive (доступ — SYSTEM и администраторы)."

# --- 3. config.env и запускатель ------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
$configText = @(
    "MINIO_ROOT_USER=$rootUser"
    "MINIO_ROOT_PASSWORD=$($values['PDM_MINIO_ROOT_PASSWORD'])"
    "MINIO_VOLUMES=$($archive.Replace('\', '/'))"
    'MINIO_BROWSER=on'
) -join "`n"
# Сначала пустой файл с закрытой ACL, потом содержимое — секрет ни мгновения не лежит под наследуемыми правами.
[IO.File]::WriteAllText($configEnv, '', $utf8)
Set-ProtectedAcl -Path $configEnv
[IO.File]::WriteAllText($configEnv, $configText + "`n", $utf8)

$launcherText = @"
# Создан tools\Install-Server.ps1. Запускает MinIO и перезапускает его при падении. Не редактируйте вручную.
`$ErrorActionPreference = 'Continue'
`$env:MINIO_CONFIG_ENV_FILE = '$configEnv'
while (`$true) {
    `$log = Join-Path '$logDir' ('minio-' + (Get-Date -Format 'yyyyMM') + '.log')
    Add-Content -LiteralPath `$log -Value ((Get-Date -Format s) + ' запуск MinIO')
    & cmd.exe /d /c "`"$minioExe`" server --quiet --address $ListenAddress --console-address $ConsoleAddress >> `"`$log`" 2>&1"
    Add-Content -LiteralPath `$log -Value ((Get-Date -Format s) + ' MinIO завершился с кодом ' + `$LASTEXITCODE + ', перезапуск через 10 с')
    Start-Sleep -Seconds 10
}
"@
[IO.File]::WriteAllText($launcher, $launcherText, (New-Object Text.UTF8Encoding $true))
Set-ProtectedAcl -Path $launcher -ReadSid $sidUsers

# --- 4. Задача планировщика --------------------------------------------------------------------------------
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$launcher`"" -WorkingDirectory $minioDirFull
$trigger = New-ScheduledTaskTrigger -AtStartup
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Description 'SolidBrew PDM: MinIO (архив содержимого), 127.0.0.1:9000' `
    -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Output "Задача $taskName зарегистрирована и запущена (MinIO: http://$ListenAddress, консоль http://$ConsoleAddress)."

# --- 5. Бакет, политика, пользователь API --------------------------------------------------------------
& (Join-Path $scriptDir 'Initialize-MinIO.ps1') -McPath $mcExe -Endpoint "http://$ListenAddress" -EnvFile $EnvFile -WaitSeconds 90

# --- 6. Брандмауэр ---------------------------------------------------------------------------------------
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Description 'SolidBrew PDM: HTTPS API (Docker Desktop) из ЛВС' `
    -Direction Inbound -Protocol TCP -LocalPort $httpsPort -RemoteAddress $LanSubnet -Profile Domain, Private -Action Allow | Out-Null
Write-Output "Правило брандмауэра «$ruleName»: TCP $httpsPort, только из $LanSubnet, профили Domain и Private."
Write-Output 'Готово. Дальше: docker compose up -d --build (из папки репозитория, от обычного пользователя).'
