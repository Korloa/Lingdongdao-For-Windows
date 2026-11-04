$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$modelRoot = if (Test-Path (Join-Path $root 'LingDongDao\LingDongDao.csproj')) {
    Join-Path $root 'models\vosk'
} else {
    Join-Path $root 'models'
}
$archive = Join-Path $modelRoot 'vosk-model-small-cn-0.22.zip'
$model = Join-Path $modelRoot 'vosk-model-small-cn-0.22'
$expectedSha256 = '3AF8B0E7E0F835AE9D414CE5DF580237A3CFB08D586C9FBBB0F7FF29AD5B14BA'
New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null
if (Test-Path (Join-Path $model 'conf\model.conf')) {
    Write-Output "Voice model ready: $model"
    exit 0
}
if (-not (Test-Path $archive)) {
    Invoke-WebRequest -Uri 'https://alphacephei.com/vosk/models/vosk-model-small-cn-0.22.zip' -OutFile $archive
}
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($hash -ne $expectedSha256) {
    throw "Voice model archive SHA-256 mismatch: $hash"
}
Expand-Archive -LiteralPath $archive -DestinationPath $modelRoot -Force
if (-not (Test-Path (Join-Path $model 'conf\model.conf'))) {
    throw 'Voice model extraction incomplete'
}
Write-Output "Voice model ready: $model"
