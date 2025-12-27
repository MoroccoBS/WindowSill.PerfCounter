using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

using Windows.Win32;
using System.Runtime.InteropServices.ComTypes;
using Windows.Win32.System.SystemInformation;

using LibreHardwareMonitor.Hardware; 

namespace WindowSill.PerfCounter.Services;


[Export(typeof(IPerformanceMonitorService))]
public class PerformanceMonitorService : IPerformanceMonitorService, IDisposable
{
    private readonly Timer _timer;
    private readonly GpuMonitorService _gpuMonitor;
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;
    private readonly object _lockObject = new();
    private bool _isMonitoring;

    public event EventHandler<PerformanceDataEventArgs>? PerformanceDataUpdated;

    private readonly Computer _computer; // Add this field

    [ImportingConstructor]
    public PerformanceMonitorService()
    {
        _timer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        _gpuMonitor = new GpuMonitorService();

        // Initialize LibreHardwareMonitor
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };
        
        try 
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            // Handle lacking admin privileges or initialization errors
            System.Diagnostics.Debug.WriteLine($"Failed to open hardware monitor: {ex.Message}");
        }


        InitializeCpuUsageTracking();
    }

    public void Dispose()
    {
        StopMonitoring();
        _timer?.Dispose();
        _gpuMonitor?.Dispose();
        _computer?.Close(); // Close the computer connection
    }

    public void StartMonitoring()
    {
        lock (_lockObject)
        {
            if (!_isMonitoring)
            {
                _isMonitoring = true;
                InitializeCpuUsageTracking();
                _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
        }
    }

    public void StopMonitoring()
    {
        lock (_lockObject)
        {
            if (_isMonitoring)
            {
                _isMonitoring = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    public PerformanceData GetCurrentPerformanceData()
    {
        double cpuUsage = GetCpuUsage();
        double memoryUsage = GetMemoryUsage();
        double? gpuUsage = _gpuMonitor.GetGpuUsage();

        // Get Temperatures
        double? cpuTemp = GetCpuTemperature();
        double? gpuTemp = GetGpuTemperature();

        return new PerformanceData(
            cpuUsage,
            cpuTemp,     // Pass new data
            memoryUsage,
            gpuUsage,
            gpuTemp      // Pass new data
        );
    }

    private void OnTimerCallback(object? state)
    {
        if (!_isMonitoring)
        {
            return;
        }

        try
        {
            PerformanceData performanceData = GetCurrentPerformanceData();
            PerformanceDataUpdated?.Invoke(this, new PerformanceDataEventArgs(performanceData));
        }
        catch (Exception ex)
        {
            // Log error but continue monitoring
            System.Diagnostics.Debug.WriteLine($"Error getting performance data: {ex.Message}");
        }
    }

    private void InitializeCpuUsageTracking()
    {
        unsafe
        {
            FILETIME idleTime, kernelTime, userTime;
            if (PInvoke.GetSystemTimes(&idleTime, &kernelTime, &userTime))
            {
                _lastIdleTime = FileTimeToUInt64(idleTime);
                _lastKernelTime = FileTimeToUInt64(kernelTime);
                _lastUserTime = FileTimeToUInt64(userTime);
            }
        }
    }

    private double GetCpuUsage()
    {
        unsafe
        {
            FILETIME idleTime, kernelTime, userTime;
            if (!PInvoke.GetSystemTimes(&idleTime, &kernelTime, &userTime))
            {
                return 0.0;
            }

            ulong currentIdleTime = FileTimeToUInt64(idleTime);
            ulong currentKernelTime = FileTimeToUInt64(kernelTime);
            ulong currentUserTime = FileTimeToUInt64(userTime);

            ulong idleDiff = currentIdleTime - _lastIdleTime;
            ulong kernelDiff = currentKernelTime - _lastKernelTime;
            ulong userDiff = currentUserTime - _lastUserTime;

            ulong totalSys = kernelDiff + userDiff;
            ulong totalCpu = totalSys - idleDiff;

            double cpuUsage = 0.0;
            if (totalSys > 0)
            {
                cpuUsage = (double)totalCpu * 100.0 / totalSys;
            }

            // Update for next calculation
            _lastIdleTime = currentIdleTime;
            _lastKernelTime = currentKernelTime;
            _lastUserTime = currentUserTime;

            return Math.Max(0.0, Math.Min(100.0, cpuUsage));
        }
    }

    private static double GetMemoryUsage()
    {
        var memoryStatus = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!PInvoke.GlobalMemoryStatusEx(ref memoryStatus))
        {
            return 0.0;
        }

        double memoryUsage = (double)memoryStatus.dwMemoryLoad;

        return memoryUsage;
    }

    private double? GetCpuTemperature()
    {
        try
        {
            // You must call Accept or Update to refresh sensor data
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    hardware.Update();
                    // Look for the "Package" temperature or the average of cores
                    var tempSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package")) 
                                     ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);

                    return tempSensor?.Value;
                }
            }
        }
        catch { /* Ignore errors */ }
        return null;
    }

    private double? GetGpuTemperature()
    {
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                // Check for Nvidia, AMD or Intel GPUs
                if (hardware.HardwareType == HardwareType.GpuNvidia || 
                    hardware.HardwareType == HardwareType.GpuAmd || 
                    hardware.HardwareType == HardwareType.GpuIntel)
                {
                    hardware.Update();
                    var tempSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                    if (tempSensor?.Value != null)
                    {
                        return tempSensor.Value;
                    }
                }
            }
        }
        catch { /* Ignore errors */ }
        return null;
    }

    private static ulong FileTimeToUInt64(FILETIME fileTime)
    {
        return ((ulong)(uint)fileTime.dwHighDateTime << 32) | (ulong)(uint)fileTime.dwLowDateTime;
    }
}
