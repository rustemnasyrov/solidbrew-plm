<#
.SYNOPSIS
    Выполняет одно задание из .swjobs/session на компьютере с SOLIDWORKS — в интерактивной сессии пользователя.
    Запускается задачей планировщика, которую создаёт tools/Invoke-InUserSession.ps1. Вручную не вызывается.
#>
param([Parameter(Mandatory = $true)][string]$Job)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = New-Object Text.UTF8Encoding($false)
$utf8 = New-Object Text.UTF8Encoding($false)
$spec = [IO.File]::ReadAllText($Job, $utf8) | ConvertFrom-Json
$out = [IO.Path]::ChangeExtension($Job, '.out.txt')
$done = [IO.Path]::ChangeExtension($Job, '.exit')
$code = 1
try {
    if ($spec.cwd) { Set-Location -LiteralPath $spec.cwd }
    $argList = @($spec.args)
    if ($spec.script) {
        # Скрипт PowerShell выполняется в этом же процессе (без вложенного powershell.exe и его CLIXML в потоке ошибок).
        $global:LASTEXITCODE = 0
        & ([scriptblock]::Create([string]$spec.script)) *> $out
    } elseif ($null -ne $spec.stdin) {
        $text = ($spec.stdin | Out-String) -replace "`r?`n$", ''
        $text | & $spec.exe @argList *> $out
    } else {
        & $spec.exe @argList *> $out
    }
    $code = $LASTEXITCODE
} catch {
    Add-Content -LiteralPath $out -Value ("ОШИБКА ЗАПУСКА: " + $_.Exception.Message) -Encoding UTF8
} finally {
    [IO.File]::WriteAllText($done, [string]$code, $utf8)
}
