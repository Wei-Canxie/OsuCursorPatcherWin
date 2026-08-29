using System;
using System.ServiceProcess;

namespace OsuCursorWin;

/// <summary>
/// Manages the OsuCursor Windows Service.
/// Provides start/stop and auto-start configuration.
/// </summary>
internal static class ServiceManager
{
    private const string ServiceName = "OsuCursorService";
    private const string ServiceDisplayName = "OsuCursor Service";
    private const string ServiceDescription = "Manages the osu! custom cursor overlay.";

    /// <summary>
    /// Check if the service is installed.
    /// </summary>
    internal static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status; // Will throw if not installed
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Check if the service is running.
    /// </summary>
    internal static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start the service.
    /// </summary>
    internal static bool Start()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to start service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop the service.
    /// </summary>
    internal static bool Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to stop service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Set the service to start automatically with Windows.
    /// </summary>
    internal static bool SetAutoStart(bool enable)
    {
        try
        {
            // Use sc.exe to change startup type
            var startType = enable ? "auto" : "demand";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config {ServiceName} start= {startType}",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to set auto-start: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if the service is set to start automatically.
    /// </summary>
    internal static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key != null)
            {
                var start = key.GetValue("Start");
                return start is int startInt && startInt == 2; // SERVICE_AUTO_START
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Install the service (requires admin).
    /// </summary>
    internal static bool Install(string exePath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"create {ServiceName} binPath= \"{exePath}\" start= auto DisplayName= \"{ServiceDisplayName}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(10000);

            if (proc?.ExitCode == 0)
            {
                // Set description
                psi.Arguments = $"description {ServiceName} \"{ServiceDescription}\"";
                proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
            }

            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to install service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Uninstall the service (requires admin).
    /// </summary>
    internal static bool Uninstall()
    {
        try
        {
            // Stop first
            Stop();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"delete {ServiceName}",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to uninstall service: {ex.Message}");
            return false;
        }
    }
}
