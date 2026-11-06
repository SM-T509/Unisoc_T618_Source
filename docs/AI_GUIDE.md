# Brom — AI / Developer Guide

This is the compact map of the current codebase. The product is a single-page Avalonia BROM utility (Linux and Windows).

## What the application is

- Name: **Brom**
- Target: Linux and Windows, `net9.0`, Avalonia, C# 12
- UI: one **BROM** page
- Core capabilities: BROM detection and entry, handshake repair, FRP + MDM maintenance, SPL-only bootloader restore, MTP/ADB info, serial-number change
- No other device-family pages, no firmware-flashing page, no third-party tool launcher

## Build

```powershell
dotnet build Brom.csproj
dotnet run --project Brom.csproj
```

Linux build check:

```bash
dotnet build Brom.csproj
```

## Entry point

`Brom/Program.cs`:

- normal launch: `dotnet run --project Brom.csproj`
- Avalonia desktop lifetime

## UI map

`Brom/MainWindow.axaml` builds the page:

| Section | Button | Handler |
|---|---|---|
| Device Information | Read Info | `brom_readInfoBtn_Click` |
| BootROM | Force BROM | `brom_forceBtn_Click` |
| BootROM | Fix Handshake | `brom_fixHandshakeBtn_Click` |
| BootROM | FRP + MDM | `brom_frpMdmBtn_Click` |
| BootROM | Exit BROM (No Format) | `brom_exitBtn_Click` |


The sidebar contains the device selector, activity log, and STOP. `MainWindow.axaml.cs` owns the handlers, log renderer, cancellation token, and device detection.

## Core files

| File | Role |
|---|---|
| `Brom/MainWindow.axaml` | Single-page layout, theme, action buttons |
| `Brom/MainWindow.axaml.cs` | Handlers, device detection, activity log, STOP |
| `Brom/App.axaml` | Application theme |
| `Brom/Core/UsbTransport.cs` | Serial port wrapper with buffered async read/write |
| `Brom/Models/DeviceModels.cs` | `DeviceInfo`, `PartitionInfo` |
| `Brom/Protocols/MtpMode.cs` | Static facade for MTP/ADB operations |
| `Brom/Protocols/MtpProtocol.cs` | ADB and modem AT implementation |
| `Brom/Protocols/SpdMode.cs` | BROM port discovery, entry sequence, operation wrappers |
| `Brom/Protocols/Sprd/` | HDLC, BROM, and FDL protocol clients |
| `Brom/T509Native/T509Engine.cs` | In-process BROM -> FDL1 -> FDL2 engine |
| `Brom/T509Native/T509FlashService.cs` | Fix Handshake, FRP + MDM, SPL-only mapping |
| `Brom/T509Native/Proto.cs` | BSL framing and CRC |
| `Brom/T509Native/UsbIds.cs` | Registry VID/PID -> COM resolution |
| `Brom/T509Native/SprdNative/` | Vendored protocol stack, GPLv3 |
| `Brom/Services/PathManager.cs` | Portable path resolution |
| `Brom/Services/ILogSink.cs` | Logging abstraction |
| `Brom/Services/OpLog.cs` | Structured disk logs with redaction |
| `Brom/Services/UserLog.cs` | User-facing log blocks |
| `Brom/Services/ScreenCapture.cs` | SNAP implementation |
| `Brom/Services/LicenseState.cs` | Local build state |

## Operation flow

All UI handlers follow this shape:

```csharp
logs_richTextBox.Clear();
BeginV15LogFlow("operation");
SetControlsEnabled(false);
ResetCts();
try
{
    // resolve port/device
    // await facade or engine
}
catch (OperationCanceledException) { /* warn */ }
catch (Exception ex) { OpLog.Exception("BROM", "Operation", ex, "tag"); }
finally
{
    EndV15LogFlow();
    SetControlsEnabled(true);
}
```

`STOP` calls `T509Engine.CancelAllHandlers()` and cancels `_operationCts`.

## BROM flow

```
1782:4D00 BROM
  -> CHECK_BAUD / CONNECT
  -> FDL1 at 0x5500
  -> stub at 0x3EE8
  -> FDL1 handshake
  -> FDL2 at 0x9EFFFE00
  -> EXEC
  -> verified partition I/O
  -> NORMAL_RESET
```

Key files:

- `loaders/T509/fdl1-dl.bin`
- `loaders/T509/fdl2.bin`
- `loaders/T509/custom_exec_no_verify_3ee8.bin`
- `loaders/T509/T509_Fix.bin`
- `bins/t509/splloader.bin`
- `bins/t509/misc.bin`
- `bins/t509/persistent.bin`
- `bins/t509/sec_efs.bin`

## MTP/ADB flow

- `Read Info`: ADB properties, then modem AT fallback.
ADB is resolved from `sdks/platform-tools/adb.exe`, the Android SDK, or `PATH`.

## Invariants

- One operation at a time; controls are disabled during work.
- All partition writes go through `T509Engine` and are verified by read-back.
- No absolute path is compiled in; use `PathManager`.
- The UI thread must not block on serial I/O.
- `SprdNative` is GPLv3; keep `LICENSE-SPRDClientCore.txt`.
- The project has exactly one page and one set of handlers.

## Where to change what

| Task | File |
|---|---|
| Add a BROM action | `T509FlashService` + `SpdMode` wrapper + `MainWindow.axaml.cs` + `MainWindow.axaml` |
| Add an MTP/ADB action | `MtpProtocol` + `MtpMode` + `MainWindow.axaml.cs` |
| Change loader/image paths | `PathManager` |
| Change BROM handshake | `T509Engine` and `Protocols/Sprd` |
| Change log formatting | `Tool.LogOutput` and `UserLog` |
| Change the page layout | `MainWindow.axaml` |
| Add a portable root rule | `PathManager.ResolveProjectRoot` |

## Validation

Minimum before shipping:

1. `dotnet build Brom.csproj` has 0 errors.
2. The page renders at 1366×715 and at the minimum size.
3. `Read Info` works over MTP/ADB.
4. `Read Info` works over BROM/Diag.
5. `Fix Handshake` completes on a BROM device.
6. `FRP + MDM` writes and verifies the four images on a test device.
7. `STOP` cancels and re-enables the UI.
8. `SNAP` writes a screenshot.

## Documentation

- `README.md` — overview and build.
- `USAGE.md` — operating guide.
- `INSTALLATION.md` — deployment.
- `ARCHITECTURE.md` — layers and data flow.
- `DEVELOPMENT.md` — contribution notes.
- `TROUBLESHOOTING.md` — failure guide.
- `docs/T509_ENGINE.md` — low-level engine.
- `docs/MTP_AT.md` — AT/ADB reference.
- `docs/PORTABLE_PATHS_AR.md` — Arabic path notes.
