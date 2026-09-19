[CmdletBinding()]
param(
    [string] $ConsumerRoot = 'tests/package-consumers'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedConsumerRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ConsumerRoot))
[xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
$expectedVersion = [string] $buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
    throw 'Directory.Build.props does not define Version.'
}

$requirements = [ordered]@{
    CoreNet8 = @('Raffinert.Consistency')
    CoreNet10 = @('Raffinert.Consistency')
    EfNet10 = @(
        'Raffinert.Consistency.EntityFrameworkCore',
        'Raffinert.Consistency'
    )
}

foreach ($project in $requirements.Keys) {
    $assetsPath = Join-Path $resolvedConsumerRoot "$project/obj/project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Package assets file does not exist: $assetsPath"
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $resolvedRaffinertPackages = @($assets.libraries.PSObject.Properties.Name |
        Where-Object { $_ -like 'Raffinert.Consistency/*' -or
            $_ -like 'Raffinert.Consistency.EntityFrameworkCore/*' })

    foreach ($packageId in $requirements[$project]) {
        $expectedIdentity = "$packageId/$expectedVersion"
        if ($resolvedRaffinertPackages -notcontains $expectedIdentity) {
            throw "$project did not resolve $expectedIdentity. Resolved: $($resolvedRaffinertPackages -join ', ')"
        }
        Write-Output "$project resolved $expectedIdentity."
    }

    $unexpected = @($resolvedRaffinertPackages | Where-Object { $_ -notlike "*/$expectedVersion" })
    if ($unexpected.Count -ne 0) {
        throw "$project resolved unexpected Raffinert package versions: $($unexpected -join ', ')"
    }
}
