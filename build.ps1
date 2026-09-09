# ============================================================
#  Aurora Player .NET 10 构建脚本
#  顺序：主程序(WPF) -> 卸载器(WinForms) -> 安装器(SingleFile) -> Portable(自包含单文件)
# ============================================================
$ErrorActionPreference = "Stop"
$env:PATH = "C:\Users\Jinwei\AppData\Local\Microsoft\dotnet;$env:PATH"
# 以脚本自身目录为根（而非调用时的当前目录），避免从其它目录调用时 $root 为空/错位
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location $root
Write-Host "Project root: $root"

Write-Host "=== [1/4] AuroraPlayer (WPF, .NET 10) ===" -ForegroundColor Cyan
dotnet build AuroraPlayer.csproj -c Release -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== [2/4] unins (WinForms, .NET 10) ===" -ForegroundColor Cyan
dotnet build unins.csproj -c Release -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== [3/4] AuroraPlayer-Setup (WinForms, .NET 10, SingleFile) ===" -ForegroundColor Cyan
dotnet publish installer.csproj -c Release -r win-x64 --self-contained false -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

# publish 输出到 win-x64/publish/，复制到项目根目录
$publishedExe = Join-Path (Join-Path $PSScriptRoot "win-x64") "publish\AuroraPlayer-Setup.exe"
if (Test-Path -LiteralPath $publishedExe) {
    Copy-Item -LiteralPath $publishedExe -Destination $PSScriptRoot -Force
    Write-Host "  Copied single-file installer to project root"
} else {
    Write-Host "  WARN: installer not found at $publishedExe" -ForegroundColor Yellow
}

Write-Host "`n=== [4/4] Portable (self-contained single file) ===" -ForegroundColor Cyan
dotnet publish AuroraPlayer.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishDir=win-x64\portable\ -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "PORTABLE PUBLISH FAILED" -ForegroundColor Red; exit 1 }
$portableExe = Join-Path (Join-Path $PSScriptRoot "win-x64\portable") "AuroraPlayer.exe"
if (Test-Path -LiteralPath $portableExe) {
    Copy-Item -LiteralPath $portableExe -Destination (Join-Path $PSScriptRoot "Aurora-x64-Portable.exe") -Force
    Write-Host "  Copied portable exe to project root (Aurora-x64-Portable.exe)"
} else {
    Write-Host "  WARN: portable exe not found at $portableExe" -ForegroundColor Yellow
}

Write-Host "`n=== BUILD OK ===" -ForegroundColor Green
Write-Host "Outputs:"
Get-ChildItem build -Filter "*.exe" | ForEach-Object { Write-Host "  build\$($_.Name) ($([math]::Round($_.Length/1KB,1)) KB)" }
Get-ChildItem build -Filter "*.dll" | ForEach-Object { Write-Host "  build\$($_.Name) ($([math]::Round($_.Length/1KB,1)) KB)" }
if (Test-Path "AuroraPlayer-Setup.exe") {
    Write-Host "  AuroraPlayer-Setup.exe ($([math]::Round((Get-Item 'AuroraPlayer-Setup.exe').Length/1KB,1)) KB)"
}
