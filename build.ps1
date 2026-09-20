$ErrorActionPreference = 'Stop'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $frameworkRoot 'csc.exe'
$speech = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll'
if (!(Test-Path $compiler) -or !(Test-Path $speech)) {
    throw 'Required Windows .NET Framework compiler or System.Speech is unavailable.'
}
$destination = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $destination | Out-Null
& $compiler /nologo /target:winexe ("/out:" + (Join-Path $destination 'StackAlert-v2.exe')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ("/reference:" + $speech) (Join-Path $PSScriptRoot 'src\StackAlert.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Host 'Build complete: dist/StackAlert-v2.exe'
