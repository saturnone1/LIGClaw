# ADR 0031: Keep detailed network observation current, approved, and bounded

- Status: accepted
- Date: 2026-08-01

## Context

IP, DNS, gateway, and connected Wi-Fi information helps explain common connection failures. These values can also reveal internal network and location context. Newer Windows releases apply precise-location consent to `WlanQueryInterface` when querying the current connection and can return `ERROR_ACCESS_DENIED`.

## Decision

Protocol 1.16 adds `system.get_network_details.v1` as an R1 Tool requiring a reason and approval on every call. Desktop reads only currently active adapters through .NET network APIs and queries the connected SSID through the Windows Native Wi-Fi API after the in-app approval. It does not scan nearby networks.

The result contains at most 16 adapters. Each adapter contains at most eight current IP/prefix values, four DNS servers, four gateways, and an optional connected SSID. MAC addresses, BSSIDs, Wi-Fi profile names, credentials, security keys, command output, and connection history are not read, returned, logged, or persisted.

SSID permission failure is isolated as `permission_required`; IP, DNS, and gateway results remain usable. An unavailable WLAN service or API is isolated as `unavailable`, and a device without a WLAN interface is `not_applicable`. The approval warns that Windows may show its own location-consent prompt. Native observation runs away from the UI thread and this adapter has a 60-second execution boundary for that one-time OS prompt.

The implementation follows Microsoft's Native Wi-Fi lifetime rules: buffers returned by enumeration and query calls are released with `WlanFreeMemory`, and the client handle is closed. See [WlanQueryInterface](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanqueryinterface), [WlanEnumInterfaces](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanenuminterfaces), and [Wi-Fi access and location changes](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes).

## Consequences

- Users receive actionable current network configuration without a shell or administrator access.
- A denied location permission does not make wired diagnostics fail.
- The Tool cannot change adapters, DNS, routes, Wi-Fi profiles, or connectivity.
- Deterministic bounds, privacy fields, SSID decoding, permission isolation, Windows 10/11 adapter, and Sidecar replay tests protect the contract; real consent prompts remain in external OS acceptance.
