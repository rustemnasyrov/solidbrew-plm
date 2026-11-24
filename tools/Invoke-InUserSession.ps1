<#
.SYNOPSIS
    Запустить программу на компьютере с SOLIDWORKS в интерактивной сессии пользователя (там, где доступен COM SOLIDWORKS)
    и дождаться результата. Запускается на машине разработки.

.DESCRIPTION
    Из SSH-сессии SOLIDWORKS недоступен (MK_E_UNAVAILABLE). Скрипт кладёт задание в общую папку .swjobs/session,
    через ssh регистрирует задачу планировщика с LogonType Interactive (пароль не нужен), запускает её,
    ждёт файл с кодом выхода и печатает вывод. Пользователь должен быть залогинен на той машине.

.EXAMPLE
    ./tools/Invoke-InUserSession.ps1 -Exe 'C:\Users\rnasyrov\work\solidworks\solidbrew-plm\publish\client\sbpdm.exe' -Arguments 'status','--dir','C:\PDM\work'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string[]]$Arguments = @(),
    [string]$WorkingDirectory,
    [string]$StdIn,
    [string]$SshTarget = 'rnasyrov@10.0.0.150',
    [string]$RemoteUser = 'airt\rnasyrov',
    [string]$RemoteProjectRoot = 'C:\Users\rnasyrov\work\solidworks\solidbrew-plm',
    [int]$TimeoutSec = 1800
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
$localJobs = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.swjobs\session'))
New-Item -ItemType Directory -Force -Path $localJobs | Out-Null
$id = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), ([guid]::NewGuid().ToString('N').Substring(0, 6))
$spec = [ordered]@{ exe = $Exe; args = @($Arguments); cwd = $WorkingDirectory; stdin = $StdIn }
[IO.File]::WriteAllText((Join-Path $localJobs "$id.json"), ($spec | ConvertTo-Json -Depth 3), $utf8)

$remoteJob = "$RemoteProjectRoot\.swjobs\session\$id.json"
$remoteRunner = "$RemoteProjectRoot\tools\Run-SessionJob.ps1"
$task = "SolidBrewSession-$id"
$remote = @"
`$a = New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "$remoteRunner" -Job "$remoteJob"'
`$p = New-ScheduledTaskPrincipal -UserId '$RemoteUser' -LogonType Interactive
`$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSec) -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName '$task' -Action `$a -Principal `$p -Settings `$s -Force | Out-Null
Start-ScheduledTask -TaskName '$task'
"@
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
ssh -o BatchMode=yes $SshTarget "powershell -NoProfile -NonInteractive -EncodedCommand $enc" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Не удалось запустить задачу на $SshTarget." }

$exitFile = Join-Path $localJobs "$id.exit"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
try {
    while (-not (Test-Path -LiteralPath $exitFile)) {
        if ((Get-Date) -gt $deadline) { throw "Нет результата за $TimeoutSec с (задача $task)." }
        Start-Sleep -Milliseconds 800
    }
} finally {
    $cleanup = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("Unregister-ScheduledTask -TaskName '$task' -Confirm:`$false -ErrorAction SilentlyContinue"))
    ssh -o BatchMode=yes $SshTarget "powershell -NoProfile -NonInteractive -EncodedCommand $cleanup" 2>$null | Out-Null
}
$outFile = Join-Path $localJobs "$id.out.txt"
if (Test-Path -LiteralPath $outFile) { [IO.File]::ReadAllText($outFile) }
$code = [int]([IO.File]::ReadAllText($exitFile).Trim())
exit $code
