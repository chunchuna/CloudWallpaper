# Publish a new version: builds the single-file exe and writes latest.json next to it.
#
#   tools\publish.ps1 -Version 1.1.0 -Notes "what changed"
#
# Upload BOTH files from the output folder (CloudWallpaper.exe + latest.json) to the same place.
# The address of latest.json must be the one in update-url.txt (project root, one line);
# every installed copy checks that address and updates itself.
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Notes = "",
  [string]$UpdateUrl = "",          # overrides update-url.txt (used for local tests)
  [string]$Out = "release",
  [switch]$Upload                   # also create a GitHub release and upload both files (needs $env:GITHUB_TOKEN)
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }

$props = @("-p:Version=$Version", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
           "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none")
if ($UpdateUrl) { $props += "-p:UpdateUrl=$UpdateUrl" }
dotnet publish -c Release -r win-x64 --self-contained true @props -o $Out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $Out "CloudWallpaper.exe"
$manifest = [ordered]@{
  version = $Version
  file    = "CloudWallpaper.exe"
  sha256  = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
  size    = (Get-Item $exe).Length
  notes   = $Notes
}
[IO.File]::WriteAllText((Join-Path (Resolve-Path $Out) "latest.json"), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
"Published $Version -> $((Resolve-Path $Out).Path)"
Get-ChildItem $Out | Select-Object Name, Length | Format-Table | Out-String

if (-not $Upload) { return }

# ---- Upload as a GitHub release (the address in update-url.txt points at the latest release) ----
$token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { $env:GH_TOKEN }
if (-not $token) { throw "Set the GITHUB_TOKEN environment variable first (do not put the token in a file)." }
$urlFile = "update-url.txt"
if (-not (Test-Path $urlFile) -or (Get-Content $urlFile -Raw) -notmatch 'github\.com/([^/]+)/([^/]+)/releases/') { throw "update-url.txt does not point at a GitHub repository" }
$repo = "$($Matches[1])/$($Matches[2])"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$headers = @{ Authorization = "Bearer $token"; "User-Agent" = "CloudWallpaper-publish"; Accept = "application/vnd.github+json" }
$body = @{ tag_name = "v$Version"; name = "v$Version"; body = $Notes } | ConvertTo-Json
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases" -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8"
foreach ($name in "CloudWallpaper.exe", "latest.json") {
  "Uploading $name ..."
  Invoke-RestMethod "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=$name" -Method Post -Headers $headers `
    -InFile (Join-Path (Resolve-Path $Out) $name) -ContentType "application/octet-stream" -TimeoutSec 1800 | Out-Null
}
"Released: $($release.html_url)"
