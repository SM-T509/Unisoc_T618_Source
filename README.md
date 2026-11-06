# Brom

**Brom** is a cross-platform (Linux and Windows) BootROM (BROM) service utility for Samsung T509 tablets, with a small MTP/ADB side panel for everyday service tasks.

The application has a single page: **BROM**. There are no device-family tabs, no firmware flashing tabs, and no third-party tool integrations.

## What it does

| Section | Action | What happens |
|---|---|---|
| Device Information | **Read Info** | Reads model, CSC, firmware, serial, IMEI, Android version, and FRP status over MTP/ADB — or reads chip/flash information from a BROM/Diag port. |
| BootROM | **Force BROM** | Sends the BROM entry sequence to a Diag/modem port and waits for the device to enumerate in BROM mode. |
| BootROM | **Fix Handshake** | Uploads the boot SPL and recovery stub to RAM to repair a failed BROM handshake. The device stays in BROM. |
| BootROM | **FRP + MDM** | Writes `misc`, `persistent`, `sec_efs`, and `splloader` in one verified FDL session. This is destructive. |
| BootROM | **Exit BROM (No Format)** | Writes `splloader.bin` only and resets the device. No partition is formatted and no user data is touched. |


The right-hand sidebar shows the detected device, the activity log, **STOP** (cancel the running operation), and **SNAP** (save a screenshot of the tool).

## Requirements

- Linux (x64) or Windows 10/11 (x64).
- A Samsung USB driver and a working COM port for the device.
- For MTP/ADB actions: Android platform-tools (`adb`) in `sdks/platform-tools/`, or an existing Android SDK installation.
- For BROM actions: the loader files in `loaders/T509/` and the image set in `bins/t509/`.
- To build: .NET SDK 9.0.

## Build

Linux:

```bash
dotnet build Brom.csproj
dotnet run --project Brom.csproj
```

Windows:

```powershell
dotnet build Brom.csproj
dotnet run --project Brom.csproj
```

Publish a self-contained build:

```bash
dotnet publish Brom.csproj -c Release -r linux-x64 --self-contained true
```

```powershell
dotnet publish Brom.csproj -c Release -r win-x64 --self-contained true
```

## Runtime layout

```
Brom/
├─ Brom.csproj
├─ Brom.sln
├─ Brom/
│  ├─ App.axaml(.cs)          # Avalonia application
│  ├─ MainWindow.axaml(.cs)    # single BROM page (Linux + Windows)
│  ├─ Program.cs
│  ├─ Core/
│  │  └─ UsbTransport.cs      # serial transport
│  ├─ Models/
│  │  └─ DeviceModels.cs
│  ├─ Protocols/
│  │  ├─ MtpMode.cs           # MTP/ADB facade
│  │  ├─ MtpProtocol.cs       # AT + ADB implementation
│  │  ├─ SpdMode.cs           # BROM facade and operation wrappers
│  │  └─ Sprd/                # HDLC, BROM, and FDL protocol clients
│  ├─ Services/
│  │  ├─ PathManager.cs
│  │  ├─ ILogSink.cs
│  │  ├─ OpLog.cs
│  │  ├─ UserLog.cs
│  │  └─ LicenseState.cs
│  └─ T509Native/
│     ├─ Proto.cs
│     ├─ T509Engine.cs        # in-process BROM -> FDL1 -> FDL2 engine
│     ├─ T509FlashService.cs  # Fix Handshake / FRP + MDM / SPL helpers
│     └─ SprdNative/          # vendored protocol implementation
├─ bins/t509/                 # misc, persistent, sec_efs, splloader, T509_Fix
├─ loaders/T509/              # FDL1, FDL2, recovery stub, boot SPL
└─ docs/
```

## Runtime files

| Path | Purpose |
|---|---|
| `loaders/T509/fdl1-dl.bin` | Download-mode FDL1 loader. |
| `loaders/T509/fdl2.bin` | FDL2 loader used for partition I/O. |
| `loaders/T509/custom_exec_no_verify_3ee8.bin` | Recovery stub loaded at `0x3EE8`. |
| `loaders/T509/T509_Fix.bin` | Captured boot SPL used by Fix Handshake. |
| `bins/t509/splloader.bin` | Bootloader image written by Exit BROM. |
| `bins/t509/misc.bin` | Misc partition image used by FRP + MDM. |
| `bins/t509/persistent.bin` | Persistent partition image used by FRP + MDM. |
| `bins/t509/sec_efs.bin` | Device identity image used by FRP + MDM. |
| `sdks/platform-tools/adb.exe` | ADB executable for MTP/ADB actions. |

## Portable paths

No absolute path is compiled into the application. The tool root is resolved in this order:

1. `BROM_ROOT`, when it points to an existing folder.
2. The folder containing `Brom.exe`, when it contains `bins/`, `loaders/`, or `sdks/`.
3. The nearest ancestor that looks like a tool root (for example, when running from `bin/Debug`).

Temporary files go to `%TEMP%\Brom`. See [`docs/PORTABLE_PATHS_AR.md`](docs/PORTABLE_PATHS_AR.md) for details.

## Documentation

- [`USAGE.md`](USAGE.md) — operating guide and workflows.
- [`INSTALLATION.md`](INSTALLATION.md) — build, publish, drivers, and runtime files.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — source layout and data flow.
- [`DEVELOPMENT.md`](DEVELOPMENT.md) — build commands, code map, and contribution notes.
- [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) — common failures and fixes.
- [`docs/T509_ENGINE.md`](docs/T509_ENGINE.md) — BROM/FDL engine details.
- [`docs/MTP_AT.md`](docs/MTP_AT.md) — MTP/ADB and AT command reference.
- [`docs/AI_GUIDE.md`](docs/AI_GUIDE.md) — compact code map for automated agents.
- [`docs/PORTABLE_PATHS_AR.md`](docs/PORTABLE_PATHS_AR.md) — مسارات التشغيل المحمولة.

## Safety

This is a destructive service tool. **FRP + MDM**, serial-number changes, and bootloader writes can erase data or leave a device unbootable when used incorrectly.

- Keep the USB cable connected for the entire operation.
- Do not start a BROM operation while another operation is running.
- Back up device-specific partitions before writing.
- Use only loader and image files that match the device.

## License

The vendored protocol implementation under `Brom/T509Native/SprdNative/` is distributed under GPLv3 (`LICENSE-SPRDClientCore.txt`). Keep the license notice with any redistribution.
