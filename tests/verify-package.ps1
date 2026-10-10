[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory, [string]$PreviousSetup)

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$installedDirectory = Join-Path $testDirectory "installed app"
$appFile = Join-Path $installedDirectory "ApolloMicBridge.exe"
$uninstaller = Join-Path $installedDirectory "unins000.exe"
$setup = Join-Path $projectDirectory "dist\ApolloMicBridge-Setup-0.1.2.exe"
$firstSetup = if ($PreviousSetup) { [IO.Path]::GetFullPath($PreviousSetup) } else { $setup }
$installKey = "HKCU:\Software\SkySystems\ApolloMicBridge"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{77DCFA70-A21A-43F9-AB44-C6A8D3481997}_is1"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runName = "SkySystemsApolloMicBridge"
$settingsDirectory = Join-Path $env:LOCALAPPDATA "SkySystems\ApolloMicBridge"
$settingsFile = Join-Path $settingsDirectory "settings.json"
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath("Programs")) "SkySystems\Apollo Mic Bridge"
$checks = [Collections.Generic.List[string]]::new()
$installed = $false

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

# This test owns a new installation only. Never replace an existing installation or user settings.
if (Test-Path -LiteralPath $installKey) { throw "An existing Apollo Mic Bridge installation must be preserved; package verification skipped." }
if (Test-Path -LiteralPath $uninstallKey) { throw "An existing Apollo Mic Bridge uninstall entry must be preserved." }
if (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue) { throw "An existing Apollo Mic Bridge startup value must be preserved." }
if (Test-Path -LiteralPath $startMenuDirectory) { throw "Existing Apollo Mic Bridge shortcuts must be preserved." }
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

try {
    Invoke-Setup "autostart" "install.log" $firstSetup
    $installed = $true
    Assert-Check (Test-Path -LiteralPath $appFile) "Installed executable exists"
    Assert-Check (Test-Path -LiteralPath $uninstaller) "Uninstaller exists"
    Assert-Check (Test-Path -LiteralPath $uninstallKey) "Windows Apps uninstall entry exists"
    $expectedStartup = '"' + $appFile + '" --autostart --background'
    Assert-Check ((Get-ItemPropertyValue -LiteralPath $runKey -Name $runName) -ceq $expectedStartup) "Windows startup command quotes paths with spaces"
    Assert-Check (Test-Path -LiteralPath (Join-Path $startMenuDirectory "Apollo Mic Bridge.lnk")) "Start menu application shortcut exists"
    Assert-Check (Test-Path -LiteralPath (Join-Path $startMenuDirectory "Deinstallieren - Uninstall.lnk")) "Start menu uninstaller shortcut exists"
    $firstInstalledVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appFile).FileVersion

    $smoke = Start-Process -FilePath $appFile -ArgumentList "--smoke-test `"$(Join-Path $testDirectory 'installed-ui.png')`"" -WindowStyle Hidden -PassThru
    Assert-Check ($smoke.WaitForExit(15000) -and $smoke.ExitCode -eq 0) "Installed EXE passes native window, close-to-tray and restore checks"

    # Saved but unavailable devices must never activate another microphone on this computer.
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{ Backend = 0; InputId = "missing-test-input"; CableId = "missing-test-cable"; Channel = 9; Gain = -6; AsioRate = 48000 } |
        ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    $settingsHash = (Get-FileHash -LiteralPath $settingsFile).Hash
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
    Start-Sleep -Milliseconds 500
    $running.Refresh()
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

    @{ Checks = $checks.ToArray(); FirstInstalledVersion = $firstInstalledVersion;
        UpgradedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $projectDirectory "dist\win-x64\ApolloMicBridge.exe")).FileVersion;
        IdleDurationMs = $elapsed.Elapsed.TotalMilliseconds; IdleProcessCpuMs = $idleCpuMs;
        IdlePercentOfOneLogicalCore = $idleCorePercent; SetupSha256 = (Get-FileHash -LiteralPath $setup).Hash;
        Boundary = "Installed application lifecycle, registry and idle CPU only. No recording from real microphones, Apollo, VB-CABLE or FiveM." } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testDirectory "package-verification.json") -Encoding UTF8
    Write-Output "IDLE: $idleCpuMs ms CPU over $($elapsed.Elapsed.TotalSeconds) s."
}
finally {
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
