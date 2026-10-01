# Debug Session: actual-power-missing

Status: [OPEN]

## Symptom
PC HUD continues to display `--W` for actual power although AIDA64 shows CPU package and GPU power sensors.

## Hypotheses
1. LibreHardwareMonitor initialization fails or exposes no hardware nodes in this process.
2. The process lacks the permissions required to access the sensors.
3. Hardware types or sensor types/names differ from the current matching logic.
4. BatteryService returns before HardwareService is called because a battery/WMI path is incorrectly treated as a battery device.
5. Real-time refresh is reading a stale or empty snapshot.

## Instrumentation Plan
Add runtime evidence only before changing business logic. Capture initialization, hardware tree, sensor type/name/value, BatteryService branch, and HUD snapshot values.

## Evidence
Pending user reproduction with instrumented build.

## Fix
Pending evidence.

## Verification
Pending.
