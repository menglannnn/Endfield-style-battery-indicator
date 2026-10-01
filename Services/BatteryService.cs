using System;
using System.Management;
using System.Runtime.Versioning;
using EndfieldCharge.Settings;

namespace EndfieldCharge.Services;

/// <summary>一次电池采样结果。</summary>
public sealed record BatterySnapshot(
    double RemainingWh,
    double FullWh,
    int Percent,
    bool AcOnline,
    bool Charging)
{
    /// <summary>充/放电功率（瓦）。正=充电，负=放电；未知为 null。</summary>
    public double? RateWatts { get; init; }

    /// <summary>设计容量（mWh），用于计算健康度。</summary>
    public double? DesignCapacityWh { get; init; }

    /// <summary>电池健康度百分比（当前满充容量 / 设计容量）。</summary>
    public double? HealthPercent => DesignCapacityWh.HasValue && DesignCapacityWh.Value > 0
        ? Math.Round(FullWh / DesignCapacityWh.Value * 100, 1)
        : null;

    /// <summary>剩余时间估计；未知为 null。</summary>
    public TimeSpan? EstimatedRemaining { get; init; }

    public bool HasBattery => FullWh > 0;

    /// <summary>PC 主机硬件总功耗（瓦）。当无电池时使用。</summary>
    public double? PowerWatts { get; init; }

    /// <summary>PC 主机 CPU 占用率（百分比）。</summary>
    public double? CpuUsagePercent { get; init; }

    /// <summary>电源设备报告的功率（瓦）；硬件未提供时为 null。</summary>
    public double? PowerSupplyWatts { get; init; }
}

/// <summary>
/// 电池与功耗读取。笔记本模式只走电池；台式机模式只走硬件功耗。
/// </summary>
[SupportedOSPlatform("windows")]
public static class BatteryService
{
    /// <summary>按设备模式取快照。笔记本无电池或台式机读不到功耗时返回 null。</summary>
    public static BatterySnapshot? GetSnapshot(DeviceMode mode)
    {
        if (mode == DeviceMode.Desktop)
            return GetDesktopPower();

        if (TryFromPowerProf(out var snap) && snap is not null)
            return snap;

        return TryFromWmi();
    }

    private static BatterySnapshot? GetDesktopPower()
    {
        var hardware = HardwareService.GetSnapshot();
        if (!hardware.TotalPowerWatts.HasValue && !hardware.CpuUsagePercent.HasValue)
            return null;

        return new BatterySnapshot(
            RemainingWh: 0,
            FullWh: 0,
            Percent: 0,
            AcOnline: true,
            Charging: false)
        {
            PowerWatts = hardware.TotalPowerWatts,
            CpuUsagePercent = hardware.CpuUsagePercent,
            PowerSupplyWatts = hardware.PowerSupplyWatts
        };
    }

    private static bool TryFromPowerProf(out BatterySnapshot? snapshot)
    {
        snapshot = null;
        if (!PowerNative.TryGetBatteryState(out var s))
            return false;

        // 有的固件 MaxCapacity 给的是"设计容量"而非"当前满充容量"，这里只做合理性校验
        if (s.MaxCapacity == 0)
            return false;

        double fullWh = s.MaxCapacity / 1000.0;
        double remainingWh = s.RemainingCapacity / 1000.0;

        // 百分比直接用容量比算，比 EstimatedChargeRemaining 更连续（后者常为整数跳变）
        int percent = (int)Math.Round(remainingWh / fullWh * 100.0);
        percent = Math.Clamp(percent, 0, 100);

        snapshot = new BatterySnapshot(
            RemainingWh: remainingWh,
            FullWh: fullWh,
            Percent: percent,
            AcOnline: s.AcOnLine != 0,
            Charging: s.Charging != 0)
        {
            RateWatts = s.Rate == 0 ? null : s.Rate / 1000.0,
            EstimatedRemaining = s.EstimatedTime is 0 or 0x80000000
                ? null
                : TimeSpan.FromSeconds(s.EstimatedTime),
            // powrprof 不提供设计容量，健康度仅 WMI 路径可读
        };
        return true;
    }

    private static BatterySnapshot? TryFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT EstimatedChargeRemaining, FullChargeCapacity, DesignCapacity, BatteryStatus FROM Win32_Battery");

            foreach (ManagementObject mo in searcher.Get())
            {
                int? pct = ReadUInt16(mo["EstimatedChargeRemaining"]);
                uint? fullMwh = ReadUInt32(mo["FullChargeCapacity"]) ?? ReadUInt32(mo["DesignCapacity"]);
                uint? designMwh = ReadUInt32(mo["DesignCapacity"]);

                if (pct is null || fullMwh is 0 or null)
                    continue;

                double fullWh = fullMwh.Value / 1000.0;
                double remainingWh = fullWh * pct.Value / 100.0;

                // BatteryStatus: 2 = 正在充电, 1 = 放电, 其他见 WMI 文档
                ushort status = ReadUInt16(mo["BatteryStatus"]) ?? 0;

                return new BatterySnapshot(
                    RemainingWh: remainingWh,
                    FullWh: fullWh,
                    Percent: Math.Clamp(pct.Value, 0, 100),
                    AcOnline: status is 2 or 6 or 7 or 8 or 9,
                    Charging: status is 2 or 6 or 7 or 8 or 9)
                {
                    DesignCapacityWh = designMwh.HasValue && designMwh > 0
                        ? designMwh.Value / 1000.0
                        : null,
                };
            }
        }
        catch
        {
            // WMI 被禁用或服务未启动时静默失败
        }

        return null;

        static ushort? ReadUInt16(object? v) => v is null ? null : Convert.ToUInt16(v);
        static uint? ReadUInt32(object? v) => v is null ? null : Convert.ToUInt32(v);
    }
}
