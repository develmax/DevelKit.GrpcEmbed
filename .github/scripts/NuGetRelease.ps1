# No publishing occurs when this file is sourced. Push requires explicit -Publish.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NuGetReleasePlan {
    param([string]$Version, [string]$Ref, [string]$Event, [bool]$PublishRequested = $false)
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') {
        throw 'Directory.Build.props must contain a release version (major.minor.patch with optional prerelease).'
    }
    $tag = $Ref.StartsWith('refs/tags/')
    if ($tag -and $Ref -cne "refs/tags/v$Version") { throw 'Release tag does not match Directory.Build.props Version.' }
    $publish = $PublishRequested -or ($Event -eq 'push' -and $tag)
    if ($publish -and !$tag) { throw 'Publication requires a version tag; branch runs can only be dry runs.' }
    [pscustomobject]@{ Version = $Version; Publish = $publish }
}

function Test-NuGetReleaseArtifacts {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Commit)
    $ids = @('DevelKit.GrpcEmbed.Abstractions', 'DevelKit.GrpcEmbed.Core', 'DevelKit.GrpcEmbed.Client', 'DevelKit.GrpcEmbed.AspNetCore', 'DevelKit.GrpcEmbed')
    $root = (Resolve-Path -LiteralPath $Directory).Path
    $files = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' })
    if ($files.Count -ne 10) { throw 'Expected exactly five nupkg and five snupkg files.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packages = @(); $symbols = @()
    foreach ($extension in '.nupkg', '.snupkg') {
        foreach ($id in $ids) {
            $path = Join-Path $root "$id.$Version$extension"
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing expected package: $id.$Version$extension" }
            $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
            try {
                $entries = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
                if ($entries.Count -ne 1 -or $entries[0].Length -gt 1MB) { throw 'Invalid package manifest.' }
                $reader = [System.IO.StreamReader]::new($entries[0].Open())
                try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $metadata = $manifest.package.metadata
                if ($metadata.id -cne $id -or $metadata.version -cne $Version) { throw 'Package ID/version mismatch.' }
                if ($extension -eq '.nupkg') {
                    if ($metadata.repository.commit -cne $Commit) { throw 'Package repository commit mismatch.' }
                    $packages += $path
                } else {
                    if ($metadata.packageTypes.packageType.name -ne 'SymbolsPackage') { throw 'Invalid symbol package type.' }
                    $symbols += $path
                }
            } finally { $zip.Dispose() }
        }
    }
    [pscustomobject]@{ Packages = $packages; Symbols = $symbols; Version = $Version; Commit = $Commit }
}

function Push-NuGetReleaseArtifacts {
    param([string]$Directory, [string]$Version, [string]$Commit, [switch]$Publish)
    $artifacts = Test-NuGetReleaseArtifacts -Directory $Directory -Version $Version -Commit $Commit
    if (!$Publish) {
        foreach ($path in $artifacts.Packages + $artifacts.Symbols) { Write-Host "Dry run: $([IO.Path]::GetFileName($path))" }
        return
    }
    if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) { throw 'NuGet authentication did not supply an API key.' }
    $source = 'https://api.nuget.org/v3/index.json'
    # Explicit symbol upload also repairs a partially published release on a rerun.
    foreach ($path in $artifacts.Packages) {
        & dotnet nuget push $path --source $source --api-key $env:NUGET_API_KEY --no-symbols --skip-duplicate
        if ($LASTEXITCODE -ne 0) { throw "Package upload failed: $([IO.Path]::GetFileName($path))" }
    }
    foreach ($path in $artifacts.Symbols) {
        & dotnet nuget push $path --source $source --api-key $env:NUGET_API_KEY --symbol-api-key $env:NUGET_API_KEY --skip-duplicate
        if ($LASTEXITCODE -ne 0) { throw "Symbol upload failed: $([IO.Path]::GetFileName($path))" }
    }
}
