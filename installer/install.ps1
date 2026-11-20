# Builds Alliance Status from source and installs it for the current user.
# If no .NET 10 SDK is present, a private copy is downloaded to a temp folder and deleted afterwards;
# nothing is installed system-wide.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\AllianceStatus\AllianceStatus.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AllianceStatus'
$exe = Join-Path $installDir 'AllianceStatus.exe'
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$work = Join-Path $env:TEMP "AllianceStatus-build-$([guid]::NewGuid().ToString('N').Substring(0,8))"

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

try {
    New-Item -ItemType Directory -Path $work | Out-Null

    Step 'Looking for the .NET 10 SDK'
    $dotnet = $null
    $system = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($system -and ((& $system.Source --list-sdks) -match '^10\.')) {
        $dotnet = $system.Source
        Write-Host "Using installed SDK."
    } else {
        Write-Host "Not found. Downloading a temporary copy (about 200 MB, removed afterwards)..."
        $script = Join-Path $work 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
        & $script -Channel 10.0 -Architecture $arch -InstallDir (Join-Path $work 'sdk') -NoPath | Out-Null
        $dotnet = Join-Path $work 'sdk\dotnet.exe'
    }

    Step 'Building (this takes a minute or two)'
    $out = Join-Path $work 'out'
    & $dotnet publish $project -c Release -r "win-$arch" --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed. Scroll up for the error and send it to whoever maintains the app." }

    Step 'Installing'
    Get-Process AllianceStatus -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item (Join-Path $out 'AllianceStatus.exe') $exe -Force
    Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') (Join-Path $installDir 'uninstall.ps1') -Force

    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Alliance Status.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $installDir
    $link.Description = 'Alliance cluster status monitor'
    $link.Save()

    # Entry in Settings > Apps so it can be uninstalled normally.
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AllianceStatus'
    New-Item -Path $key -Force | Out-Null
    $props = @{
        DisplayName     = 'Alliance Status'
        DisplayVersion  = '1.0.0'
        Publisher       = 'Ferdinand Koenig and Claude (Anthropic)'
        DisplayIcon     = $exe
        InstallLocation = $installDir
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDir\uninstall.ps1`""
        NoModify        = 1
        NoRepair        = 1
    }
    foreach ($p in $props.GetEnumerator()) {
        $type = if ($p.Value -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $key -Name $p.Key -Value $p.Value -PropertyType $type -Force | Out-Null
    }

    $answer = Read-Host "`nStart Alliance Status when you sign in to Windows? [Y/n]"
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ($answer -notmatch '^(n|no)$') {
        New-ItemProperty -Path $run -Name 'AllianceStatus' -Value "`"$exe`" --minimized" -PropertyType String -Force | Out-Null
    }

    Start-Process $exe
    Write-Host "`nDone. Alliance Status is running in the notification area (bottom-right)." -ForegroundColor Green
    Write-Host "Installed to $installDir"
}
catch {
    Write-Host "`n$($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
