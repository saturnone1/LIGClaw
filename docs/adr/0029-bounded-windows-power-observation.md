# ADR 0029: Observe Windows power state through a bounded common adapter

- Status: accepted
- Date: 2026-08-01

## Context

The basic system status reports only whether AC power is connected. Users also need a simple answer about battery percentage, charging, low or critical charge, and energy saver. Hardware-specific battery inventory APIs expose unnecessary identifiers and vary by device.

## Decision

Protocol 1.14 adds the R0 `system.get_power_status.v1` Tool. Desktop reads the stable Win32 `GetSystemPowerStatus` structure through an injectable adapter shared by Windows 10 and Windows 11. It returns only provider availability, power source, battery presence, energy-saver state, and—when a battery is present—charging state, charge safety state, percentage, and an optional runtime estimate.

No battery name, serial number, manufacturer, chemistry, or persistent hardware identifier is collected. A device without a battery and a provider failure are separate structured outcomes. The Sidecar owns only the Tool description and bridge call; Desktop remains the source of Windows state.

## Consequences

- Laptop questions can be answered without WMI, PowerShell, or administrator access.
- Desktop PCs do not produce misleading zero-percent battery data.
- Deterministic flag classification and bridge replay tests cover the protocol path; real laptop behavior remains in the Windows 10/11 acceptance matrix.
