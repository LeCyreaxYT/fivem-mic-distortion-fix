[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $projectDirectory "ApolloMicBridge.csproj"
$appVersion = ([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version
if ($Tag -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Release tags must use numeric semantic versions without v: $Tag"
}
if ($Tag -ne $appVersion) {
    throw "Tag $Tag does not match project version $appVersion."
}
if ($ValidateOnly) {
    Write-Host "Release tag matches project version: $Tag"
    return
}
if (-not $env:GITHUB_REPOSITORY) {
    throw "GITHUB_REPOSITORY is required to publish a release."
}

$packagePaths = @(
    (Join-Path $projectDirectory "dist/ApolloMicBridge-Setup-$Tag.exe"),
    (Join-Path $projectDirectory "dist/ApolloMicBridge-Portable-win-x64.zip")
)
$assetPaths = foreach ($packagePath in $packagePaths) {
    $package = Get-Item -LiteralPath $packagePath
    if ($package.Length -eq 0) { throw "Release package is empty: $packagePath" }
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$packagePath.sha256" -Value "$hash  $($package.Name)" -Encoding ascii
    $packagePath
    "$packagePath.sha256"
}

function Get-GitHubRelease {
    # The release-by-tag endpoint excludes drafts. List releases with write access
    # to find interrupted drafts too, and filter each page to this numeric tag.
    $query = '.[] | select(.tag_name == "' + $Tag + '")'
    $response = & gh api "repos/$env:GITHUB_REPOSITORY/releases?per_page=100" --paginate --jq $query 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Cannot read release $Tag`: $($response -join ' ')" }
    if ($response) { ($response -join "`n") | ConvertFrom-Json }
}
$release = Get-GitHubRelease
if ($release -and -not $release.draft) {
    Write-Host "Release $Tag is already published; its assets and notes are preserved."
    return
}

if (-not $release) {
    $createArguments = @(
        "release", "create", $Tag, "--repo", $env:GITHUB_REPOSITORY,
        "--verify-tag", "--draft", "--generate-notes", "--title", $Tag
    )
    $notesPath = Join-Path $projectDirectory ".github/release-notes/$Tag.txt"
    if (Test-Path -LiteralPath $notesPath) { $createArguments += @("--notes-file", $notesPath) }
    & gh @createArguments
    if ($LASTEXITCODE -ne 0) { throw "Cannot create draft release $Tag." }
}

# Only drafts reach this upload, so a retry cannot replace published packages.
& gh release upload $Tag @assetPaths --repo $env:GITHUB_REPOSITORY --clobber
if ($LASTEXITCODE -ne 0) { throw "Cannot upload release packages; release $Tag remains a draft." }
$release = Get-GitHubRelease
if (-not $release -or -not $release.draft) { throw "Expected draft release $Tag before publication." }
foreach ($assetPath in $assetPaths) {
    $file = Get-Item -LiteralPath $assetPath
    $assets = @($release.assets | Where-Object name -eq $file.Name)
    $digest = "sha256:" + (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($assets.Count -ne 1 -or $assets[0].size -ne $file.Length -or $assets[0].digest -ne $digest) {
        throw "Uploaded asset failed size or SHA-256 verification: $($file.Name). Release remains a draft."
    }
}
& gh release edit $Tag --repo $env:GITHUB_REPOSITORY --draft=false --prerelease=false --latest
if ($LASTEXITCODE -ne 0) { throw "Cannot publish verified release $Tag." }
Write-Host "Published release $Tag with verified setup, portable ZIP and SHA-256 files."
