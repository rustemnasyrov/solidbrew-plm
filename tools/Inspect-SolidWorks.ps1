[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string[]]$EntryFiles,
    [string]$Output = 'artifacts/solidworks-references.json',
    [string]$InteropPath
)
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $Source).FullName.TrimEnd('\') + '\'
if (-not $InteropPath) {
    $process = Get-Process -Name SLDWORKS | Select-Object -First 1
    if (-not $process.Path) { throw 'Cannot locate the running SOLIDWORKS executable. Pass -InteropPath.' }
    $install = Split-Path $process.Path -Parent
    $InteropPath = @((Join-Path $install 'api\redist\SolidWorks.Interop.sldworks.dll'), (Join-Path $install 'SolidWorks.Interop.sldworks.dll')) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $InteropPath) { throw 'SOLIDWORKS interop DLL not found. Pass -InteropPath.' }
}
$InteropPath = (Get-Item -LiteralPath $InteropPath).FullName
if (-not ('SolidBrew.DependencyReader' -as [type])) {
    [void][Reflection.Assembly]::LoadFrom($InteropPath)
    Add-Type -ReferencedAssemblies $InteropPath -TypeDefinition @'
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
namespace SolidBrew {
    public static class DependencyReader {
        public static string Revision(object app) { return ((ISldWorks)app).RevisionNumber(); }
        public static string[] Read(object app, string path) {
            var result = new List<string>();
            var values = ((ISldWorks)app).GetDocumentDependencies2(path, true, false, false) as Array;
            if (values != null) foreach (var value in values) result.Add(Convert.ToString(value));
            return result.ToArray();
        }
    }
}
'@
}
$sw = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
$revision = [SolidBrew.DependencyReader]::Revision($sw)
$byName = @{}
foreach ($local in Get-ChildItem -LiteralPath $root -File -Recurse) {
    if (-not $byName.ContainsKey($local.Name)) { $byName[$local.Name] = [Collections.Generic.List[string]]::new() }
    $byName[$local.Name].Add($local.FullName.Substring($root.Length))
}
$reports = foreach ($entry in $EntryFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ('Invalid entry file: ' + $entry)
    }
    $raw = @([SolidBrew.DependencyReader]::Read($sw, $path))
    if ($raw.Count % 2 -ne 0) { throw ('Unexpected SOLIDWORKS dependency result for ' + $entry) }
    $references = for ($i = 0; $i -lt $raw.Count; $i += 2) {
        $name = $raw[$i]
        $saved = $raw[$i + 1]
        $savedFull = $null
        $candidates = @()
        if ($saved) {
            try {
                $leaf = [IO.Path]::GetFileName($saved)
                if ($leaf -and $byName.ContainsKey($leaf)) { $candidates = @($byName[$leaf].ToArray()) }
                $savedFull = if ([IO.Path]::IsPathRooted($saved)) { [IO.Path]::GetFullPath($saved) } else { [IO.Path]::GetFullPath((Join-Path (Split-Path $path -Parent) $saved)) }
            } catch { $savedFull = $null }
        }
        $inRoot = $savedFull -and $savedFull.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
        $state = if ($inRoot -and (Test-Path -LiteralPath $savedFull -PathType Leaf)) { 'saved-path-in-package' }
                 elseif ($candidates.Count -eq 1) { 'candidate-by-name' }
                 elseif ($candidates.Count -gt 1) { 'ambiguous-name' }
                 elseif ($savedFull -and -not $inRoot) { 'outside-package' }
                 else { 'unresolved' }
        [pscustomobject]@{ Name = $name; SavedPath = $saved; State = $state; LocalCandidates = $candidates }
    }
    [pscustomobject]@{ File = $entry; Count = @($references).Count; States = @($references | Where-Object { $_ } | Group-Object State | Select-Object Name,Count); References = @($references | Where-Object { $_ }) }
}
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($Output)))
[pscustomobject]@{ SolidWorksRevision = $revision; Mode = 'saved paths, traversal requested, CAD open not tested'; Source = $root; Entries = @($reports) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding UTF8
$reports | Select-Object File,Count,States | ConvertTo-Json -Depth 5
