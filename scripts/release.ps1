[CmdletBinding()]
param(
    [string]$Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "BaudRunner\BaudRunner.csproj"
$releaseRoot = Join-Path $repositoryRoot "release"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file was not found: $project"
}

$headTag = (& git tag --points-at HEAD 2>$null | Where-Object { $_ -like "v*" } | Select-Object -First 1)
if ($headTag) { $headTag = $headTag.ToString().Trim() }
if (-not $Tag) { $Tag = $headTag }
if (-not $Tag) { throw "HEAD must have an exact release tag, for example v2.0.0." }
if ($Tag -ne $headTag) { throw "The requested tag '$Tag' is not the exact tag on HEAD ('$headTag')." }
if ($Tag -notmatch '^v2\.\d+\.\d+$') { throw "Release tag '$Tag' must match v2.x.y and must not contain -dev." }

$version = $Tag.Substring(1)
$releaseDirectory = Join-Path $releaseRoot $Tag
$stagingDirectory = Join-Path $repositoryRoot "publish\release-staging\$Tag"

if (Test-Path -LiteralPath $releaseDirectory) {
    throw "Release directory already exists: $releaseDirectory"
}
if (Test-Path -LiteralPath $stagingDirectory) {
    throw "Staging directory already exists: $stagingDirectory"
}

$builds = @(
    @{ Name = "windows-framework-dependent"; Runtime = "win-x64"; SelfContained = $false; Application = "BaudRunner.exe" },
    @{ Name = "windows-self-contained"; Runtime = "win-x64"; SelfContained = $true; Application = "BaudRunner.exe" },
    @{ Name = "linux-framework-dependent"; Runtime = "linux-x64"; SelfContained = $false; Application = "BaudRunner" },
    @{ Name = "linux-self-contained"; Runtime = "linux-x64"; SelfContained = $true; Application = "BaudRunner" }
)

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
    foreach ($build in $builds) {
        $output = Join-Path $stagingDirectory $build.Name
        $zip = Join-Path $releaseDirectory "BaudRunner-$Tag-$($build.Name).zip"
        $selfContainedText = $build.SelfContained.ToString().ToLowerInvariant()

        Write-Host "Publishing $($build.Name) from tag $Tag..."
        $arguments = @(
            $project,
            "--configuration", "Release",
            "--runtime", $build.Runtime,
            "--self-contained:$selfContainedText",
            "--output", $output,
            "-p:VersionPrefix=$version",
            "-p:VersionSuffix=",
            "-p:InformationalVersion=$Tag"
        )
        & dotnet publish @arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($build.Name) with exit code $LASTEXITCODE." }

        $application = Join-Path $output $build.Application
        if (-not (Test-Path -LiteralPath $application)) {
            throw "Expected published application not found: $application"
        }

        Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -CompressionLevel Optimal
        $size = (Get-Item -LiteralPath $zip).Length
        Write-Host ("Created {0} ({1:N0} bytes)" -f $zip, $size) -ForegroundColor Green
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

Write-Host ""
Write-Host "Release complete: $releaseDirectory" -ForegroundColor Green
Get-ChildItem -LiteralPath $releaseDirectory -Filter "*.zip" | Select-Object -ExpandProperty FullName
