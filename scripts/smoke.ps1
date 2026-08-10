param([string]$ExePath)

$ErrorActionPreference = "Stop"

$log = Join-Path $env:TEMP "OsuCursorWin.log"
if (Test-Path -LiteralPath $log)
{
    Remove-Item -LiteralPath $log -Force
}

$publishV2Exe = Join-Path $PSScriptRoot "..\publish-v2\OsuCursorWin.exe"
$publishExe = Join-Path $PSScriptRoot "..\publish\OsuCursorWin.exe"
$binExe = Join-Path $PSScriptRoot "..\OsuCursorWin\bin\Release\net8.0-windows\OsuCursorWin.exe"
$exe = if ($ExePath) { $ExePath } elseif (Test-Path -LiteralPath $publishV2Exe) { $publishV2Exe } elseif (Test-Path -LiteralPath $publishExe) { $publishExe } else { $binExe }
$process = Start-Process -FilePath $exe -ArgumentList "--smoke" -PassThru

if (-not $process.WaitForExit(10000))
{
    Write-Host "timed out; stopping"
    Stop-Process -Id $process.Id -Force
    Start-Sleep -Milliseconds 300
    Write-Host "exitCode=$($process.ExitCode)"
}
else
{
    Write-Host "exited exitCode=$($process.ExitCode)"
}

if (Test-Path -LiteralPath $log)
{
    Get-Content -Tail 60 -LiteralPath $log
}
else
{
    Write-Host "no log"
}
