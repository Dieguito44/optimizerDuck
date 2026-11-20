# Publishes a self-contained test build of Zentra to C:\Proyectos\zentra-preview (test bench, level 3).
# Open it yourself (double click Zentra.exe) or with tools\zentra-prueba.wsb in Windows Sandbox.
# Never launch it from Claude's terminal: it would inherit the MSIX AppData virtualization.
param([string]$Output = 'C:\Proyectos\zentra-preview')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Get-Process Zentra -ErrorAction SilentlyContinue) {
    throw 'Close Zentra first: the test build folder is in use.'
}
dotnet publish (Join-Path $repo 'optimizerDuck\optimizerDuck.csproj') -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Output "Test build ready: $Output\Zentra.exe"
