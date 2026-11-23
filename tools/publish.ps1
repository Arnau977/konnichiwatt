<#
.SYNOPSIS
  Builds a release of Konnichiwatt: single-file .exe (installs itself when opened), update.json (version +
  SHA-256) and Konnichiwatt-portable.zip (same .exe + portable.txt).

.EXAMPLE
  # Release whose copies update themselves from GitHub Releases of a public "releases" repo:
  .\tools\publish.ps1 -BaseUrl https://github.com/USER/konnichiwatt-releases/releases/latest/download

  Then upload publish\Konnichiwatt.exe and publish\update.json to that release.
#>
param(
    # Folder URL where Konnichiwatt.exe and update.json will be downloadable (no trailing slash). Empty = no updates.
    [string]$BaseUrl = "",
    # Version to stamp (defaults to the one in Konnichiwatt.csproj).
    [string]$Version = "",
    [string]$Notes = "",
    [string]$Out = (Join-Path $PSScriptRoot "..\publish")
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\Konnichiwatt\Konnichiwatt.csproj"
if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$manifestUrl = if ($BaseUrl) { "$BaseUrl/update.json" } else { "" }

New-Item -ItemType Directory -Force $Out | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version "-p:UpdateManifestUrl=$manifestUrl" -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $Out "Konnichiwatt.exe"
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
if ($BaseUrl) {
    [ordered]@{ version = $Version; url = "$BaseUrl/Konnichiwatt.exe"; sha256 = $sha; notes = $Notes } |
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
