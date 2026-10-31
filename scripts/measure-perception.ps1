param([switch]$Enhanced)

$ErrorActionPreference = "Stop"
$project = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pythonExe = Join-Path $project ".venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonExe)) { throw "Run scripts/setup-perception.ps1 first" }
$reportDir = Join-Path $project "reports"
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$benchmark = Join-Path $reportDir "workers-$stamp.json"
& $pythonExe (Join-Path $project "perception_py\benchmark.py") --report $benchmark
if ($LASTEXITCODE -ne 0) { throw "Worker benchmark failed" }
$env:LINGDONGDAO_PYTHON = $pythonExe
$model = Join-Path $project "models\gpa\model.onnx"
if (Test-Path -LiteralPath $model) { $env:LINGDONGDAO_GUI_MODEL = $model }
if ($Enhanced -and -not (Test-Path -LiteralPath $model)) {
    throw "Enhanced mode needs models/gpa/model.onnx; run setup-perception.ps1 -WithGui"
}
$testArgs = @("run", "--project", (Join-Path $project "PerceptionTests\PerceptionTests.csproj"), "-c", "Release", "--", "--live-service")
if ($Enhanced) { $testArgs[-1] = "--live-service-enhanced" }
$live = & dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw "Live service test failed: $live" }
$live | Write-Host
$report = [ordered]@{
    time = (Get-Date).ToString("o")
    computer = $env:COMPUTERNAME
    mode = $(if ($Enhanced) { "enhanced" } else { "standard" })
    worker_benchmark = Get-Content -LiteralPath $benchmark -Raw -Encoding UTF8 | ConvertFrom-Json
    live_service = ($live | Where-Object { $_ -like "LIVE_SERVICE *" } | Select-Object -Last 1)
}
$path = Join-Path $reportDir "perception-$stamp.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
Write-Host "Report: $path"
