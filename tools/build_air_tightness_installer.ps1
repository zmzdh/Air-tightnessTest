param(
    [string]$PublishDir = "dist\publish",
    [string]$OutputRoot = "artifacts\installer",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishSource = Resolve-Path (Join-Path $root $PublishDir)
$installerDir = Join-Path (Join-Path $root $OutputRoot) $Version
$publishStage = Join-Path $installerDir "publish"
$zipName = "Air-tightnessTest-$Version-publish.zip"
$zipPath = Join-Path $installerDir $zipName
$setupName = "Air-tightnessTest-$Version-Setup.exe"
$setupPath = Join-Path $installerDir $setupName
$installScriptPath = Join-Path $installerDir "install.ps1"
$sedPath = Join-Path $installerDir "Air-tightnessTest-$Version.iexpress.sed"

if (Test-Path $installerDir) {
    Remove-Item -LiteralPath $installerDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishStage | Out-Null

$excludedNames = @("Config", "Data", "Logs", "TestData.db")

Get-ChildItem -LiteralPath $publishSource -Force | ForEach-Object {
    if ($excludedNames -contains $_.Name) {
        return
    }

    if ($_.Extension -eq ".pdb") {
        return
    }

    Copy-Item -LiteralPath $_.FullName -Destination $publishStage -Recurse -Force
}

Compress-Archive -Path (Join-Path $publishStage "*") -DestinationPath $zipPath -Force

$installScript = @'
$ErrorActionPreference = 'Stop'

$AppName = 'Air-tightnessTest'
$DisplayName = 'Air-tightnessTest'
$Publisher = 'CSAS'
$Version = '__VERSION__'
$Package = Join-Path $PSScriptRoot 'Air-tightnessTest-__VERSION__-publish.zip'
$ProgramFilesRoot = if ([string]::IsNullOrWhiteSpace($env:ProgramW6432)) { $env:ProgramFiles } else { $env:ProgramW6432 }
$InstallRoot = Join-Path $ProgramFilesRoot 'Air-tightnessTest'
$UninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Air-tightnessTest'
$LogDir = Join-Path $env:ProgramData 'Air-tightnessTest'
$LogPath = Join-Path $LogDir 'install.log'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PowerShellPath {
    $sysnative = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $sysnative) {
        return $sysnative
    }

    return Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
}

if (-not (Test-Administrator)) {
    $quotedScript = '"' + $PSCommandPath + '"'
    $process = Start-Process -FilePath (Get-PowerShellPath) -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File $quotedScript" -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Start-Transcript -Path $LogPath -Append | Out-Null

try {
    if (-not (Test-Path -LiteralPath $Package)) {
        throw "Package not found: $Package"
    }

    Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    Expand-Archive -LiteralPath $Package -DestinationPath $InstallRoot -Force

    $exe = Join-Path $InstallRoot 'Air-tightnessTest.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Executable not found after install: $exe"
    }

    $uninstallScript = @"
param(
    [switch]`$Quiet
)

`$ErrorActionPreference = 'Stop'
`$AppName = 'Air-tightnessTest'
`$ProgramFilesRoot = if ([string]::IsNullOrWhiteSpace(`$env:ProgramW6432)) { `$env:ProgramFiles } else { `$env:ProgramW6432 }
`$InstallRoot = Join-Path `$ProgramFilesRoot 'Air-tightnessTest'
`$UninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Air-tightnessTest'
`$DesktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Air-tightnessTest.lnk'
`$ProgramsFolder = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Air-tightnessTest'

function Test-Administrator {
    `$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    `$principal = New-Object Security.Principal.WindowsPrincipal(`$identity)
    return `$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PowerShellPath {
    `$sysnative = Join-Path `$env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath `$sysnative) {
        return `$sysnative
    }

    return Join-Path `$env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
}

if (-not (Test-Administrator)) {
    `$quotedScript = '"' + `$PSCommandPath + '"'
    `$args = "-NoProfile -ExecutionPolicy Bypass -File `$quotedScript"
    if (`$Quiet) {
        `$args += ' -Quiet'
    }
    `$process = Start-Process -FilePath (Get-PowerShellPath) -ArgumentList `$args -Verb RunAs -Wait -PassThru
    exit `$process.ExitCode
}

Set-Location `$env:TEMP
Get-Process -Name `$AppName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath `$DesktopShortcut) {
    Remove-Item -LiteralPath `$DesktopShortcut -Force
}

if (Test-Path -LiteralPath `$ProgramsFolder) {
    Remove-Item -LiteralPath `$ProgramsFolder -Recurse -Force
}

if (Test-Path -LiteralPath `$UninstallKey) {
    Remove-Item -LiteralPath `$UninstallKey -Recurse -Force
}

if (Test-Path -LiteralPath `$InstallRoot) {
    Remove-Item -LiteralPath `$InstallRoot -Recurse -Force
}

if (-not `$Quiet) {
    Write-Host "`$AppName has been uninstalled. AppData runtime data was preserved."
}
"@

    Set-Content -LiteralPath (Join-Path $InstallRoot 'Uninstall.ps1') -Value $uninstallScript -Encoding UTF8

    $wsh = New-Object -ComObject WScript.Shell
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Air-tightnessTest.lnk'
    $shortcut = $wsh.CreateShortcut($desktopShortcut)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Save()

    $programsFolder = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Air-tightnessTest'
    New-Item -ItemType Directory -Path $programsFolder -Force | Out-Null
    $startShortcut = Join-Path $programsFolder 'Air-tightnessTest.lnk'
    $shortcut = $wsh.CreateShortcut($startShortcut)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Save()

    New-Item -Path $UninstallKey -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'DisplayName' -Value $DisplayName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'DisplayVersion' -Value $Version -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'Publisher' -Value $Publisher -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'InstallLocation' -Value $InstallRoot -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'DisplayIcon' -Value $exe -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallRoot\Uninstall.ps1`"" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'QuietUninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallRoot\Uninstall.ps1`" -Quiet" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null

    $sizeKb = [int]((Get-ChildItem -LiteralPath $InstallRoot -Recurse -Force | Measure-Object Length -Sum).Sum / 1KB)
    New-ItemProperty -Path $UninstallKey -Name 'EstimatedSize' -Value $sizeKb -PropertyType DWord -Force | Out-Null

    Start-Process -FilePath $exe -WorkingDirectory $InstallRoot
    Write-Host "$DisplayName $Version installed to $InstallRoot"
    Write-Host "Runtime data is stored in AppData and is preserved during uninstall."
}
catch {
    Write-Host "Installation failed. See log: $LogPath"
    Write-Host $_.Exception.Message
    throw
}
finally {
    Stop-Transcript | Out-Null
}
'@.Replace("__VERSION__", $Version)

Set-Content -LiteralPath $installScriptPath -Value $installScript -Encoding UTF8

$installerDirWithSlash = $installerDir.TrimEnd('\') + '\'
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=Install complete.
TargetName=$setupPath
FriendlyName=Air-tightnessTest $Version
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$installerDirWithSlash
[SourceFiles0]
%FILE0%=
%FILE1%=
[Strings]
FILE0="$zipName"
FILE1="install.ps1"
"@

Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII

$iexpress = Join-Path $env:SystemRoot "System32\iexpress.exe"
if (-not (Test-Path -LiteralPath $iexpress)) {
    throw "iexpress.exe was not found."
}

$process = Start-Process -FilePath $iexpress -ArgumentList @("/N", "/Q", $sedPath) -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "iexpress.exe failed with exit code $($process.ExitCode)"
}

for ($i = 0; $i -lt 360 -and -not (Test-Path -LiteralPath $setupPath); $i++) {
    Start-Sleep -Milliseconds 500
}

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Installer was not created: $setupPath"
}

Get-Item -LiteralPath $setupPath
