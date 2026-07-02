$ErrorActionPreference = "Stop"

Set-Location -LiteralPath $PSScriptRoot

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Error "Python was not found on PATH. Install Python 3 or use the Python launcher, then rerun this script."
    exit 1
}

Set-Location -LiteralPath (Join-Path $PSScriptRoot "trainer-client")
python scripts/operator_console.py
