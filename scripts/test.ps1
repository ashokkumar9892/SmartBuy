<# Runs the backend test suite, the frontend build, and the browser end-to-end tests. #>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet test "$root\backend\SmartBuy.sln" -nologo
if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }

Push-Location "$root\frontend"
try {
  npm run build
  if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
  npx playwright install chromium
  npx playwright test
  if ($LASTEXITCODE -ne 0) { throw 'End-to-end tests failed.' }
} finally { Pop-Location }
