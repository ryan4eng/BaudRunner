[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$BumpVersionOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "BaudRunner\BaudRunner.csproj"
$publishRoot = Join-Path $repositoryRoot "publish"
$isGitHub = $env:GITHUB_ACTIONS -eq "true"
$isTaggedRelease = $isGitHub -and $env:GITHUB_EVENT_NAME -eq "push" -and $env:GITHUB_REF_TYPE -eq "tag"

$propsPath = Join-Path $repositoryRoot "Directory.Build.props"
if (-not (Test-Path -LiteralPath $propsPath)) { throw "Version properties file was not found: $propsPath" }

if ($BumpVersionOnly) {
    if (-not $isTaggedRelease) { throw "Version bumps are restricted to tagged GitHub Actions release runs." }
    [xml]$bumpProps = Get-Content $propsPath
    $versionNode = $bumpProps.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
    if (-not $versionNode) { throw "<VersionPrefix> was not found in $propsPath" }
    $parts = $versionNode.InnerText.Split('.')
    if ($parts.Count -ne 3 -or $parts[0] -ne '2') { throw "VersionPrefix '$versionNode' must be a three-part v2 version." }
    $currentVersion = $versionNode.InnerText
    $parts[2] = ([int]$parts[2] + 1).ToString()
    $nextVersion = $parts -join '.'
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $propsText = [System.IO.File]::ReadAllText($propsPath, $utf8)
    $propsText = $propsText -replace '(<VersionPrefix>)[^<]+(</VersionPrefix>)', "`${1}$nextVersion`${2}"
    $propsText = $propsText -replace '(<AssemblyVersion>)[^<]+(</AssemblyVersion>)', "`${1}$nextVersion.0`${2}"
    $propsText = $propsText -replace '(<FileVersion>)[^<]+(</FileVersion>)', "`${1}$nextVersion.0`${2}"
    [System.IO.File]::WriteAllText($propsPath, $propsText, $utf8)
    Write-Host "Bumped Directory.Build.props: $currentVersion -> $nextVersion" -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $project)) { throw "Project file was not found: $project" }

[xml]$props = Get-Content $propsPath
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
if (-not $versionNode) { throw "<VersionPrefix> was not found in $propsPath" }
$baseVersion = $versionNode.InnerText
if ($baseVersion -notmatch '^2\.\d+\.\d+$') { throw "VersionPrefix '$baseVersion' must be a three-part v2 version." }

if ($isTaggedRelease) {
    $tag = $env:GITHUB_REF_NAME
    if ($tag -notmatch '^release/v2\.\d+\.\d+$') { throw "Release tag '$tag' must match release/v2.x.y." }
    $versionTag = $tag.Substring("release/".Length)
    $tagVersion = $versionTag.Substring(1)
    if ($tagVersion -ne $baseVersion) { throw "Tag version $tagVersion does not match Directory.Build.props version $baseVersion." }
    $mode = "release"
    $releaseDirectory = Join-Path $repositoryRoot "release\$versionTag"
    $stagingDirectory = Join-Path $publishRoot "release-staging\$versionTag"
    $versionSuffix = ""
} else {
    $statePath = Join-Path $publishRoot "release-candidate.json"
    $candidateNumber = 1
    if (Test-Path -LiteralPath $statePath) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            if ($state.BaseVersion -eq $baseVersion) { $candidateNumber = [int]$state.Number + 1 }
        } catch { $candidateNumber = 1 }
    }
    $versionTag = "v$baseVersion-rc$candidateNumber"
    $mode = "release-candidate"
    $releaseDirectory = Join-Path $publishRoot "release-candidates\$versionTag"
    $stagingDirectory = Join-Path $publishRoot "release-staging\$versionTag"
    $versionSuffix = "rc$candidateNumber"
}

if (Test-Path -LiteralPath $releaseDirectory) { throw "Output directory already exists: $releaseDirectory" }
if (Test-Path -LiteralPath $stagingDirectory) { throw "Staging directory already exists: $stagingDirectory" }

$builds = @(
    @{ Name = "windows-framework-dependent"; Runtime = "win-x64"; SelfContained = $false; Application = "BaudRunner.exe" },
    @{ Name = "windows-self-contained"; Runtime = "win-x64"; SelfContained = $true; Application = "BaudRunner.exe" },
    @{ Name = "linux-framework-dependent"; Runtime = "linux-x64"; SelfContained = $false; Application = "BaudRunner" },
    @{ Name = "linux-self-contained"; Runtime = "linux-x64"; SelfContained = $true; Application = "BaudRunner" }
)

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

try {
    foreach ($build in $builds) {
        $output = Join-Path $stagingDirectory $build.Name
        $zip = Join-Path $releaseDirectory "BaudRunner-$versionTag-$($build.Name).zip"
        $selfContainedText = $build.SelfContained.ToString().ToLowerInvariant()

        Write-Host "Publishing $($build.Name) as $versionTag..."
        $arguments = @(
            $project,
            "--configuration", $Configuration,
            "--runtime", $build.Runtime,
            "--self-contained:$selfContainedText",
            "--output", $output,
            "-p:VersionPrefix=$baseVersion",
            "-p:VersionSuffix=$versionSuffix",
            "-p:InformationalVersion=$versionTag"
        )
        if ($NoRestore.IsPresent) { $arguments += "--no-restore" }
        & dotnet publish @arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($build.Name) with exit code $LASTEXITCODE." }

        $application = Join-Path $output $build.Application
        if (-not (Test-Path -LiteralPath $application)) { throw "Expected application not found: $application" }
        Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal
        Write-Host ("Created {0} ({1:N0} bytes)" -f $zip, (Get-Item -LiteralPath $zip).Length) -ForegroundColor Green
    }

    if ($mode -eq "release-candidate") {
        $state = @{ BaseVersion = $baseVersion; Number = $candidateNumber; Version = $versionTag } | ConvertTo-Json
        [System.IO.File]::WriteAllText($statePath, $state)
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force }
}

$relativeDirectory = $releaseDirectory.Substring($repositoryRoot.Length).TrimStart('\', '/') -replace '\\', '/'
Write-Host ""
Write-Host "Publish complete: $versionTag ($mode)" -ForegroundColor Green
Write-Host "Output: $releaseDirectory"

if ($env:GITHUB_OUTPUT) {
    "version=$versionTag" >> $env:GITHUB_OUTPUT
    "mode=$mode" >> $env:GITHUB_OUTPUT
    "artifact_directory=$relativeDirectory" >> $env:GITHUB_OUTPUT
}
