<#
.SYNOPSIS
    Запуск исполнителя заданий SwProbe на компьютере с SOLIDWORKS (в том числе из SSH).

.DESCRIPTION
    Из SSH-сессии (сеанс 0) запущенный SOLIDWORKS недоступен через COM. Скрипт регистрирует задачу
    планировщика SolidBrewSwProbe с LogonType Interactive от имени текущего пользователя: она выполняется
    в сеансе, где пользователь вошёл и где запущен SOLIDWORKS. Пароль не нужен.

    Собранный SwProbe копируется в %LOCALAPPDATA%\SolidBrew\SwProbe\<время> (теневая копия), чтобы
    пересборка с машины разработки не упиралась в заблокированные файлы.

    -Stop кладёт задание stop, ждёт завершения и удаляет задачу.
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot = 'C:\Users\rnasyrov\work\solidworks\solidbrew-plm',
    [switch]$Stop,
    [int]$TimeoutSec = 60
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$taskName = 'SolidBrewSwProbe'
$jobs = Join-Path $ProjectRoot '.swjobs'
$inbox = Join-Path $jobs 'inbox'
$outbox = Join-Path $jobs 'outbox'
$heartbeatPath = Join-Path $jobs 'heartbeat.json'
$dotnet = Join-Path $ProjectRoot '.tools\dotnet\dotnet.exe'
$build = Join-Path $ProjectRoot 'tools\SwProbe\bin\Debug\net10.0-windows'
$shadowRoot = Join-Path $env:LOCALAPPDATA 'SolidBrew\SwProbe'

function Read-Heartbeat {
    if (-not (Test-Path -LiteralPath $heartbeatPath)) { return $null }
    try { return Get-Content -LiteralPath $heartbeatPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

function Test-ProbeAlive {
    $hb = Read-Heartbeat
    if (-not $hb -or $hb.stopped) { return $false }
    $p = Get-Process -Id $hb.pid -ErrorAction SilentlyContinue
    return [bool]($p -and $p.ProcessName -eq 'dotnet')
}

function Stop-Probe {
    if (Test-ProbeAlive) {
        $id = 'stop-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        New-Item -ItemType Directory -Force -Path $inbox | Out-Null
        $tmp = Join-Path $inbox "$id.tmp"
        [IO.File]::WriteAllText($tmp, "{`"id`":`"$id`",`"command`":`"stop`",`"args`":[]}", (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tmp -Destination (Join-Path $inbox "$id.json") -Force
        Write-Host "Отправлено задание остановки $id, жду до $TimeoutSec с..."
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-Date) -lt $deadline -and (Test-ProbeAlive)) { Start-Sleep -Milliseconds 500 }
        if (Test-ProbeAlive) {
            $hb = Read-Heartbeat
            Write-Warning "Исполнитель не остановился за $TimeoutSec с (занят: $($hb.busy)). Завершаю процесс $($hb.pid)."
            Stop-Process -Id $hb.pid -Force
        } else {
            Write-Host 'Исполнитель остановлен.'
        }
    }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Задача $taskName удалена."
    }
}

if ($Stop) {
    Stop-Probe
    return
}

if (-not (Test-Path -LiteralPath $dotnet)) { throw "Не найден $dotnet." }
if (-not (Test-Path -LiteralPath (Join-Path $build 'SwProbe.dll'))) { throw "Не найден собранный SwProbe: $build. Соберите решение на машине разработки." }

# Перезапуск: остановить предыдущий экземпляр.
Stop-Probe

# Теневая копия сборки.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dest = Join-Path $shadowRoot $stamp
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $build '*') -Destination $dest -Recurse -Force
Get-ChildItem -LiteralPath $shadowRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip 3 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

New-Item -ItemType Directory -Force -Path $inbox, $outbox | Out-Null

# Задача в сеансе пользователя. Окно консоли — свёрнутое (start /min), задача завершается сразу.
$dll = Join-Path $dest 'SwProbe.dll'
$argument = "/c start `"SolidBrew SwProbe`" /min `"$dotnet`" `"$dll`" serve --jobs `"$jobs`""
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument $argument -WorkingDirectory $dest
$user = if ($env:USERDOMAIN) { "$env:USERDOMAIN\$env:USERNAME" } else { $env:USERNAME }
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
$started = Get-Date
Start-ScheduledTask -TaskName $taskName
Write-Host "Задача $taskName запущена ($user), программа: $dest"

# Дождаться свежего heartbeat.
$deadline = $started.AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    $hb = Read-Heartbeat
    if ($hb -and -not $hb.stopped -and ([DateTimeOffset]$hb.startedAt).LocalDateTime -ge $started.AddSeconds(-2)) {
        Write-Host ("Исполнитель работает: pid {0}, сеанс {1}, SOLIDWORKS {2}{3}" -f $hb.pid, $hb.sessionId, $hb.swRevision, $(if ($hb.swError) { ", ошибка: $($hb.swError)" } else { '' }))
        return
    }
    Start-Sleep -Milliseconds 500
}
throw "Исполнитель не ответил за $TimeoutSec с. См. $jobs\serve.log и журнал задачи $taskName."
