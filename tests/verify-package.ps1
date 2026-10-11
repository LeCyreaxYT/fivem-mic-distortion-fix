[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory, [string]$PreviousSetup)

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$installedDirectory = Join-Path $testDirectory "installed app"
$appFile = Join-Path $installedDirectory "ApolloMicBridge.exe"
$uninstaller = Join-Path $installedDirectory "unins000.exe"
$appVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectDirectory "ApolloMicBridge.csproj") -Raw)).Project.PropertyGroup.Version
$setup = Join-Path $projectDirectory "dist\ApolloMicBridge-Setup-$appVersion.exe"
$firstSetup = if ($PreviousSetup) { [IO.Path]::GetFullPath($PreviousSetup) } else { $setup }
$installKey = "HKCU:\Software\SkySystems\ApolloMicBridge"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{77DCFA70-A21A-43F9-AB44-C6A8D3481997}_is1"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runName = "SkySystemsApolloMicBridge"
$settingsDirectory = Join-Path $env:LOCALAPPDATA "SkySystems\ApolloMicBridge"
$settingsFile = Join-Path $settingsDirectory "settings.json"
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath("Programs")) "SkySystems\PMA Mic Bridge"
$legacyStartMenuDirectory = Join-Path ([Environment]::GetFolderPath("Programs")) "SkySystems\Apollo Mic Bridge"
$checks = [Collections.Generic.List[string]]::new()
$installed = $false
$probeDirectory = Join-Path $testDirectory "update probe"
$updateDirectory = Join-Path $settingsDirectory "Update"

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $checks.Add($Message)
    Write-Output "PASS $Message"
}

function Invoke-Setup([string]$Tasks, [string]$LogName, [string]$SetupFile = $setup) {
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installedDirectory`" /TASKS=`"$Tasks`" /LOG=`"$(Join-Path $testDirectory $LogName)`""
    $process = Start-Process -FilePath $SetupFile -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw "Setup did not finish within 30 seconds; inspect its log before retrying." }
    Assert-Check ($process.ExitCode -eq 0) "Setup exit code is zero ($LogName)"
}

function Test-Updater([string]$Directory, [string]$Package, [bool]$ExpectInstalled) {
    $target = Join-Path $Directory "ApolloMicBridge.exe"
    $report = Join-Path $Directory "update-handoff.json"
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $probeDirectory "ApolloMicBridge.Tests.exe") -Destination $target
    $parent = Start-Process -FilePath $target -ArgumentList "--update-parent `"$Package`" `"$report`"" -WindowStyle Hidden -PassThru
    Assert-Check ($parent.WaitForExit(20000) -and $parent.ExitCode -eq 0) "Update parent hands off and exits (installed=$ExpectInstalled)"
    $handoff = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Assert-Check ($handoff.HelperReady -and $handoff.Installed -eq $ExpectInstalled) "Updater locks launches and selects the correct package type (installed=$ExpectInstalled)"
    $worker = Get-Process -Name ApolloMicBridge.Update -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Join-Path $updateDirectory "ApolloMicBridge.Update.exe") }
    if ($worker) {
        $null = $worker.Handle
        $completed = $worker.WaitForExit(30000)
        Write-Output "Update helper completed=$completed, exit=$($worker.ExitCode)"
        Assert-Check ($completed -and $worker.ExitCode -eq 0) "Update helper completes (installed=$ExpectInstalled)"
    }
    Assert-Check ((Get-FileHash -LiteralPath $target).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $projectDirectory "dist\win-x64\ApolloMicBridge.exe")).Hash) "Updater replaces the executable with the verified release (installed=$ExpectInstalled)"
    $restarted = Get-Process -Name ApolloMicBridge -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -ne $parent.Id -and $_.Path -eq $target }
    Assert-Check ($null -ne $restarted -and $restarted.WaitForInputIdle(10000)) "Updater restarts the application (installed=$ExpectInstalled)"
    $quit = Start-Process -FilePath $target -ArgumentList '--quit' -WindowStyle Hidden -PassThru
    Assert-Check ($quit.WaitForExit(15000) -and $quit.ExitCode -eq 0 -and $restarted.WaitForExit(10000)) "Updated application accepts a graceful shutdown (installed=$ExpectInstalled)"
    Assert-Check ((Get-FileHash -LiteralPath $settingsFile).Hash -eq $settingsHash) "Automatic update preserves audio settings (installed=$ExpectInstalled)"
    Assert-Check ((Get-ItemPropertyValue -LiteralPath $runKey -Name $runName) -ceq $expectedStartup) "Automatic update preserves startup registration (installed=$ExpectInstalled)"
    Assert-Check (-not (Get-ChildItem -LiteralPath $Directory -Filter '*.update-*')) "Portable transaction leaves no temporary replacements (installed=$ExpectInstalled)"
    Assert-Check (-not (Get-ChildItem -LiteralPath $updateDirectory -Filter 'package.*')) "Successful update removes its downloaded package (installed=$ExpectInstalled)"
}

# This test owns a new installation only. Never replace an existing installation or user settings.
if (Test-Path -LiteralPath $installKey) { throw "An existing Apollo Mic Bridge installation must be preserved; package verification skipped." }
if (Test-Path -LiteralPath $uninstallKey) { throw "An existing Apollo Mic Bridge uninstall entry must be preserved." }
if (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue) { throw "An existing Apollo Mic Bridge startup value must be preserved." }
if (Test-Path -LiteralPath $startMenuDirectory) { throw "Existing Apollo Mic Bridge shortcuts must be preserved." }
if (Test-Path -LiteralPath $legacyStartMenuDirectory) { throw "Existing legacy application shortcuts must be preserved." }
if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Apollo Mic Bridge.lnk')) {
    throw "An existing legacy desktop shortcut must be preserved."
}
if ((Test-Path -LiteralPath $settingsDirectory) -and (Get-ChildItem -LiteralPath $settingsDirectory -Force)) {
    throw "Existing Apollo Mic Bridge settings or logs must be preserved."
}
if (Get-Process -Name ApolloMicBridge -ErrorAction SilentlyContinue) { throw "An existing Apollo Mic Bridge process must be preserved." }
if ((Test-Path -LiteralPath $installedDirectory) -and (Get-ChildItem -LiteralPath $installedDirectory -Force)) {
    throw "The requested test installation directory is not empty."
}
if (-not $installedDirectory.StartsWith($testDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test installation path escaped its output directory."
}
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'ApolloMicBridge.Tests.csproj') --configuration Release --runtime win-x64 --self-contained true --nologo `
    -p:RuntimeFrameworkVersion=10.0.9 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:NuGetLockFilePath=obj/packages.probe.lock.json --output $probeDirectory
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the updater process fixture.' }

try {
    Invoke-Setup "autostart" "install.log" $firstSetup
    $installed = $true
    Assert-Check (Test-Path -LiteralPath $appFile) "Installed executable exists"
    Assert-Check (Test-Path -LiteralPath $uninstaller) "Uninstaller exists"
    Assert-Check (Test-Path -LiteralPath $uninstallKey) "Windows Apps uninstall entry exists"
    $expectedStartup = '"' + $appFile + '" --autostart --background'
    Assert-Check ((Get-ItemPropertyValue -LiteralPath $runKey -Name $runName) -ceq $expectedStartup) "Windows startup command quotes paths with spaces"
    $initialStartMenu = if (Test-Path -LiteralPath $startMenuDirectory) { $startMenuDirectory } else { $legacyStartMenuDirectory }
    $initialLinkName = if ($initialStartMenu -eq $startMenuDirectory) { 'PMA Mic Bridge.lnk' } else { 'Apollo Mic Bridge.lnk' }
    Assert-Check (Test-Path -LiteralPath (Join-Path $initialStartMenu $initialLinkName)) "Start menu application shortcut exists"
    Assert-Check (Test-Path -LiteralPath (Join-Path $initialStartMenu "Deinstallieren - Uninstall.lnk")) "Start menu uninstaller shortcut exists"
    $firstInstalledVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appFile).FileVersion

    $smoke = Start-Process -FilePath $appFile -ArgumentList "--smoke-test `"$(Join-Path $testDirectory 'installed-ui.png')`"" -WindowStyle Hidden -PassThru
    Assert-Check ($smoke.WaitForExit(15000) -and $smoke.ExitCode -eq 0) "Installed EXE passes native window, close-to-tray and restore checks"

    # Saved but unavailable devices must never activate another microphone on this computer.
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{ Backend = 0; InputId = "missing-test-input"; CableId = "missing-test-cable"; Channel = 9; Gain = -6; AsioRate = 48000 } |
        ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    $settingsHash = (Get-FileHash -LiteralPath $settingsFile).Hash
    Test-Updater $installedDirectory $setup $true
    Test-Updater (Join-Path $testDirectory 'portable app') (Join-Path $projectDirectory 'dist\ApolloMicBridge-Portable-win-x64.zip') $false
    $backgroundSmoke = Start-Process -FilePath $appFile -ArgumentList "--smoke-test `"$(Join-Path $testDirectory 'background-english-ui.png')`" en-US background" -WindowStyle Hidden -PassThru
    Assert-Check ($backgroundSmoke.WaitForExit(15000) -and $backgroundSmoke.ExitCode -eq 0) "Background startup restores the complete English UI at the display DPI"
    Assert-Check ((Get-ItemPropertyValue -LiteralPath $uninstallKey -Name DisplayName) -like 'PMA Mic Bridge*') "Windows Apps displays PMA Mic Bridge"
    $running = Start-Process -FilePath $appFile -ArgumentList "--autostart --background" -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    Assert-Check (-not $running.HasExited) "Background startup stays running while saved devices are unavailable"
    $running.Refresh()
    Assert-Check ($running.MainWindowHandle -eq [IntPtr]::Zero) "Configured background startup does not show a window"

    $cpuStart = $running.TotalProcessorTime
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 20
    $elapsed.Stop()
    $running.Refresh()
    $idleCpuMs = ($running.TotalProcessorTime - $cpuStart).TotalMilliseconds
    $idleCorePercent = $idleCpuMs / $elapsed.Elapsed.TotalMilliseconds * 100
    Assert-Check (-not $running.HasExited) "Background application remains alive during the idle measurement"

    $second = Start-Process -FilePath $appFile -WindowStyle Hidden -PassThru
    Assert-Check ($second.WaitForExit(10000) -and $second.ExitCode -eq 0) "Second launch reuses the existing instance"
    $restoreWait = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $running.Refresh()
    } while ($running.MainWindowHandle -eq [IntPtr]::Zero -and $restoreWait.ElapsedMilliseconds -lt 5000)
    Assert-Check ($running.MainWindowHandle -ne [IntPtr]::Zero) "Second launch restores the existing window"

    # Updating must respect autostart disabled in the application, and stop its running instance.
    Remove-ItemProperty -LiteralPath $runKey -Name $runName
    Invoke-Setup "" "upgrade.log"
    Assert-Check ($running.WaitForExit(10000)) "Installer gracefully stops the running application before updating"
    Assert-Check ((Get-FileHash -LiteralPath $appFile).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $projectDirectory "dist\win-x64\ApolloMicBridge.exe")).Hash) "Updated binary matches the release binary"
    Assert-Check ((Get-FileHash -LiteralPath $settingsFile).Hash -eq $settingsHash) "Updating preserves saved audio settings"
    Assert-Check (-not (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue)) "Updating keeps disabled autostart disabled"

    $legacySmoke = Start-Process -FilePath $appFile -ArgumentList "--smoke-test `"$(Join-Path $testDirectory 'upgraded-legacy-ui.png')`"" -WindowStyle Hidden -PassThru
    Assert-Check ($legacySmoke.WaitForExit(15000) -and $legacySmoke.ExitCode -eq 0) "Upgraded application loads legacy settings and exposes the cable output option"
    $savedSettings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    $savedSettings | Add-Member -NotePropertyName ExclusiveOutput -NotePropertyValue $true -Force
    $savedSettings | Add-Member -NotePropertyName OutputBits -NotePropertyValue 24 -Force
    $savedSettings | ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    $exclusiveSmoke = Start-Process -FilePath $appFile -ArgumentList "--smoke-test `"$(Join-Path $testDirectory 'exclusive-mode-ui.png')`"" -WindowStyle Hidden -PassThru
    Assert-Check ($exclusiveSmoke.WaitForExit(15000) -and $exclusiveSmoke.ExitCode -eq 0) "Saved exclusive output mode and 24-bit selection are restored in the native controls"

    # Simulate enabling the app's startup option after an installation without the startup task.
    Set-ItemProperty -LiteralPath $runKey -Name $runName -Value $expectedStartup
    $running = Start-Process -FilePath $appFile -ArgumentList "--autostart --background" -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    $remove = Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$(Join-Path $testDirectory 'uninstall.log')`"" -WindowStyle Hidden -PassThru
    Assert-Check ($remove.WaitForExit(30000) -and $remove.ExitCode -eq 0) "Uninstaller exits successfully with the app running"
    Assert-Check ($running.WaitForExit(10000)) "Uninstaller gracefully stops the application"
    $installed = $false
    Assert-Check (-not (Test-Path -LiteralPath $appFile)) "Uninstaller removes the application binary"
    Assert-Check (-not (Test-Path -LiteralPath $installKey)) "Uninstaller removes its install registration"
    Assert-Check (-not (Test-Path -LiteralPath $uninstallKey)) "Uninstaller removes the Windows Apps entry"
    Assert-Check (-not (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue)) "Uninstaller removes autostart enabled after installation"
    Assert-Check (-not (Test-Path -LiteralPath $settingsFile)) "Uninstaller removes its saved settings"
    Assert-Check (-not (Test-Path -LiteralPath $startMenuDirectory)) "Uninstaller removes its start menu shortcuts"
    Assert-Check (-not (Test-Path -LiteralPath $updateDirectory)) "Uninstaller removes the updater helper and staging files"

    @{ Checks = $checks.ToArray(); FirstInstalledVersion = $firstInstalledVersion;
        UpgradedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $projectDirectory "dist\win-x64\ApolloMicBridge.exe")).FileVersion;
        IdleDurationMs = $elapsed.Elapsed.TotalMilliseconds; IdleProcessCpuMs = $idleCpuMs;
        IdlePercentOfOneLogicalCore = $idleCorePercent; SetupSha256 = (Get-FileHash -LiteralPath $setup).Hash;
        Boundary = "Installed/portable update handoff and restart, native window lifecycle, registry and idle CPU. No recording from real microphones, Apollo, VB-CABLE or FiveM." } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testDirectory "package-verification.json") -Encoding UTF8
    Write-Output "IDLE: $idleCpuMs ms CPU over $($elapsed.Elapsed.TotalSeconds) s."
}
finally {
    foreach ($log in @((Join-Path $settingsDirectory 'app.log'), (Join-Path $updateDirectory 'install.log'))) {
        if (Test-Path -LiteralPath $log) {
            Copy-Item -LiteralPath $log -Destination (Join-Path $testDirectory ('updater-' + [IO.Path]::GetFileName($log)))
        }
    }
    if ($installed -and (Test-Path -LiteralPath $uninstaller)) {
        $registeredPath = Get-ItemPropertyValue -LiteralPath $installKey -Name InstallPath -ErrorAction SilentlyContinue
        if ($registeredPath -eq $installedDirectory) {
            $cleanup = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru
            if (-not $cleanup.WaitForExit(30000) -or $cleanup.ExitCode -ne 0) {
                Write-Warning "Test installation cleanup did not complete; preserve the logs and inspect the registered test directory."
            }
        }
    }
}
