$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "OsuCursorWin\OsuCursorWin.csproj"
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

dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output

Write-Host "Built: $output\OsuCursorWin.exe"
