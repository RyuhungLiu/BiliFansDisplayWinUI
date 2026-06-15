[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$Runtime = "win-x64",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "BiliFansDisplay\BiliFansDisplay.csproj"
$projectXml = [xml](Get-Content -Raw -LiteralPath $projectPath)
$targetFramework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ })[0]
$buildDir = Join-Path $repoRoot "BiliFansDisplay\bin\$Platform\$Configuration\$targetFramework\$Runtime"
$packageDir = Join-Path $repoRoot "artifacts\package\BiliFansDisplay-win-x64"
$zipPath = Join-Path $repoRoot "artifacts\BiliFansDisplay-win-x64.zip"
$programsRoot = Join-Path $env:LOCALAPPDATA "Programs"
$installDir = Join-Path $programsRoot "BiliFansDisplay"
$exePath = Join-Path $installDir "BiliFansDisplay.exe"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startupDir = Join-Path $startMenuDir "Startup"
$startMenuShortcut = Join-Path $startMenuDir "BiliFansDisplay.lnk"
$startupShortcut = Join-Path $startupDir "BiliFansDisplay.lnk"

function Assert-PathUnder {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent)

    if (-not $resolvedParent.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $resolvedParent += [System.IO.Path]::DirectorySeparatorChar
    }

    if (-not $resolvedPath.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside expected parent: $resolvedPath"
    }
}

function New-AppShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = "$exePath,0"
    $shortcut.Description = "BiliFansDisplay"
    $shortcut.Save()
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found: $projectPath"
}

Assert-PathUnder -Path $buildDir -Parent $repoRoot
Assert-PathUnder -Path $packageDir -Parent $repoRoot
Assert-PathUnder -Path $zipPath -Parent $repoRoot
Assert-PathUnder -Path $installDir -Parent $programsRoot

Get-Process -Name "BiliFansDisplay" -ErrorAction SilentlyContinue | Stop-Process -Force

& dotnet build $projectPath `
    -c $Configuration `
    -p:Platform=$Platform `
    -r $Runtime

if (-not (Test-Path -LiteralPath (Join-Path $buildDir "BiliFansDisplay.exe"))) {
    throw "Build output executable was not found: $buildDir"
}

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item -Path (Join-Path $buildDir "*") -Destination $packageDir -Recurse -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

if (Test-Path -LiteralPath $installDir) {
    Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item -Path (Join-Path $packageDir "*") -Destination $installDir -Recurse -Force

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Installed executable was not found: $exePath"
}

New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
New-Item -ItemType Directory -Path $startupDir -Force | Out-Null

New-AppShortcut -ShortcutPath $startMenuShortcut
New-AppShortcut -ShortcutPath $startupShortcut

if (-not $NoLaunch) {
    Start-Process -FilePath $exePath -WorkingDirectory $installDir
}

[pscustomobject]@{
    PackageDir = $packageDir
    Zip = $zipPath
    InstallDir = $installDir
    Exe = $exePath
    StartMenuShortcut = $startMenuShortcut
    StartupShortcut = $startupShortcut
    StartupEnabled = (Test-Path -LiteralPath $startupShortcut)
}
