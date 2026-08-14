[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "BaudRunner\BaudRunner.csproj"
$publishRoot = Join-Path $repositoryRoot "publish"
$output = Join-Path $publishRoot $RuntimeIdentifier

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file was not found: $project"
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Write-Host "Publishing $project"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime: $RuntimeIdentifier"
Write-Host "Self-contained: $($SelfContained.IsPresent)"
Write-Host "Restore: $(!$NoRestore.IsPresent)"
Write-Host "Output: $output"

$publishArguments = @(
    $project,
    "--configuration", $Configuration,
    "--runtime", $RuntimeIdentifier,
    "--self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
    "--output", $output
)
if ($NoRestore.IsPresent) { $publishArguments += "--no-restore" }
& dotnet publish @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ($RuntimeIdentifier -eq "win-x64") {
    $application = Join-Path $output "BaudRunner.exe"
} else {
    $application = Join-Path $output "BaudRunner"
}

if (-not (Test-Path -LiteralPath $application)) {
    throw "Publish completed but the expected application was not found: $application"
}

$size = (Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("Publish complete: {0:N0} bytes" -f $size) -ForegroundColor Green
Write-Host "Application: $application"
