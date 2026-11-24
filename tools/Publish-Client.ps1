<#
.SYNOPSIS
    Публикует клиент SolidBrew PDM (SolidBrew.exe) и командную строку (sbpdm.exe) в одну папку.
.DESCRIPTION
    dotnet publish, Release, win-x64, self-contained: на рабочем месте не нужен установленный .NET.
    Результат — publish\client\ (папка в .gitignore). Папку целиком копируют на рабочее место
    или запускают из общей папки. Прежнее содержимое папки удаляется.
.EXAMPLE
    .\tools\Publish-Client.ps1
#>
[CmdletBinding()]
param(
    [string]$Output,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repo = [IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if (-not $Output) { $Output = Join-Path $repo 'publish\client' }
$Output = [IO.Path]::GetFullPath($Output)

$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

foreach ($project in @('src\SolidBrew.Client\SolidBrew.Client.csproj', 'src\SolidBrew.Cli\SolidBrew.Cli.csproj')) {
    Write-Output "Публикация $project…"
    & $dotnet publish (Join-Path $repo $project) -nologo -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:GenerateDocumentationFile=false -o $Output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project завершился с кодом $LASTEXITCODE." }
}

foreach ($exe in @('SolidBrew.exe', 'sbpdm.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Output $exe))) { throw "В $Output нет $exe." }
}
$version = & (Join-Path $Output 'sbpdm.exe') --version
$size = (Get-ChildItem -LiteralPath $Output -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Output ("Готово: {0} ({1:N0} МБ). {2}" -f $Output, ($size / 1MB), $version)
Write-Output 'На рабочем месте запускайте SolidBrew.exe; адрес сервера — https://<сервер>:8443/, отпечаток сертификата сверьте с администратором.'
