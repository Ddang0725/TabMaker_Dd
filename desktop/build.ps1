$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$projectRoot = Split-Path -Parent $sourceRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$output = Join-Path $projectRoot 'TabMaker_Dd.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output" "/win32manifest:$sourceRoot\app.manifest" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Xaml.dll /reference:System.Web.Extensions.dll `
    "/reference:$framework\WPF\WindowsBase.dll" `
    "/reference:$framework\WPF\PresentationCore.dll" `
    "/reference:$framework\WPF\PresentationFramework.dll" `
    "/resource:$sourceRoot\MainWindow.xaml,MainWindow.xaml" `
    "/resource:$projectRoot\engine\transcribe.py,transcribe.py" `
    "$sourceRoot\Audio.cs" "$sourceRoot\Analysis.cs" "$sourceRoot\TabView.cs" "$sourceRoot\WavePlayer.cs" "$sourceRoot\Program.cs" "$sourceRoot\Rhythm.cs" "$sourceRoot\AiEngine.cs" "$sourceRoot\SelfTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
Write-Output "Built: $output"
