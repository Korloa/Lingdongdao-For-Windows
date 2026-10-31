param(
    [string]$Python = "python",
    [switch]$WithGui
)

$ErrorActionPreference = "Stop"
$env:PIP_DISABLE_PIP_VERSION_CHECK = "1"
$project = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$venv = Join-Path $project ".venv"
$pythonExe = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path -LiteralPath $pythonExe)) {
    & $Python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Could not create Python environment" }
}
& $pythonExe -m pip install --disable-pip-version-check --index-url https://pypi.org/simple -r (Join-Path $project "perception_py\requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "Could not install local OCR dependencies" }

if ($WithGui) {
    $modelDir = Join-Path $project "models\gpa"
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
    $pt = Join-Path $modelDir "model.pt"
    $onnx = Join-Path $modelDir "model.onnx"
    $expected = "DD404F25B7F329998C7ED97A67827A174DD626DC2683F6844AB33A5219C05F71"
    if (-not (Test-Path -LiteralPath $pt)) {
        Invoke-WebRequest -Uri "https://huggingface.co/Salesforce/GPA-GUI-Detector/resolve/main/model.pt?download=true" -OutFile $pt
    }
    if ((Get-FileHash -LiteralPath $pt -Algorithm SHA256).Hash -ne $expected) {
        throw "GUI model checksum mismatch; file was not loaded"
    }
    if (-not (Test-Path -LiteralPath $onnx)) {
        & $pythonExe -m pip install --disable-pip-version-check --index-url https://pypi.org/simple ultralytics==8.4.8
        if ($LASTEXITCODE -ne 0) { throw "Could not install model export dependencies" }
        & $pythonExe -c "from ultralytics import YOLO; YOLO(r'$pt').export(format='onnx', imgsz=320, opset=12, dynamic=False, simplify=False)"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $onnx)) {
            throw "Could not export GUI model to ONNX"
        }
    }
    Write-Host "GUI detector ONNX: $onnx"
}

Write-Host "Offline OCR ready. Enable desktop perception in LingDongDao settings."
