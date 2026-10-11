<#
  Starts the API (http://localhost:5080, SQLite, demo data) and the frontend (http://localhost:5173)
  in two new PowerShell windows. Close those windows to stop them.
#>
$root = Split-Path -Parent $PSScriptRoot
$api = "`$env:ASPNETCORE_ENVIRONMENT='Development'; Set-Location '$root\backend\src\SmartBuy.Api'; dotnet run --no-launch-profile --urls http://localhost:5080"
$web = "Set-Location '$root\frontend'; npm run dev"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $api
Start-Process powershell -ArgumentList '-NoExit', '-Command', $web
Write-Host 'API:      http://localhost:5080/swagger'
Write-Host 'Frontend: http://localhost:5173'
Write-Host 'Admin:    admin@smartbuy.local / ChangeMe!Admin2026  (development only)'
