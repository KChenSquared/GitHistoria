[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'osx-arm64', 'osx-x64', 'linux-x64')]
    [string]$Rid,

    [switch]$Archives,
    [switch]$Installers,

    [string]$OutDir = 'dist',
    [string]$RuntimeInstaller = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Archives -and -not $Installers) {
    Write-Error "At least one of -Archives or -Installers must be specified."
    exit 1
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ResolvedOutDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $OutDir))
if (-not (Test-Path $ResolvedOutDir)) {
    New-Item -ItemType Directory -Path $ResolvedOutDir -Force | Out-Null
}

$Staging = Join-Path $RepoRoot "obj/packaging-staging/$Rid"
if (Test-Path $Staging) {
    Remove-Item -Recurse -Force $Staging
}
New-Item -ItemType Directory -Path $Staging -Force | Out-Null

Write-Host "Publishing Historia.App for $Rid..."
$publishArgs = @(
    "publish",
    (Join-Path $RepoRoot "Historia.App/Historia.App.csproj"),
    "-c", "Release",
    "-r", $Rid,
    "--self-contained", "false",
    "-o", $Staging
)

$publishProc = Start-Process -FilePath "dotnet" -ArgumentList $publishArgs -WorkingDirectory $RepoRoot -NoNewWindow -Wait -PassThru
if ($publishProc.ExitCode -ne 0) {
    Write-Error "dotnet publish failed with exit code $($publishProc.ExitCode)"
    exit $publishProc.ExitCode
}

# Resolve version via MinVer
$rawVersion = (& dotnet msbuild (Join-Path $RepoRoot "Historia.App/Historia.App.csproj") -target:MinVer -getProperty:MinVerVersion -p:Configuration=Release)
$version = ($rawVersion -join "").Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    $rawVersion = (& dotnet msbuild (Join-Path $RepoRoot "Historia.App/Historia.App.csproj") -target:MinVer -getProperty:Version -p:Configuration=Release)
    $version = ($rawVersion -join "").Trim()
}
if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Error "Could not resolve build version from MinVer."
    exit 1
}
Write-Host "Packaging version: $version"

# Assert staging invariants
$isWindows = $Rid -eq 'win-x64'
$cmdrExeName = if ($isWindows) { 'historia-cmdr.exe' } else { 'historia-cmdr' }
$cmdrPath = Join-Path $Staging $cmdrExeName
$cmdrConfigPath = Join-Path $Staging 'historia-cmdr.runtimeconfig.json'
$licensesPath = Join-Path $Staging 'third-party-licenses.md'
$devModePath = Join-Path $Staging 'enableDevMode'

if (-not (Test-Path $cmdrPath)) {
    Write-Error "Assertion failed: staging directory is missing $cmdrExeName"
    exit 1
}
if (-not (Test-Path $cmdrConfigPath)) {
    Write-Error "Assertion failed: staging directory is missing historia-cmdr.runtimeconfig.json"
    exit 1
}
if (-not (Test-Path $licensesPath)) {
    Write-Error "Assertion failed: staging directory is missing third-party-licenses.md"
    exit 1
}
if (Test-Path $devModePath) {
    Write-Error "Assertion failed: staging directory must not contain enableDevMode"
    exit 1
}

# Copy LICENSE into staging
Copy-Item -Path (Join-Path $RepoRoot "LICENSE") -Destination $Staging -Force

# Create archives
if ($Archives) {
    if ($isWindows) {
        $archivePath = Join-Path $ResolvedOutDir "Historia-$version-win-x64.zip"
        if (Test-Path $archivePath) { Remove-Item -Force $archivePath }
        Write-Host "Creating archive $archivePath..."
        Compress-Archive -Path "$Staging\*" -DestinationPath $archivePath -CompressionLevel Optimal
    } else {
        $archivePath = Join-Path $ResolvedOutDir "Historia-$version-$Rid.tar.gz"
        if (Test-Path $archivePath) { Remove-Item -Force $archivePath }
        Write-Host "Creating archive $archivePath..."
        $tarArgs = @("-czf", $archivePath, "-C", $Staging, ".")
        $tarProc = Start-Process -FilePath "tar" -ArgumentList $tarArgs -NoNewWindow -Wait -PassThru
        if ($tarProc.ExitCode -ne 0) {
            Write-Error "tar failed with exit code $($tarProc.ExitCode)"
            exit $tarProc.ExitCode
        }
    }
}

# Create installers
if ($Installers) {
    if ($isWindows) {
        $isccPath = $null
        $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($cmd) {
            $isccPath = $cmd.Source
        } else {
            $candidates = @(
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
            )
            foreach ($candidate in $candidates) {
                if (Test-Path $candidate) {
                    $isccPath = $candidate
                    break
                }
            }
        }

        if (-not $isccPath) {
            Write-Error "ISCC.exe not found on PATH or under Program Files. Inno Setup 6 is required to build the Windows installer."
            exit 1
        }

        $issFile = Join-Path $RepoRoot "packaging/windows/Historia.iss"
        $isccArgs = @(
            "/DAppVersion=$version",
            "/DSourceDir=$Staging",
            "/DOutputDir=$ResolvedOutDir"
        )
        if (-not [string]::IsNullOrWhiteSpace($RuntimeInstaller)) {
            $isccArgs += "/DRuntimeInstaller=$RuntimeInstaller"
        }

        $isccArgs += $issFile
        Write-Host "Running Inno Setup compiler: $isccPath"
        $isccProc = Start-Process -FilePath $isccPath -ArgumentList $isccArgs -NoNewWindow -Wait -PassThru
        if ($isccProc.ExitCode -ne 0) {
            Write-Error "Inno Setup compilation failed with exit code $($isccProc.ExitCode)"
            exit $isccProc.ExitCode
        }
    } elseif ($Rid -like 'osx-*') {
        $bundleStaging = Join-Path $RepoRoot "obj/packaging-bundle/$Rid"
        if (Test-Path $bundleStaging) { Remove-Item -Recurse -Force $bundleStaging }
        $appDir = Join-Path $bundleStaging "Historia.app"
        $macOSDir = Join-Path $appDir "Contents/MacOS"
        $resDir = Join-Path $appDir "Contents/Resources"
        New-Item -ItemType Directory -Path $macOSDir -Force | Out-Null
        New-Item -ItemType Directory -Path $resDir -Force | Out-Null

        Copy-Item -Path "$Staging/*" -Destination $macOSDir -Recurse -Force
        & chmod +x (Join-Path $macOSDir "Historia") (Join-Path $macOSDir "historia-cmdr")

        $plistContent = Get-Content (Join-Path $RepoRoot "packaging/macos/Info.plist") -Raw
        $plistContent = $plistContent -replace '__VERSION__', $version
        Set-Content -Path (Join-Path $appDir "Contents/Info.plist") -Value $plistContent

        # Build Historia.icns
        $iconsetDir = Join-Path $bundleStaging "Historia.iconset"
        New-Item -ItemType Directory -Path $iconsetDir -Force | Out-Null
        $masterIcon = Join-Path $RepoRoot "assets/dark/historia-dark-1024.png"
        $iconSizes = @(
            @{ Name = "icon_16x16.png"; Size = 16 },
            @{ Name = "icon_16x16@2x.png"; Size = 32 },
            @{ Name = "icon_32x32.png"; Size = 32 },
            @{ Name = "icon_32x32@2x.png"; Size = 64 },
            @{ Name = "icon_128x128.png"; Size = 128 },
            @{ Name = "icon_128x128@2x.png"; Size = 256 },
            @{ Name = "icon_256x256.png"; Size = 256 },
            @{ Name = "icon_256x256@2x.png"; Size = 512 },
            @{ Name = "icon_512x512.png"; Size = 512 },
            @{ Name = "icon_512x512@2x.png"; Size = 1024 }
        )
        foreach ($entry in $iconSizes) {
            $sz = $entry.Size
            $outIcon = Join-Path $iconsetDir $entry.Name
            & sips -z $sz $sz $masterIcon --out $outIcon | Out-Null
        }
        & iconutil -c icns $iconsetDir -o (Join-Path $resDir "Historia.icns")

        # Ad-hoc sign app bundle
        Write-Host "Ad-hoc signing Historia.app..."
        & codesign --force --deep --sign - $appDir

        # Create Applications symlink
        $dmgFolder = Join-Path $bundleStaging "dmg-root"
        New-Item -ItemType Directory -Path $dmgFolder -Force | Out-Null
        Move-Item -Path $appDir -Destination $dmgFolder
        & ln -s /Applications (Join-Path $dmgFolder "Applications")

        $dmgPath = Join-Path $ResolvedOutDir "Historia-$version-$Rid.dmg"
        if (Test-Path $dmgPath) { Remove-Item -Force $dmgPath }
        Write-Host "Creating DMG $dmgPath..."
        & hdiutil create -volname Historia -srcfolder $dmgFolder -ov -format UDZO $dmgPath
    } elseif ($Rid -eq 'linux-x64') {
        $debVer = $version -replace '-', '~'
        $debStaging = Join-Path $RepoRoot "obj/packaging-deb"
        if (Test-Path $debStaging) { Remove-Item -Recurse -Force $debStaging }

        $optDir = Join-Path $debStaging "opt/historia"
        $binDir = Join-Path $debStaging "usr/bin"
        $appsDir = Join-Path $debStaging "usr/share/applications"
        $debianDir = Join-Path $debStaging "DEBIAN"

        New-Item -ItemType Directory -Path $optDir -Force | Out-Null
        New-Item -ItemType Directory -Path $binDir -Force | Out-Null
        New-Item -ItemType Directory -Path $appsDir -Force | Out-Null
        New-Item -ItemType Directory -Path $debianDir -Force | Out-Null

        Copy-Item -Path "$Staging/*" -Destination $optDir -Recurse -Force
        & chmod +x (Join-Path $optDir "Historia") (Join-Path $optDir "historia-cmdr")

        & ln -s /opt/historia/Historia (Join-Path $binDir "historia")
        & ln -s /opt/historia/historia-cmdr (Join-Path $binDir "historia-cmdr")

        Copy-Item -Path (Join-Path $RepoRoot "packaging/linux/historia.desktop") -Destination (Join-Path $appsDir "historia.desktop") -Force

        $controlContent = Get-Content (Join-Path $RepoRoot "packaging/linux/control") -Raw
        $controlContent = $controlContent -replace '__VERSION__', $debVer
        Set-Content -Path (Join-Path $debianDir "control") -Value $controlContent

        $iconSizes = @(16, 24, 32, 48, 64, 128, 256, 512)
        foreach ($sz in $iconSizes) {
            $srcIcon = Join-Path $RepoRoot "assets/dark/historia-dark-$sz.png"
            if (Test-Path $srcIcon) {
                $iconDestDir = Join-Path $debStaging "usr/share/icons/hicolor/${sz}x${sz}/apps"
                New-Item -ItemType Directory -Path $iconDestDir -Force | Out-Null
                Copy-Item -Path $srcIcon -Destination (Join-Path $iconDestDir "historia.png") -Force
            }
        }

        $debPath = Join-Path $ResolvedOutDir "historia_${debVer}_amd64.deb"
        if (Test-Path $debPath) { Remove-Item -Force $debPath }
        Write-Host "Building Debian package $debPath..."
        & dpkg-deb --build --root-owner-group $debStaging $debPath
    }
}

Write-Host "Packaging complete for $Rid."
