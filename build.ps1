param(
  [string]$Out = "",
  [switch]$SkipManifest
)

$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$src     = Join-Path $root "src"
$dist    = Join-Path $root "dist"
$release = Join-Path $root "release.json"
$fw      = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc     = Join-Path $fw "csc.exe"

if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 编译器：$csc" }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# 发版信息：版本号与更新说明的单一来源，必须与 src/BuildInfo.cs 保持一致
$rel = Get-Content -Path $release -Raw -Encoding UTF8 | ConvertFrom-Json
$buildInfo = Get-Content -Path (Join-Path $src "BuildInfo.cs") -Raw -Encoding UTF8
$m = [regex]::Match($buildInfo, 'AppVersion\s*=\s*"([0-9]+(?:\.[0-9]+)+)"')
if (-not $m.Success) { throw "BuildInfo.cs 里找不到 AppVersion 常量" }
if ($m.Groups[1].Value -ne $rel.version) {
  throw "版本不一致：release.json=$($rel.version)，BuildInfo.cs=$($m.Groups[1].Value)。请同步修改后再构建。"
}

# 程序正在运行时不能覆盖自身；验证构建用 -Out 指向另一个文件即可
$target = Join-Path $dist "ClipboardHelper.exe"
if ($Out -ne "") { $target = $Out }

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
          "/out:$target")
foreach ($r in $refs) { $args += "/r:$r" }
$args += $files

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
Write-Host "编译完成：$target" -ForegroundColor Green
$exe = Get-Item $target
$exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

if (-not $SkipManifest) {
  $hash = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLowerInvariant()
  $manifest = [ordered]@{
    version = $rel.version
    date    = $rel.date
    notes   = [string[]]@($rel.notes)
    url     = $rel.downloadUrl
    sha256  = $hash
    size    = [long]$exe.Length
  }
  $json = $manifest | ConvertTo-Json -Depth 5
  $jsonPath = Join-Path $dist "update.json"
  [System.IO.File]::WriteAllText($jsonPath, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "更新清单：$jsonPath (v$($rel.version), sha256=$hash)" -ForegroundColor Green
}
