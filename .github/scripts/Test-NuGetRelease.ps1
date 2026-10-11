# Self-contained tests with synthetic archives and a mocked dotnet command. No network.
. "$PSScriptRoot/NuGetRelease.ps1"
$checks = 0
function Assert-Release([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }; $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Release $rejected $Message
}

$plan = Get-NuGetReleasePlan -Version 2.1.2 -Ref refs/tags/v2.1.2 -Event push
Assert-Release $plan.Publish 'A version tag must publish.'
$plan = Get-NuGetReleasePlan -Version 2.1.2 -Ref refs/heads/main -Event workflow_dispatch
Assert-Release (!$plan.Publish) 'Manual branch runs must default to dry run.'
$plan = Get-NuGetReleasePlan -Version 2.1.2 -Ref refs/tags/v2.1.2 -Event workflow_dispatch
Assert-Release (!$plan.Publish) 'Manual tag runs must default to dry run.'
$plan = Get-NuGetReleasePlan -Version 2.1.2 -Ref refs/tags/v2.1.2 -Event workflow_dispatch -PublishRequested $true
Assert-Release $plan.Publish 'An explicitly requested tag retry must publish.'
$plan = Get-NuGetReleasePlan -Version 2.2.0-rc.1 -Ref refs/tags/v2.2.0-rc.1 -Event push
Assert-Release $plan.Publish 'Prerelease tags must be supported.'
Assert-Rejected { Get-NuGetReleasePlan -Version 2.1.2 -Ref refs/tags/v2.1.3 -Event push } 'Reject mismatched version tags.'
Assert-Rejected { Get-NuGetReleasePlan -Version 2.1.2 -Ref refs/heads/main -Event workflow_dispatch -PublishRequested $true } 'Reject branch publication.'
Assert-Rejected { Get-NuGetReleasePlan -Version '2.1.2+build' -Ref refs/heads/main -Event workflow_dispatch } 'Reject unsupported version metadata.'

$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('grpcembed-release-tests-' + [guid]::NewGuid())))
$null = New-Item -ItemType Directory -Path $fixtureRoot
$fixtureCommit = '0123456789012345678901234567890123456789'
$ids = @('DevelKit.GrpcEmbed.Abstractions', 'DevelKit.GrpcEmbed.Core', 'DevelKit.GrpcEmbed.Client', 'DevelKit.GrpcEmbed.AspNetCore', 'DevelKit.GrpcEmbed')
function New-ReleaseFixture([string]$Id, [string]$Extension, [string]$ManifestVersion = '2.1.2', [string]$Commit = $fixtureCommit) {
    $path = Join-Path $fixtureRoot "$Id.2.1.2$Extension"
    $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry("$Id.nuspec")
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $symbolType = if ($Extension -eq '.snupkg') { '<packageTypes><packageType name="SymbolsPackage" /></packageTypes>' } else { '' }
            $writer.Write("<package><metadata><id>$Id</id><version>$ManifestVersion</version><repository commit=`"$Commit`" />$symbolType</metadata></package>")
        } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    return $path
}

$script:pushCalls = [Collections.Generic.List[object]]::new()
function dotnet {
    $script:pushCalls.Add(@($args))
    $global:LASTEXITCODE = 0
}
$savedKey = $env:NUGET_API_KEY
try {
    foreach ($id in $ids) { $null = New-ReleaseFixture $id '.nupkg'; $null = New-ReleaseFixture $id '.snupkg' }
    $artifacts = Test-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit
    Assert-Release ($artifacts.Packages.Count -eq 5 -and $artifacts.Symbols.Count -eq 5) 'Require all five package/symbol pairs.'
    Assert-Rejected { Test-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit 'different' } 'Reject wrong source commits.'
    $env:NUGET_API_KEY = ''
    Push-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit
    Assert-Release ($pushCalls.Count -eq 0) 'Dry run must not invoke dotnet or require credentials.'
    Assert-Rejected { Push-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit -Publish } 'Reject unauthenticated publication.'
    $env:NUGET_API_KEY = 'synthetic-test-key-not-a-credential'
    Push-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit -Publish
    Assert-Release ($pushCalls.Count -eq 10) 'Push each primary and each symbol archive explicitly.'
    for ($i = 0; $i -lt 10; $i++) {
        Assert-Release ($pushCalls[$i][2] -like $(if ($i -lt 5) { '*.nupkg' } else { '*.snupkg' })) 'Push primaries before symbols.'
        Assert-Release ($pushCalls[$i] -contains '--skip-duplicate') 'Retries must tolerate duplicates.'
        Assert-Release ($pushCalls[$i] -contains 'https://api.nuget.org/v3/index.json') 'Use NuGet.org V3 for symbols.'
        if ($i -lt 5) { Assert-Release ($pushCalls[$i] -contains '--no-symbols') 'Avoid implicit symbol upload.' }
    }
    $symbolPath = Join-Path $fixtureRoot 'DevelKit.GrpcEmbed.2.1.2.snupkg'
    Remove-Item -LiteralPath $symbolPath
    Assert-Rejected { Test-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit } 'Reject missing symbols.'
    $null = New-ReleaseFixture 'DevelKit.GrpcEmbed' '.snupkg'
    $mainPath = Join-Path $fixtureRoot 'DevelKit.GrpcEmbed.2.1.2.nupkg'
    Remove-Item -LiteralPath $mainPath
    $null = New-ReleaseFixture 'DevelKit.GrpcEmbed' '.nupkg' '9.0.0'
    Assert-Rejected { Test-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit } 'Reject wrong manifest versions.'
    Remove-Item -LiteralPath $mainPath
    $null = New-ReleaseFixture 'DevelKit.GrpcEmbed' '.nupkg'
    $null = New-ReleaseFixture 'Unexpected.Package' '.nupkg'
    Assert-Rejected { Test-NuGetReleaseArtifacts -Directory $fixtureRoot -Version 2.1.2 -Commit $fixtureCommit } 'Reject unexpected packages.'
    Write-Host "$checks release automation checks passed. No network or NuGet uploads performed."
} finally {
    $env:NUGET_API_KEY = $savedKey
    Remove-Item Function:\dotnet
    # Delete only the exact unique fixture directory created by this test.
    $resolvedFixture = (Resolve-Path -LiteralPath $fixtureRoot).Path
    if ([IO.Path]::GetFullPath($resolvedFixture) -ne $fixtureRoot -or [IO.Path]::GetFileName($fixtureRoot) -notmatch '^grpcembed-release-tests-[a-f0-9-]{36}$') { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
