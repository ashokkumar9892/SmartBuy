<#
  One-time local setup. Requires the .NET 10 SDK and Node.js 22+.
  Development uses SQLite, so no database server is needed.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host 'Restoring and building the API...' -ForegroundColor Cyan
dotnet build "$root\backend\SmartBuy.sln" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

Write-Host 'Installing frontend packages...' -ForegroundColor Cyan
Push-Location "$root\frontend"
try {
  npm install
  if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
} finally { Pop-Location }

Write-Host ''
Write-Host 'Done. Start everything with:  .\scripts\dev.ps1' -ForegroundColor Green
