# HardwareInspector

A lightweight, standalone Windows desktop utility for low-level hardware serial identification, firmware data enumeration, and differential baseline snapshot tracking.

## Features

- **Raw SMBIOS Table Extraction**: Direct queries via `kernel32!GetSystemFirmwareTable('RSMB', 0)` to parse Motherboard BaseBoard serials, System UUIDs, BIOS serials, and CPU IDs.
- **Physical Disk Firmware Serials**: Direct `IOCTL_STORAGE_QUERY_PROPERTY` queries on `\\.\PhysicalDrive0..N` for hardware-burned storage descriptors (NVMe, SATA, SSD).
- **Plug-and-Play Device Nodes**: Captures physical hardware instance IDs for Keyboards, Mice, Bluetooth adapters, USB Hubs, Audio Endpoints, and HID controllers.
- **Network Interface MACs**: Extracts MAC addresses across all physical and virtual network adapters.
- **Registry Fingerprints**: Reads `MachineGuid` and `HwProfileGuid` directly from Windows NT system registries.
- **Differential Baseline Snapshots**: Save baselines and perform before/after comparisons to track hardware identifier changes.
- **Portable**: Zero external runtime dependencies. Runs natively on Windows 7/8/10/11 (~26 KB binary).

## Compilation

Build directly using the built-in Microsoft .NET Framework C# compiler:

```cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:HardwareInspector.exe /r:System.Management.dll /optimize+ /platform:x64 Program.cs
```
