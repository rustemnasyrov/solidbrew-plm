<#
.SYNOPSIS
    Выполнить команду SwProbe на компьютере с SOLIDWORKS через папку заданий .swjobs.

.EXAMPLE
    ./tools/Invoke-SwJob.ps1 info
    ./tools/Invoke-SwJob.ps1 read 'C:\Users\rnasyrov\AppData\Local\Temp\solidbrew-probe\testkit\ТК.000.000 Изделие тестовое.SLDASM'
    ./tools/Invoke-SwJob.ps1 scan C:\...\pilot --recursive -SaveTo .swjobs/results/scan-pilot.json

.NOTES
    Пути в аргументах — пути на компьютере с SOLIDWORKS. Исполнитель запускается tools/Start-SwProbe.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Command,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Arguments = @(),
    [string]$Jobs = (Join-Path $PSScriptRoot '..\.swjobs'),
    [int]$TimeoutSec = 900,
    [string]$SaveTo
)
$ErrorActionPreference = 'Stop'
$Jobs = [IO.Path]::GetFullPath($Jobs)
$inbox = Join-Path $Jobs 'inbox'
$outbox = Join-Path $Jobs 'outbox'
$utf8 = New-Object Text.UTF8Encoding($false)

$hbPath = Join-Path $Jobs 'heartbeat.json'
if (Test-Path -LiteralPath $hbPath) {
    $hb = [IO.File]::ReadAllText($hbPath, $utf8) | ConvertFrom-Json
    $age = ((Get-Date) - ([DateTimeOffset]$hb.time).LocalDateTime).TotalSeconds
    if ($hb.stopped -or $age -gt 180) { Write-Warning "Исполнитель, похоже, не работает (heartbeat $([int]$age) с назад по этим часам, stopped=$($hb.stopped)). Запустите tools/Start-SwProbe.ps1 на компьютере с SOLIDWORKS." }
    elseif ($hb.busy) { Write-Host "Исполнитель занят: $($hb.busy)" }
} else {
    Write-Warning "Нет $hbPath — исполнитель не запускался."
}

$id = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), ([guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $inbox | Out-Null
$job = [ordered]@{ id = $id; command = $Command; args = @($Arguments) }
$tmp = Join-Path $inbox "$id.tmp"
[IO.File]::WriteAllText($tmp, ($job | ConvertTo-Json -Depth 3 -Compress), $utf8)
Move-Item -LiteralPath $tmp -Destination (Join-Path $inbox "$id.json")
Write-Host "Задание $id отправлено: $Command $($Arguments -join ' ')"

$result = Join-Path $outbox "$id.json"
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while (-not (Test-Path -LiteralPath $result)) {
    if ((Get-Date) -gt $deadline) { throw "Нет результата задания $id за $TimeoutSec с." }
    Start-Sleep -Milliseconds 700
}
$text = [IO.File]::ReadAllText($result, $utf8)
if ($SaveTo) {
    $dir = Split-Path -Parent ([IO.Path]::GetFullPath($SaveTo))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($SaveTo), $text, $utf8)
}
$text
# Ключи «» (свойства уровня файла) ConvertFrom-Json без -AsHashtable не разбирает — достаём поля регуляркой.
$code = if ($text -match '"exitCode":\s*(-?\d+)') { [int]$Matches[1] } else { 1 }
if ($text -match '(?m)^  "error":\s*"((?:[^"\\]|\\.)*)"') { Write-Warning ([regex]::Unescape($Matches[1])) }
exit $code
