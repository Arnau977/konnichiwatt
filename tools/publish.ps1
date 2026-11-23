<#
.SYNOPSIS
  Builds a release of Konnichiwatt: single-file .exe (installs itself when opened), update.json (version +
  SHA-256) and Konnichiwatt-portable.zip (same .exe + portable.txt).

.EXAMPLE
  # What the Release workflow runs for tag v1.2.3:
  .\tools\publish.ps1 -Version 1.2.3 `
      -BaseUrl https://github.com/OWNER/REPO/releases/latest/download `
      -BetaManifestUrl https://github.com/OWNER/REPO/releases/download/beta/update.json `
      -ExeUrl https://github.com/OWNER/REPO/releases/download/v1.2.3/Konnichiwatt.exe

  .\tools\publish.ps1   # local build without updates
#>
param(
    # Folder URL of the stable update.json (no trailing slash), e.g. .../releases/latest/download. Empty = no updates.
    [string]$BaseUrl = "",
    # Full URL of the beta channel's update.json (newest build including pre-releases). Empty = same as stable.
    [string]$BetaManifestUrl = "",
    # Where this exact .exe will be downloadable. Defaults to "$BaseUrl/Konnichiwatt.exe"; releases pass the
    # versioned URL (.../releases/download/v1.2.3/Konnichiwatt.exe) so a manifest always points at its own build.
    [string]$ExeUrl = "",
    # Version to stamp (defaults to the one in Konnichiwatt.csproj).
    [string]$Version = "",
    # What's new; defaults to this version's section of CHANGELOG.md.
    [string]$Notes = "",
    [string]$Out = (Join-Path $PSScriptRoot "..\publish")
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\Konnichiwatt\Konnichiwatt.csproj"
if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$manifestUrl = if ($BaseUrl) { "$BaseUrl/update.json" } else { "" }
if (-not $ExeUrl -and $BaseUrl) { $ExeUrl = "$BaseUrl/Konnichiwatt.exe" }

# This version's section of CHANGELOG.md ("## 1.2.3" up to the next "## ").
if (-not $Notes) {
    $inside = $false
    $section = foreach ($line in Get-Content (Join-Path $PSScriptRoot "..\CHANGELOG.md") -Encoding utf8) {
        if ($line -match '^##\s+v?(\d+\.\d+(\.\d+)?)') { if ($inside) { break }; $inside = ($Matches[1] -eq $Version) }
        if ($inside) { $line }
    }
    $Notes = ($section -join "`n").Trim()
}
New-Item -ItemType Directory -Force $Out | Out-Null
# Release page body: "- espanol -- *English*" per change.
($Notes -split "`n" | ForEach-Object {
    if ($_ -match '^- (.+?) // (.+)$') { "- $($Matches[1]) $([char]0x2014) *$($Matches[2])*" } elseif ($_ -notmatch '^##') { $_ }
}) -join "`n" | Set-Content (Join-Path $Out "release-notes.md") -Encoding utf8

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version "-p:UpdateManifestUrl=$manifestUrl" "-p:UpdateManifestUrlBeta=$BetaManifestUrl" -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $Out "Konnichiwatt.exe"
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
if ($BaseUrl) {
    [ordered]@{ version = $Version; url = $ExeUrl; sha256 = $sha; notes = $Notes } |
        ConvertTo-Json | Set-Content (Join-Path $Out "update.json") -Encoding utf8
}
# Portable build: the same .exe plus the marker file that keeps everything in a "data" folder next to it.
$staging = Join-Path $Out "portable"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item $exe, (Join-Path $PSScriptRoot "portable.txt") $staging
Remove-Item (Join-Path $Out "Konnichiwatt.zip") -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath (Join-Path $Out "Konnichiwatt-portable.zip") -Force
Remove-Item $staging -Recurse -Force

"Konnichiwatt $Version"
"  exe:      $exe"
"  sha256:   $sha"
if ($BaseUrl) { "  updates:  $manifestUrl" } else { "  updates:  (disabled - no -BaseUrl)" }
