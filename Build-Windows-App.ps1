$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /optimize+ /platform:anycpu /out:JackPedalControl.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll JackPedalControl.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Close the running app before rebuilding.' }
} finally { Pop-Location }
