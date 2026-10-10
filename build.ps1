[CmdletBinding()]
param([string]$InnoCompiler)

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$appProject = Join-Path $projectDirectory "ApolloMicBridge.csproj"
$testProject = Join-Path $projectDirectory "tests\ApolloMicBridge.Tests.csproj"
$releaseDirectory = Join-Path $projectDirectory "dist\win-x64"
$distDirectory = Join-Path $projectDirectory "dist"
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

if (-not $InnoCompiler) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compilerCandidates = @(
        $compilerCommand.Source,
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe")
    )
    $InnoCompiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw "Inno Setup ISCC.exe not found. Pass -InnoCompiler with its absolute path."
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
& $dotnet restore $testProject --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed." }
& $dotnet run --project $testProject --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Audio and settings checks failed." }
& $dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained true --no-restore --nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false --output $releaseDirectory
if ($LASTEXITCODE -ne 0) { throw "Windows executable publish failed." }

$noticePath = Join-Path $releaseDirectory "THIRD-PARTY-NOTICES.txt"
Copy-Item -LiteralPath (Join-Path $projectDirectory "THIRD-PARTY-NOTICES.txt") -Destination $noticePath
$assets = Get-Content -LiteralPath (Join-Path $projectDirectory "obj\project.assets.json") -Raw | ConvertFrom-Json
$packageFolders = $assets.packageFolders.PSObject.Properties.Name
foreach ($runtimePackage in @("microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64")) {
    $runtimeDirectory = $packageFolders | ForEach-Object { Join-Path $_ "$runtimePackage\10.0.9" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $runtimeDirectory) { throw "Runtime package license folder not found: $runtimePackage" }
    $runtimeNotices = Get-ChildItem -LiteralPath $runtimeDirectory -File |
        Where-Object { $_.Name -in @("LICENSE", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT") }
    if (-not ($runtimeNotices | Where-Object { $_.Name -in @("LICENSE", "LICENSE.TXT") })) {
        throw "Runtime license missing: $runtimeDirectory"
    }
    foreach ($runtimeNotice in $runtimeNotices) {
        Add-Content -LiteralPath $noticePath -Value "`r`n$runtimePackage 10.0.9 - $($runtimeNotice.Name)`r`n" -Encoding UTF8
        Get-Content -LiteralPath $runtimeNotice.FullName -Raw -Encoding UTF8 | Add-Content -LiteralPath $noticePath -Encoding UTF8
    }
}

& $InnoCompiler (Join-Path $projectDirectory "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
$portableFiles = @("ApolloMicBridge.exe", "THIRD-PARTY-NOTICES.txt") |
    ForEach-Object { Join-Path $releaseDirectory $_ }
$portableZip = Join-Path $distDirectory "ApolloMicBridge-Portable-win-x64.zip"
Compress-Archive -LiteralPath $portableFiles -DestinationPath $portableZip -Force
Get-FileHash -LiteralPath (Join-Path $distDirectory "ApolloMicBridge-Setup-0.1.2.exe"), $portableZip -Algorithm SHA256 |
    Format-List Path, Hash
