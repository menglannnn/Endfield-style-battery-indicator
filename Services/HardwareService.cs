using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace EndfieldCharge.Services;

public sealed record HardwarePowerSnapshot(
    double? TotalPowerWatts,
    double? PowerSupplyWatts,
    double? CpuUsagePercent,
    double? CpuPowerWatts = null,
    double? GpuPowerWatts = null);

public static class HardwareService
{
    private static readonly Computer? _computer;
    private static readonly object _sync = new();
    private static readonly Lazy<NvmlPowerReader> _nvml = new(() => new NvmlPowerReader());
    private static bool _initialized;
    private static bool _loggedSources;
    private static int? _memoryStickCount;

    static HardwareService()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsPsuEnabled = true,
                IsPowerMonitorEnabled = true,
            };
            _computer.Open();
            _initialized = true;
            _memoryStickCount = CountMemorySticks(_computer);
        }
        catch (Exception ex)
        {
            Logger.Warn($"HardwareService init failed: {ex.Message}");
        }
    }

    public static HardwarePowerSnapshot GetSnapshot()
    {
        if (!_initialized || _computer is null)
            return new HardwarePowerSnapshot(null, null, null);

        var totals = new PowerTotals();
        lock (_sync)
        {
            try
            {
                foreach (var hardware in _computer.Hardware)
                    Visit(hardware, totals, underGpu: false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"HardwareService read failed: {ex.Message}");
            }
        }

        _nvml.Value.TryRead(out var nvmlWatts, out var nvmlOk, out var nvmlLimit);

        double? cpu = ResolveCpu(totals);
        double gpu = ResolveGpu(totals, nvmlOk, nvmlWatts, nvmlLimit);
        double dram = totals.HasDram ? totals.Dram : EstimateMemory(totals.CpuLoad);
        double storage = totals.StoragePower + totals.StorageEstimate;
        double board = totals.Board;
        double vrm = ResolveVrmLoss(totals, ref cpu);
        double residual = totals.HasBoard ? 4d : 14d;
        double components = (cpu ?? 0) + gpu + dram + storage + totals.FanPower + board + vrm + residual;

        double? supply = totals.Psu is > 20d ? totals.Psu : totals.PsuRails > 20d ? totals.PsuRails : null;
        double total = supply is double psu && (!totals.HasCpuPackage || psu >= (cpu ?? 0) * 0.8d)
            ? psu
            : components;

        if (!_loggedSources)
        {
            _loggedSources = true;
            Logger.Info(
                $"Power cpu={cpu:F1} gpu={gpu:F1} dram={dram:F1} storage={storage:F1} " +
                $"fans={totals.FanPower:F1} board={board:F1} vrm={vrm:F1} residual={residual:F1} " +
                $"psu={supply:F1} total={total:F1} nvml={(nvmlOk ? nvmlWatts.ToString("F1") : "off")}");
        }

        if (total <= 0)
            return new HardwarePowerSnapshot(null, supply, totals.CpuLoad, cpu, gpu > 0 ? gpu : null);

        return new HardwarePowerSnapshot(
            Math.Round(total, 1),
            supply is null ? null : Math.Round(supply.Value, 1),
            totals.CpuLoad,
            cpu is null ? null : Math.Round(cpu.Value, 1),
            gpu > 0 ? Math.Round(gpu, 1) : null);
    }

    private static void Visit(IHardware hardware, PowerTotals totals, bool underGpu)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hardware update failed ({hardware.Name}): {ex.Message}");
            return;
        }

        string kind = hardware.HardwareType.ToString();
        bool isCpu = kind.Equals("Cpu", StringComparison.OrdinalIgnoreCase);
        bool isNvidia = kind.Equals("GpuNvidia", StringComparison.OrdinalIgnoreCase);
        bool isGpu = underGpu || kind.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
        bool isStorage = kind.Equals("Storage", StringComparison.OrdinalIgnoreCase);
        bool isPsu = kind.Equals("Psu", StringComparison.OrdinalIgnoreCase);
        bool isBoard = kind.Equals("Motherboard", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("SuperIO", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("EmbeddedController", StringComparison.OrdinalIgnoreCase);

        var node = isGpu ? new GpuNode { Nvidia = isNvidia, Integrated = kind.Equals("GpuIntel", StringComparison.OrdinalIgnoreCase) } : null;
        double? storageActivity = null;
        bool storageActivitySeen = false;
        bool storageMeasured = false;
        double cpuPackage = 0;
        double cpuParts = 0;
        bool nodePackage = false;
        bool nodeParts = false;

        foreach (var sensor in hardware.Sensors)
        {
            if (!TryRead(sensor, out var value))
                continue;

            string name = sensor.Name ?? string.Empty;
            bool limit = IsLimitName(name);

            if (isCpu && sensor.SensorType == SensorType.Load && IsCpuTotalLoad(name))
                totals.CpuLoad = Math.Clamp(value, 0d, 100d);

            if (isCpu && sensor.SensorType == SensorType.Voltage && name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                totals.CpuVoltage = Math.Max(totals.CpuVoltage ?? 0, value);

            if (isCpu && sensor.SensorType == SensorType.Clock &&
                (name.Contains("Core", StringComparison.OrdinalIgnoreCase) || name.Contains("CPU", StringComparison.OrdinalIgnoreCase)))
                totals.CpuClock = Math.Max(totals.CpuClock ?? 0, value);

            if (sensor.SensorType == SensorType.Power && value is > 0 and < 2000)
            {
                if (isCpu)
                    AbsorbCpuPower(name, value, limit, ref nodePackage, ref cpuPackage, ref nodeParts, ref cpuParts, totals);
                else if (node is not null)
                    AbsorbGpuPower(name, value, limit, node);
                else if (isPsu)
                    AbsorbPsuPower(name, value, totals);
                else if (isStorage && !limit)
                {
                    totals.StoragePower += value;
                    storageMeasured = true;
                }
                else if (isBoard && !limit)
                    AbsorbBoardPower(name, value, totals);
                else if (!limit && IsDramName(name))
                    totals.AddDram(value);
            }

            if (node is not null && sensor.SensorType == SensorType.Load && IsGpuCoreLoad(name))
                node.Load = Math.Max(node.Load ?? 0, Math.Clamp(value, 0d, 100d));

            if (node is not null && sensor.SensorType == SensorType.Voltage &&
                (name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || name.Contains("Core", StringComparison.OrdinalIgnoreCase)))
                node.Voltage = Math.Max(node.Voltage ?? 0, value);

            if (node is not null && sensor.SensorType == SensorType.Clock &&
                (name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || name.Contains("Core", StringComparison.OrdinalIgnoreCase)))
                node.Clock = Math.Max(node.Clock ?? 0, value);

            if (!isGpu && sensor.SensorType == SensorType.Fan && value > 0 &&
                !name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                totals.FanPower += EstimateFan(name, value);

            if (isStorage && sensor.SensorType == SensorType.Load &&
                name.Contains("Activity", StringComparison.OrdinalIgnoreCase))
            {
                storageActivity = Math.Clamp(value, 0d, 100d);
                storageActivitySeen = true;
            }
        }

        if (nodePackage)
        {
            totals.HasCpuPackage = true;
            totals.CpuPackage += cpuPackage;
        }
        else if (nodeParts)
        {
            totals.HasCpuParts = true;
            totals.CpuParts += cpuParts;
        }

        if (node is not null && (node.Power.HasValue || node.Load.HasValue || isNvidia || isGpu && !underGpu))
            totals.Gpus.Add(node);

        if (isStorage && !storageMeasured)
            totals.StorageEstimate += EstimateStorage(hardware.Name, storageActivitySeen ? storageActivity : null);

        foreach (var sub in hardware.SubHardware)
            Visit(sub, totals, isGpu);
    }

    private static void AbsorbCpuPower(
        string name,
        double value,
        bool limit,
        ref bool nodePackage,
        ref double cpuPackage,
        ref bool nodeParts,
        ref double cpuParts,
        PowerTotals totals)
    {
        if (limit)
        {
            totals.CpuLimit = Math.Max(totals.CpuLimit ?? 0, value);
            return;
        }

        if (IsDramName(name))
        {
            totals.AddDram(value);
            return;
        }

        if (IsPackageName(name))
        {
            nodePackage = true;
            cpuPackage = Math.Max(cpuPackage, value);
            return;
        }

        if (IsCpuPartName(name) && !name.Contains('#'))
        {
            nodeParts = true;
            cpuParts += value;
        }
    }

    private static void AbsorbGpuPower(string name, double value, bool limit, GpuNode node)
    {
        if (limit)
        {
            node.Limit = Math.Max(node.Limit ?? 0, value);
            return;
        }

        if (name.Contains("Board", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("板", StringComparison.OrdinalIgnoreCase))
        {
            node.Power = Math.Max(node.Power ?? 0, value);
            node.PowerScore = 100;
            return;
        }

        int score = name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Total", StringComparison.OrdinalIgnoreCase)
            ? 80
            : 40;
        if (score >= node.PowerScore)
        {
            node.Power = value;
            node.PowerScore = score;
        }
    }

    private static void AbsorbPsuPower(string name, double value, PowerTotals totals)
    {
        if (IsLimitName(name))
            return;

        bool total = name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Output", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Input", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Power", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("电源", StringComparison.OrdinalIgnoreCase);
        if (total)
            totals.Psu = Math.Max(totals.Psu ?? 0, value);
        else
            totals.PsuRails += value;
    }

    private static void AbsorbBoardPower(string name, double value, PowerTotals totals)
    {
        if (IsDramName(name))
        {
            totals.AddDram(value);
            return;
        }

        if (name.Contains("VRM", StringComparison.OrdinalIgnoreCase))
        {
            totals.HasVrm = true;
            totals.VrmInput = Math.Max(totals.VrmInput ?? 0, value);
            return;
        }

        if (name.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PCH", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("LAN", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RGB", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("LED", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("芯片组", StringComparison.OrdinalIgnoreCase))
        {
            totals.Board += value;
            totals.HasBoard = true;
        }
    }

    private static double ResolveVrmLoss(PowerTotals totals, ref double? cpu)
    {
        if (totals.VrmInput is double input)
        {
            if (totals.HasCpuPackage && cpu is double package)
                return Math.Clamp(input - package, 0d, 40d);

            if (!totals.HasCpuPackage && !totals.HasCpuParts)
                cpu = input;

            return 0d;
        }

        if (cpu is double measured && !totals.HasVrm && (totals.HasCpuPackage || totals.HasCpuParts))
            return Math.Min(30d, measured * 0.08d);

        return 0d;
    }

    private static double? ResolveCpu(PowerTotals totals)
    {
        if (totals.HasCpuPackage)
            return totals.CpuPackage;
        if (totals.HasCpuParts)
            return totals.CpuParts;
        if (totals.CpuLoad is null && totals.CpuClock is null)
            return null;

        return EstimateCpu(totals.CpuLoad ?? 0, totals.CpuVoltage, totals.CpuClock, totals.CpuLimit);
    }

    private static double ResolveGpu(PowerTotals totals, bool nvmlOk, double nvmlWatts, double? nvmlLimit)
    {
        double sum = 0;
        foreach (var gpu in totals.Gpus)
        {
            if (gpu.Nvidia && nvmlOk)
                continue;

            if (gpu.Power is double measured)
            {
                sum += measured;
                continue;
            }

            double? limit = gpu.Limit ?? (gpu.Nvidia ? nvmlLimit : null);
            if (gpu.Load is null && limit is null)
                continue;

            sum += EstimateGpu(gpu.Load ?? 0, gpu.Voltage, gpu.Clock, limit, gpu.Integrated);
        }

        if (nvmlOk)
            sum += nvmlWatts;

        return sum;
    }

    private static double EstimateCpu(double usagePercent, double? voltage, double? clockMHz, double? limitWatts)
    {
        double idle = 12d;
        double rated = Math.Clamp(limitWatts ?? 200d, 35d, 400d);
        double load = Math.Clamp(usagePercent / 100d, 0d, 1d);
        double voltageFactor = Math.Clamp(Math.Pow((voltage ?? 1.15d) / 1.15d, 2d), 0.6d, 1.5d);
        double clockFactor = Math.Clamp((clockMHz ?? 4000d) / 4000d, 0.4d, 1.8d);
        return Math.Clamp(idle + (rated - idle) * load * voltageFactor * clockFactor, idle * 0.5d, rated * 1.05d);
    }

    private static double EstimateGpu(double loadPercent, double? voltage, double? clockMHz, double? limitWatts, bool integrated)
    {
        double idle = integrated ? 4d : 15d;
        double rated = Math.Clamp(limitWatts ?? (integrated ? 25d : 220d), integrated ? 8d : 30d, integrated ? 80d : 600d);
        double load = Math.Clamp(loadPercent / 100d, 0d, 1d);
        double voltageFactor = Math.Clamp(Math.Pow((voltage ?? 0.95d) / 0.95d, 2d), 0.65d, 1.4d);
        double clockFactor = Math.Clamp((clockMHz ?? 1800d) / 1800d, 0.35d, 1.6d);
        return Math.Clamp(idle + (rated - idle) * load * voltageFactor * clockFactor, idle * 0.5d, rated * 1.05d);
    }

    private static double EstimateMemory(double? cpuLoadPercent)
    {
        int sticks = _memoryStickCount is > 0 ? _memoryStickCount.Value : 2;
        double load = Math.Clamp((cpuLoadPercent ?? 15d) / 100d, 0d, 1d);
        return sticks * (2.4d + 2.6d * load);
    }

    private static double EstimateStorage(string? name, double? activityPercent)
    {
        double activity = Math.Clamp((activityPercent ?? 8d) / 100d, 0d, 1d);
        string text = name ?? string.Empty;
        bool hdd = !text.Contains("SSD", StringComparison.OrdinalIgnoreCase) &&
                   !text.Contains("NVMe", StringComparison.OrdinalIgnoreCase) &&
                   (text.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("WDC", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Seagate", StringComparison.OrdinalIgnoreCase));
        if (hdd)
            return 5.5d + 4.5d * activity;
        if (text.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return 2.8d + 5.5d * activity;
        return 1.8d + 3.2d * activity;
    }

    private static double EstimateFan(string name, double rpm)
    {
        bool pump = name.Contains("Pump", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("AIO", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("水泵", StringComparison.OrdinalIgnoreCase);
        if (pump)
            return Math.Clamp(2.5d + 7d * Math.Pow(rpm / 2800d, 2.2d), 1.5d, 15d);

        return Math.Clamp(0.3d + 2.4d * Math.Pow(rpm / 1600d, 3d), 0.15d, 8d);
    }

    private static int CountMemorySticks(Computer computer)
    {
        try
        {
            int count = 0;
            foreach (var device in computer.SMBios.MemoryDevices)
            {
                double size = Convert.ToDouble(device.Size);
                if (size > 0 && size != 0xFFFF)
                    count++;
            }

            return count;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory stick count failed: {ex.Message}");
            return 0;
        }
    }

    private static bool TryRead(ISensor sensor, out double value)
    {
        value = 0;
        if (sensor.Value is null)
            return false;

        try
        {
            value = Convert.ToDouble(sensor.Value);
        }
        catch
        {
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return false;

        if (value == 0 && sensor.SensorType != SensorType.Load)
            return false;

        return true;
    }

    private static bool IsLimitName(string name) =>
        name.Contains("TDP", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PL1", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PL2", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("上限", StringComparison.OrdinalIgnoreCase);

    private static bool IsPackageName(string name) =>
        name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("封装", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PPT", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuPartName(string name) =>
        name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SoC", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Uncore", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);

    private static bool IsDramName(string name) =>
        name.Contains("DRAM", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DIMM", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("内存", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuTotalLoad(string name) =>
        name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("总", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuCoreLoad(string name) =>
        name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("3D", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("核心", StringComparison.OrdinalIgnoreCase);

    private sealed class GpuNode
    {
        public bool Nvidia;
        public bool Integrated;
        public double? Power;
        public int PowerScore = -1;
        public double? Load;
        public double? Voltage;
        public double? Clock;
        public double? Limit;
    }

    private sealed class PowerTotals
    {
        public double CpuPackage;
        public bool HasCpuPackage;
        public double CpuParts;
        public bool HasCpuParts;
        public double? CpuLimit;
        public double? CpuLoad;
        public double? CpuVoltage;
        public double? CpuClock;
        public List<GpuNode> Gpus { get; } = new();
        public double Dram;
        public bool HasDram;
        public double Board;
        public bool HasBoard;
        public bool HasVrm;
        public double StoragePower;
        public double StorageEstimate;
        public double FanPower;
        public double? Psu;
        public double PsuRails;
        public double? VrmInput;

        public void AddDram(double watts)
        {
            Dram += watts;
            HasDram = true;
        }
    }

    private sealed class NvmlPowerReader
    {
        private const int NvmlSuccess = 0;
        private const int NvmlErrorNotSupported = 3;
        private bool _attempted;
        private bool _initialized;
        private bool _powerUnsupportedReported;

        public bool TryRead(out double watts, out bool hasUsage, out double? limitWatts)
        {
            watts = 0;
            hasUsage = false;
            limitWatts = null;
            try
            {
                if (!_attempted)
                {
                    _attempted = true;
                    _initialized = nvmlInit_v2() == NvmlSuccess;
                }

                if (!_initialized || nvmlDeviceGetCount_v2(out uint count) != NvmlSuccess)
                    return false;

                double limitSum = 0;
                int limitCount = 0;
                for (uint index = 0; index < count; index++)
                {
                    if (nvmlDeviceGetHandleByIndex_v2(index, out IntPtr device) != NvmlSuccess)
                        continue;

                    int powerResult = nvmlDeviceGetPowerUsage(device, out uint milliwatts);
                    if (powerResult == NvmlSuccess)
                    {
                        watts += milliwatts / 1000d;
                        hasUsage = true;
                    }
                    else if (powerResult == NvmlErrorNotSupported && !_powerUnsupportedReported)
                    {
                        _powerUnsupportedReported = true;
                        Logger.Warn("NVML GPU power is not supported by the current NVIDIA driver.");
                    }

                    if (nvmlDeviceGetPowerManagementLimit(device, out uint limitMw) == NvmlSuccess && limitMw > 0)
                    {
                        limitSum += limitMw / 1000d;
                        limitCount++;
                    }
                }

                if (limitCount > 0)
                    limitWatts = limitSum / limitCount;

                return hasUsage && watts > 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlInit_v2")]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetCount_v2")]
        private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetPowerUsage")]
        private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);
    }
}
