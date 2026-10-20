# Publishes xPlayer (ReadyToRun) into .\dist and stamps a date version (yyyy.MMdd.HHmm), like the other PC apps.
param([string]$Version = "")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) { $now = Get-Date; $Version = "$($now.Year).$([int]$now.ToString('MMdd')).$([int]$now.ToString('HHmm'))" }
Get-Process xPlayer -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$root\app\xPlayer.csproj" -c Release -o "$root\dist" --nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Set-Content -Path "$root\dist\version.txt" -Value $Version -Encoding ascii
Write-Host "Built: $root\dist\xPlayer.exe v$Version"
