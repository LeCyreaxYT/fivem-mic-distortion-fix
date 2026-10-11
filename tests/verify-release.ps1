[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path $PSScriptRoot -Parent
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectDirectory "ApolloMicBridge.csproj") -Raw)).Project.PropertyGroup.Version
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ApolloMicBridge-release-tests-" + [Guid]::NewGuid())
$fixtureScripts = Join-Path $fixtureDirectory ".github/scripts"
$fixtureDist = Join-Path $fixtureDirectory "dist"
New-Item -ItemType Directory -Path $fixtureScripts, $fixtureDist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDirectory ".github/scripts/publish-release.ps1") -Destination $fixtureScripts
Set-Content -LiteralPath (Join-Path $fixtureDirectory "ApolloMicBridge.csproj") -Value "<Project><PropertyGroup><Version>$appVersion</Version></PropertyGroup></Project>"
$publisher = Join-Path $fixtureScripts "publish-release.ps1"
$setupPath = Join-Path $fixtureDist "ApolloMicBridge-Setup-$appVersion.exe"
$portablePath = Join-Path $fixtureDist "ApolloMicBridge-Portable-win-x64.zip"
$previousRepository = $env:GITHUB_REPOSITORY
$env:GITHUB_REPOSITORY = "fixture/repository"

function Assert-ReleaseCheck([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function global:gh {
    $arguments = @($args)
    $global:releaseTestState.Calls.Add($arguments) | Out-Null
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq "api") {
        Assert-ReleaseCheck ($arguments[1] -eq 'repos/fixture/repository/releases?per_page=100') "Drafts must be read through the release list."
        Assert-ReleaseCheck ($arguments -contains '--paginate') "Release lookup must cover every page."
        $query = $arguments[$arguments.IndexOf('--jq') + 1]
        Assert-ReleaseCheck ($query -eq ('.[] | select(.tag_name == "' + $appVersion + '")')) "Release lookup must filter by the exact tag."
        if ($global:releaseTestState.QueryFailure) {
            $global:LASTEXITCODE = 1
            return "gh: Bad credentials (HTTP 401)"
        }
        if (-not $global:releaseTestState.Release) { return }
        return ($global:releaseTestState.Release | ConvertTo-Json -Depth 5)
    }
    switch ($arguments[1]) {
        "create" {
            Assert-ReleaseCheck ($arguments -contains "--draft") "Release must be created as a draft."
            Assert-ReleaseCheck ($arguments -contains "--verify-tag") "Release creation must verify the pushed tag."
            $global:releaseTestState.Release = @{ draft = $true; assets = @(); body = "Generated notes" }
        }
        "upload" {
            Assert-ReleaseCheck $global:releaseTestState.Release.draft "Cannot replace assets in a published release."
            if ($global:releaseTestState.UploadFailure) {
                $global:LASTEXITCODE = 1
                return "Upload failed"
            }
            $files = $arguments[3..($arguments.IndexOf("--repo") - 1)]
            $global:releaseTestState.Release.assets = @($files | ForEach-Object {
                $file = Get-Item -LiteralPath $_
                @{
                    name = $file.Name
                    size = $file.Length
                    digest = "sha256:" + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            })
            if ($global:releaseTestState.BadDigest) { $global:releaseTestState.Release.assets[0].digest = "sha256:invalid" }
        }
        "edit" {
            Assert-ReleaseCheck ($global:releaseTestState.Release.assets.Count -eq 4) "Publish requires both packages and checksum files."
            Assert-ReleaseCheck ($arguments -contains "--draft=false") "Publish must complete the draft."
            Assert-ReleaseCheck ($arguments -contains "--latest") "Numeric releases must be marked latest."
            $global:releaseTestState.Release.draft = $false
        }
        default { throw "Unexpected GitHub CLI command: $($arguments -join ' ')" }
    }
}

function Invoke-ReleaseCase(
    [string]$Name,
    [scriptblock]$Arrange,
    [scriptblock]$Run,
    [string]$ExpectedError,
    [scriptblock]$Verify
) {
    Set-Content -LiteralPath $setupPath -Value "Test setup package"
    Set-Content -LiteralPath $portablePath -Value "Test portable package"
    $global:releaseTestState = @{
        Calls = [Collections.Generic.List[object]]::new()
        Release = $null
        QueryFailure = $false
        UploadFailure = $false
        BadDigest = $false
    }
    if ($Arrange) { & $Arrange }
    $failure = $null
    try { & $Run } catch { $failure = $_.Exception.Message }
    if ($ExpectedError) {
        Assert-ReleaseCheck ($failure -like "*$ExpectedError*") "Case '$Name' expected '$ExpectedError', got '$failure'."
    } else {
        Assert-ReleaseCheck (-not $failure) "Case '$Name' failed: $failure"
    }
    if ($Verify) { & $Verify }
    Write-Host "PASS $Name"
}

try {
    Invoke-ReleaseCase "Matching tag validates without GitHub access" -Run {
        & $publisher -Tag $appVersion -ValidateOnly
    } -Verify { Assert-ReleaseCheck ($global:releaseTestState.Calls.Count -eq 0) "Validation must not call GitHub." }

    Invoke-ReleaseCase "Mismatched tag cannot publish" -Run {
        & $publisher -Tag (([int]$appVersion.Split('.')[0] + 1).ToString() + ".0.0")
    } -ExpectedError "does not match project version" -Verify {
        Assert-ReleaseCheck ($global:releaseTestState.Calls.Count -eq 0) "Mismatched tags must fail before GitHub access."
    }

    foreach ($invalidTag in @("v$appVersion", "$appVersion-beta.1", "main")) {
        Invoke-ReleaseCase "Invalid release tag $invalidTag" -Run {
            & $publisher -Tag $invalidTag
        } -ExpectedError "numeric semantic versions without v"
    }

    Invoke-ReleaseCase "New release uploads and verifies all assets before publishing" -Run {
        & $publisher -Tag $appVersion
    } -Verify {
        Assert-ReleaseCheck (-not $global:releaseTestState.Release.draft) "New release must be published."
        $callOrder = ($global:releaseTestState.Calls | ForEach-Object { $_[0..1] -join ' ' }) -join ','
        Assert-ReleaseCheck ($callOrder -eq 'api repos/fixture/repository/releases?per_page=100,release create,release upload,api repos/fixture/repository/releases?per_page=100,release edit') "Unexpected publish order."
        $expectedHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-ReleaseCheck ((Get-Content -LiteralPath "$setupPath.sha256") -eq "$expectedHash  ApolloMicBridge-Setup-$appVersion.exe") "Checksum file must contain its package digest and filename."
    }

    Invoke-ReleaseCase "Published release retries preserve existing assets and notes" -Arrange {
        $global:releaseTestState.Release = @{ draft = $false; assets = @(@{ name = "existing-package" }); body = "Keep these notes" }
    } -Run { & $publisher -Tag $appVersion } -Verify {
        Assert-ReleaseCheck ($global:releaseTestState.Calls.Count -eq 1) "Published release must only be read."
        Assert-ReleaseCheck ($global:releaseTestState.Release.body -eq "Keep these notes") "Existing release notes changed."
    }

    Invoke-ReleaseCase "Interrupted drafts can be completed without recreating notes" -Arrange {
        $global:releaseTestState.Release = @{ draft = $true; assets = @(); body = "Keep draft notes" }
    } -Run { & $publisher -Tag $appVersion } -Verify {
        Assert-ReleaseCheck (-not $global:releaseTestState.Release.draft) "Draft retry must finish publication."
        Assert-ReleaseCheck ($global:releaseTestState.Release.body -eq "Keep draft notes") "Draft notes changed."
        Assert-ReleaseCheck ($global:releaseTestState.Calls.Count -eq 4) "Draft retry must not create another release."
    }

    Invoke-ReleaseCase "Authentication failure cannot be mistaken for a missing release" -Arrange {
        $global:releaseTestState.QueryFailure = $true
    } -Run { & $publisher -Tag $appVersion } -ExpectedError "Cannot read release" -Verify {
        Assert-ReleaseCheck ($global:releaseTestState.Calls.Count -eq 1) "Authentication failure must not create a release."
    }

    foreach ($fault in @("UploadFailure", "BadDigest")) {
        Invoke-ReleaseCase "Failed $fault leaves a draft" -Arrange {
            $global:releaseTestState[$fault] = $true
        } -Run { & $publisher -Tag $appVersion } -ExpectedError "draft" -Verify {
            Assert-ReleaseCheck $global:releaseTestState.Release.draft "Failure must not publish a partial or unverified release."
            Assert-ReleaseCheck (-not ($global:releaseTestState.Calls | Where-Object { $_[1] -eq 'edit' })) "Failure must not call publish."
        }
    }

    Invoke-ReleaseCase "Missing packages fail before creating a draft" -Arrange {
        Remove-Item -LiteralPath $setupPath
    } -Run { & $publisher -Tag $appVersion } -ExpectedError "does not exist" -Verify {
        Assert-ReleaseCheck ($global:releaseTestState.Calls.Count -eq 0) "Missing package must fail before GitHub access."
    }
} finally {
    $env:GITHUB_REPOSITORY = $previousRepository
    Remove-Item -LiteralPath Function:\gh
    Remove-Variable -Name releaseTestState -Scope Global -ErrorAction SilentlyContinue
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureDirectory)
    $tempDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedFixture.StartsWith($tempDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolvedFixture -Leaf) -notmatch '^ApolloMicBridge-release-tests-[0-9a-f-]{36}$') {
        throw "Refusing to remove an unexpected test fixture path: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
