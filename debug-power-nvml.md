# Debug Session: power-nvml

Status: [OPEN]

## Symptom

The PC HUD still displays `--W` after the NVML integration build.

## Hypotheses

1. NVML initialization fails or reports no usable device.
2. NVML device power usage returns an error or zero value.
3. LibreHardwareMonitor exposes CPU/GPU nodes but no usable power sensor.
4. BatteryService receives a snapshot without total power and passes `null` to the HUD.
5. The tested build is not the latest instrumented build.

## Evidence

Pending runtime evidence collection.

## Fix

Pending evidence.

## Verification

Pending user verification.
