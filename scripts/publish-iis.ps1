<#
  Publishes the API for IIS. Copy the output folder to the server (see docs/DEPLOYMENT.md).
  Example: .\scripts\publish-iis.ps1 -Output C:\publish\smartbuy-api
#>
param([string]$Output = "$(Split-Path -Parent $PSScriptRoot)\publish\smartbuy-api")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet publish "$root\backend\src\SmartBuy.Api\SmartBuy.Api.csproj" -c Release -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Published to $Output" -ForegroundColor Green
