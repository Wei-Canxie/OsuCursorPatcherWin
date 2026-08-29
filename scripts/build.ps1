$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
# WinUI 3 主程序（OsuCursorWin/ 为旧版 WPF 参考实现）
$project = Join-Path $root "OsuCursorWin3\OsuCursorWin3.csproj"
$output = Join-Path $root "publish"
$outputV2 = Join-Path $root "publish-v2"

$publishExe = Join-Path $output "OsuCursorWin.exe"
$locked = $false
if (Test-Path -LiteralPath $publishExe)
{
    try
    {
        $stream = [System.IO.File]::Open(
            $publishExe,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $stream.Dispose()
    }
    catch
    {
        $locked = $true
    }
}

if ($locked)
{
    Write-Host "publish\OsuCursorWin.exe is in use; building to publish-v2 instead."
    $output = $outputV2
}

# WinUI 3 项目不支持 PublishSingleFile（PRI 资源打包限制），
# 改用 dotnet build 后从 bin 输出目录复制产物。
$binDir = Join-Path $root "OsuCursorWin3\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
$builtExe = Join-Path $binDir "OsuCursorWin.exe"

dotnet build $project -c Release -p:Platform=x64

if (-not (Test-Path -LiteralPath $builtExe))
{
    throw "Build succeeded but output exe not found: $builtExe"
}

if (Test-Path -LiteralPath $output)
{
    Remove-Item -Recurse -Force $output
}
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -Path (Join-Path $binDir "*") -Destination $output -Recurse

Write-Host "Built: $output\OsuCursorWin.exe"
