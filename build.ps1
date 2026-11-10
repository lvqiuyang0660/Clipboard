$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src"
$dist = Join-Path $root "dist"
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc  = Join-Path $fw "csc.exe"

if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 编译器：$csc" }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$refs = @(
  "$fw\WPF\PresentationFramework.dll",
  "$fw\WPF\PresentationCore.dll",
  "$fw\WPF\WindowsBase.dll",
  "$fw\System.Xaml.dll",
  "$fw\System.Windows.Forms.dll",
  "$fw\System.Drawing.dll",
  "$fw\System.Security.dll",
  "$fw\System.Core.dll",
  "$fw\System.dll",
  "$fw\System.Xml.dll"
)

$files = Get-ChildItem (Join-Path $src "*.cs") | ForEach-Object { $_.FullName }
$args = @("/nologo", "/target:winexe", "/optimize+", "/codepage:65001", "/debug-",
          "/win32manifest:$root\app.manifest",
          "/out:$dist\ClipboardHelper.exe")
foreach ($r in $refs) { $args += "/r:$r" }
$args += $files

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
Write-Host "编译完成：$dist\ClipboardHelper.exe" -ForegroundColor Green
Get-Item "$dist\ClipboardHelper.exe" | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
