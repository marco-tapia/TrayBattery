# Builds TrayBattery.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wmd  = "$env:WINDIR\System32\WinMetadata"

$refs = @(
  'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
  "$fw\System.Runtime.dll",
  "$wmd\Windows.Foundation.winmd", "$wmd\Windows.Devices.winmd"
) | ForEach-Object { "/r:$_" }

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu `
  "/out:$here\TrayBattery.exe" "/win32manifest:$here\app.manifest" @refs "$here\TrayBattery.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $here\TrayBattery.exe"
