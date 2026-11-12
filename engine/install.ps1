$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$engineArgs = @($args)
$candidates = @(
    (Join-Path (Split-Path -Parent $projectRoot) '음원분리댕\.venv\Scripts\python.exe'),
    'C:\python\python.exe'
)
foreach ($commandName in @('python', 'python3')) {
    $command = Get-Command $commandName -ErrorAction SilentlyContinue
    if ($command -and $command.Source -notlike '*\WindowsApps\*') {
        $candidates += $command.Source
    }
}
$selectedPython = $null
foreach ($candidate in ($candidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $version = & $candidate -c 'import sys; print(str(sys.version_info.major)+chr(46)+str(sys.version_info.minor))' 2>$null
    if ($LASTEXITCODE -eq 0 -and $version -eq '3.10') {
        $selectedPython = $candidate
        break
    }
}
if (-not $selectedPython) {
    Write-Host 'Python 3.10 was not found. Install Python 3.10 and add it to PATH.'
    exit 1
}
$env:PYTHONUTF8 = '1'
Write-Host "Python: $selectedPython"
if ($engineArgs -contains '--check') {
    Write-Host 'Installer path and Python 3.10 check passed.'
    exit 0
}
& $selectedPython -u (Join-Path $PSScriptRoot 'setup_upgrade.py') @engineArgs
exit $LASTEXITCODE
