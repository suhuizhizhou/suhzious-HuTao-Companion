# Run the HuTao desktop pet (PowerShell 5.1 compatible)
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$exe = Join-Path $scriptDir 'src\HuTao.Pet\bin\Debug\net9.0-windows\HuTao.Pet.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "[run-pet] exe not found: $exe"
    Write-Host "Run .\build.ps1 first."
    exit 1
}

# The pet auto-loads .env (DEEPSEEK_API_KEY / HU_TAO_TTS_PYTHON) from the repo root.
& $exe
