param([switch]$Remove)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security
$target = Join-Path $env:LOCALAPPDATA "LingDongDao\agent-key.dpapi"
if ($Remove) {
    Remove-Item -LiteralPath $target -ErrorAction SilentlyContinue
    Write-Host "DeepSeek key removed."
    return
}

$secret = Read-Host "DeepSeek API key" -AsSecureString
$pointer = [IntPtr]::Zero
$plain = $null
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    $plain = [Text.Encoding]::UTF8.GetBytes(
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer))
    if ($plain.Length -eq 0) { throw "API key is empty" }
    $entropy = [Text.Encoding]::UTF8.GetBytes("LingDongDao.DeepSeek.ApiKey.v1")
    $encrypted = [Security.Cryptography.ProtectedData]::Protect(
        $plain, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    $temporary = "$target.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $encrypted)
        Move-Item -LiteralPath $temporary -Destination $target -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    Write-Host "DeepSeek key saved with Windows user encryption."
}
finally {
    if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}
