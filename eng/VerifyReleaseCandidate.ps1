[CmdletBinding()]
param(
    [string] $PackageDirectory = 'artifacts/packages',
    [switch] $RequireEmptyUnshipped
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedPackages = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageDirectory))
if (-not (Test-Path -LiteralPath $resolvedPackages -PathType Container)) {
    throw "Package directory does not exist: $resolvedPackages"
}

[xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
$expectedVersion = [string] $buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
    throw 'Directory.Build.props does not define Version.'
}

$shippedFiles = @(
    'src/Raffinert.Consistency/PublicAPI.Shipped.txt',
    'src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt'
)
$unshippedFiles = @(
    'src/Raffinert.Consistency/PublicAPI.Unshipped.txt',
    'src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt'
)

foreach ($relativePath in $shippedFiles) {
    $apiLines = @(Get-Content -LiteralPath (Join-Path $repositoryRoot $relativePath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '#nullable enable' })
    if ($apiLines.Count -eq 0) {
        throw "Shipped API baseline has no API entries: $relativePath"
    }
}

if ($RequireEmptyUnshipped) {
    foreach ($relativePath in $unshippedFiles) {
        $apiLines = @(Get-Content -LiteralPath (Join-Path $repositoryRoot $relativePath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '#nullable enable' })
        if ($apiLines.Count -ne 0) {
            throw "The alpha.1 release gate requires an empty unshipped API baseline: $relativePath"
        }
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @(Get-ChildItem -LiteralPath $resolvedPackages -Filter '*.nupkg' |
    Where-Object { $_.Name -notlike '*.snupkg' })
$symbols = @(Get-ChildItem -LiteralPath $resolvedPackages -Filter '*.snupkg')
if ($packages.Count -ne 2) { throw "Expected two NuGet packages; found $($packages.Count)." }
if ($symbols.Count -ne 2) { throw "Expected two symbol packages; found $($symbols.Count)." }

$inspected = @{}
foreach ($package in $packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($archive.Entries.FullName)
        $nuspecEntry = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
        if ($nuspecEntry.Count -ne 1) { throw "$($package.Name) must contain exactly one nuspec." }
        $reader = [IO.StreamReader]::new($nuspecEntry[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        $id = [string] $metadata.id
        if ($inspected.ContainsKey($id)) { throw "Duplicate package ID: $id" }
        if ([string] $metadata.version -ne $expectedVersion) {
            throw "$id version '$($metadata.version)' does not match '$expectedVersion'."
        }
        if ($metadata.license.GetAttribute('type') -ne 'expression' -or $metadata.license.InnerText -ne 'MIT') {
            throw "$id must use the MIT license expression."
        }
        if ($metadata.repository.GetAttribute('url') -ne 'https://github.com/Raffinert/Relations') {
            throw "$id has an unexpected repository URL."
        }
        if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA) -and
            $metadata.repository.GetAttribute('commit') -ne $env:GITHUB_SHA) {
            throw "$id repository commit does not match GITHUB_SHA."
        }
        foreach ($requiredEntry in @('README.md', 'CHANGELOG.md')) {
            if ($entries -notcontains $requiredEntry) { throw "$id has no $requiredEntry." }
        }
        $inspected[$id] = [pscustomobject]@{ Metadata = $metadata; Entries = $entries }
    }
    finally {
        $archive.Dispose()
    }
}

$coreId = 'Raffinert.Consistency'
$efId = 'Raffinert.Consistency.EntityFrameworkCore'
if (-not $inspected.ContainsKey($coreId) -or -not $inspected.ContainsKey($efId)) {
    throw 'Expected core and EntityFrameworkCore package IDs.'
}
foreach ($asset in @('lib/net8.0/Raffinert.Consistency.dll', 'lib/net10.0/Raffinert.Consistency.dll')) {
    if ($inspected[$coreId].Entries -notcontains $asset) { throw "$coreId has no $asset asset." }
}
$efAsset = 'lib/net10.0/Raffinert.Consistency.EntityFrameworkCore.dll'
if ($inspected[$efId].Entries -notcontains $efAsset) { throw "$efId has no $efAsset asset." }

$coreDependency = @($inspected[$efId].Metadata.dependencies.group.dependency |
    Where-Object { $_.GetAttribute('id') -eq $coreId })
if ($coreDependency.Count -ne 1 -or $coreDependency[0].GetAttribute('version') -ne $expectedVersion) {
    throw "$efId must depend on $coreId version $expectedVersion."
}

Write-Output "Release candidate packages are consistent at version $expectedVersion."
