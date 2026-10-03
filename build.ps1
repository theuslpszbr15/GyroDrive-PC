$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('Steering.cs', 'Controller.cs', 'VirtualPad.cs', 'App.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /platform:x64 /target:exe /main:GyroDrive.Program /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\GyroDrive.Checks.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
& "$PSScriptRoot\GyroDrive.Checks.exe" --self-test
if ($LASTEXITCODE -ne 0) { throw 'Self tests failed' }
& $compiler /nologo /platform:x64 /target:winexe /main:GyroDrive.Program /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\GyroDrive.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'GUI compilation failed' }