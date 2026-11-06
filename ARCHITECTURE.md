# Architecture

Brom is a single-window Avalonia application (Linux and Windows) with one **BROM** page. The code is split into a UI layer, thin operation facades, protocol clients, and an in-process native engine.

## Layers

```
┌──────────────────────────────────────────────────────────────┐
│ UI                                                           │
│  Program.cs · App.axaml · MainWindow.axaml
└───────────────────────────────┬──────────────────────────────┘
                                │ operation calls + log callbacks
┌───────────────────────────────▼──────────────────────────────┐
│ Facades                                                      │
│  MtpMode · SprdProtocol (SpdMode static wrapper)              │
└───────────────────────────────┬──────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────┐
│ Protocol clients                                             │
│  MtpProtocol (AT + ADB) · SpdMode · Protocols/Sprd           │
└───────────────────────────────┬──────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────┐
│ Native engine                                                │
│  T509Engine · T509FlashService · T509Native/SprdNative       │
└───────────────────────────────┬──────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────┐
│ Platform services                                            │
│  PathManager · ILogSink · OpLog · ScreenCapture · UserLog    │
└──────────────────────────────────────────────────────────────┘
```

## UI

- `Program.cs` starts the Avalonia desktop lifetime.
- `App.axaml` configures the Fluent dark theme.
- `MainWindow.axaml` builds the single **BROM** page: a header, the device sidebar, the action list, and the activity log.
- `MainWindow.axaml.cs` owns device detection, log rendering, operation cancellation, and all button handlers.

The UI never opens a serial port directly. It calls a facade and receives progress through the `LogOutput` callback.

## Facades

- `MtpMode` creates a short-lived `MtpProtocol`, performs one operation, and disposes it.
- `SprdProtocol` is the static entry point for BROM operations. It resolves a port, creates `SpdMode` or the native engine, and wraps the result in the log format used by the page.

New UI actions should follow the same pattern: a handler in `Tool.cs`, a facade method, and a protocol/engine call.

## Protocols

- `MtpProtocol` implements modem AT commands and ADB commands. It is used for device information and CSC reads.
- `SpdMode` contains the BROM/Diag port discovery, the AT/HDLC entry sequence, and operation wrappers (`ForceBromAsync`, `FixHandshakeAsync`, `FlashT509FrpMdmAsync`, `FlashSplOnlyAsync`).
- `Protocols/Sprd` contains the HDLC framing, BROM handshake, and FDL1/FDL2 clients used by `SpdMode`.

## Native engine

- `T509Engine` drives the in-process takeover:
  `BROM -> FDL1 (0x5500) + recovery stub (0x3EE8) -> FDL1 handshake -> FDL2 (0x9EFFFE00) -> EXEC -> partition I/O -> reset`.
- `T509FlashService` maps the UI actions to engine calls and resolves the image set in `bins/t509`.
- `T509Native/SprdNative` is the vendored protocol implementation (GPLv3). Keep the license file with redistributions.

The engine is the only component allowed to write partitions. It verifies writes by reading the data back and reports each stage to the `ILogSink`.

## Services

| Service | Responsibility |
|---|---|
| `PathManager` | Resolves the tool root, runtime folders, loader paths, temporary directory, and portable overrides. |
| `ILogSink` | Logging abstraction. `DelegatedLogSink` marshals messages to the UI thread. |
| `OpLog` | Writes structured operation logs and redacts secrets. |
| `ScreenCapture` | Saves the SNAP screenshot. |
| `UserLog` | Formats user-facing step, result, and hint blocks. |
| `LicenseState` | Local build state. This build always reports an unlocked/community state. |

## Data flow: FRP + MDM

1. `Tool.brom_frpMdmBtn_Click` disables the controls and creates a cancellation token.
2. `SprdProtocol.FlashT509FrpMdmAsync` resolves a BROM/Diag port.
3. `T509FlashService.FlashFrpMdm` validates `misc.bin`, `persistent.bin`, `sec_efs.bin`, and `splloader.bin`.
4. `T509Engine` performs the BROM takeover and starts an FDL2 session.
5. Each image is written and read back for verification.
6. The device is reset and the result is written to the activity log and `OpLog`.

## Data flow: Read Info

1. The handler checks whether the selected device is MTP/ADB.
2. MTP/ADB: `MtpProtocol.ReadInfoAsync` reads ADB properties and modem AT data.
3. BROM/Diag: `SprdProtocol.ReadInfoAsync` probes the port and reports the protocol stage and chip information.

## Invariants

- One operation at a time. `SetControlsEnabled(false)` and `_operationCts` guard the session.
- **STOP** cancels the token and calls `T509Engine.CancelAllHandlers()`; protocol objects are disposed before the UI is re-enabled.
- Loader and image paths are resolved through `PathManager`; no absolute path is compiled in.
- All partition writes go through `T509Engine` and are verified.
- The UI remains responsive because long operations run in `Task.Run`.
- The single page has no hidden tabs or legacy feature builders.

## Extension points

| Change | Location |
|---|---|
| New BROM action | `T509FlashService` + `SpdMode` wrapper + `MainWindow.axaml.cs` handler + `MainWindow.axaml` button |
| New read-only device field | `MtpProtocol.ReadInfoAsync` and the BROM info method in `SpdMode` |
| New loader/image path | `PathManager` |
| New log format | `UserLog` and `Tool.LogOutput` |
| New low-level protocol command | `Protocols/Sprd` or `T509Engine`, depending on the stage |
