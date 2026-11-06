# Development

## Prerequisites

- .NET SDK 9.0
- Linux or Windows for development
- A test device and a USB connection for hardware validation

## Build and run

Linux:

```bash
dotnet restore Brom.csproj
dotnet build Brom.csproj
dotnet run --project Brom.csproj
```

Windows:

```powershell
dotnet restore Brom.csproj
dotnet build Brom.csproj
dotnet run --project Brom.csproj
```

Release build:

```bash
dotnet build Brom.csproj -c Release
```

## Source map

```
Brom/
├─ Program.cs              Application entry point
├─ App.axaml(.cs)          Avalonia application and theme
├─ MainWindow.axaml(.cs)   Single BROM page, handlers, logging
├─ Core/
│  └─ UsbTransport.cs      Serial port wrapper
├─ Models/
│  └─ DeviceModels.cs      DeviceInfo + PartitionInfo
├─ Protocols/
│  ├─ MtpMode.cs           MTP/ADB facade
│  ├─ MtpProtocol.cs       AT + ADB implementation
│  ├─ SpdMode.cs           BROM facade, port discovery, entry sequence
│  └─ Sprd/                HDLC, BROM, FDL protocol clients
├─ Services/
│  ├─ PathManager.cs       Portable path resolution
│  ├─ ILogSink.cs          Logging abstraction
│  ├─ OpLog.cs             Disk operation logs
│  ├─ UserLog.cs           User-facing log formatting
│  └─ LicenseState.cs      Build state
└─ T509Native/
   ├─ Proto.cs             BSL framing
   ├─ T509Engine.cs        BROM -> FDL1 -> FDL2 engine
   ├─ T509FlashService.cs  Action-to-engine mapping
   ├─ UsbIds.cs            Registry VID/PID reader
   └─ SprdNative/          Vendored protocol stack (GPLv3)
```

## Adding a BROM action

1. Implement the operation in `T509FlashService` or `T509Engine`. Keep all partition writes in the engine.
2. Add a wrapper in `SpdMode` (`SprdProtocol`) that resolves the port, joins the cancellation token, and formats the result.
3. Add a handler in `MainWindow.axaml.cs`:
   - clear the log,
   - call `SetControlsEnabled(false)`,
   - call `ResetCts()`,
   - await the wrapper,
   - re-enable controls in `finally`.
4. Add the button in `MainWindow.axaml` under the right section.
5. Update `USAGE.md` and `docs/T509_ENGINE.md` when the flow changes.

## Adding an MTP/ADB action

1. Add the protocol method to `MtpProtocol`.
2. Add a short-lived facade method to `MtpMode`.
3. Add the confirmation and handler in `MainWindow.axaml.cs`.
4. Reuse `LogOutput` for progress and `OpLog` for the operation record.

## Logging rules

- User-facing lines go through `LogOutput` and use `[PARTIAL]` only for task rows that are completed by `Done`/`Fail`.
- Never log secrets. `OpLog` redacts common secret keys, but callers should still avoid passing them.
- Protocol chatter belongs in `LogDebug`; the on-screen log should stay result-oriented.
- Keep field lines aligned: `Label.PadRight(12) + ": " + value` in the BROM engine, matching `SpdField`.

## Threading rules

- Long operations run inside `Task.Run` or async protocol methods.
- Do not block the UI thread on serial I/O.
- `STOP` cancels the operation token and force-disposes active handlers. After a cancellation, wait for the device to re-enumerate before retrying.
- One operation at a time; do not start a second BROM session while one is active.

## Path rules

- Never hard-code an absolute path.
- Add new paths to `PathManager` and use `Combine`, `FindFile`, or `ResolvePath`.
- Runtime files live under `bins/` and `loaders/`; temporary files go to `PathManager.GetTempPath()`.
- `BROM_ROOT` overrides the tool root for portable deployments.

## Testing checklist

- [ ] `dotnet build Brom.csproj` succeeds with 0 errors.
- [ ] The BROM page renders at 1366×715 and at the minimum supported size.
- [ ] **Read Info** works in MTP/ADB mode.
- [ ] **Read Info** works in BROM/Diag mode.
- [ ] **Force BROM** finds the port after a Diag/modem connection.
- [ ] **Fix Handshake** completes and leaves the device in BROM.
- [ ] **FRP + MDM** writes and verifies all four images (test device only).
- [ ] **Exit BROM (No Format)** writes `splloader.bin` and resets.


- [ ] **STOP** cancels a running operation and re-enables the UI.
- [ ] **SNAP** writes a screenshot under `backup/Screenshot`.
- [ ] Missing loader/image files produce a clear error before any write.

## Vendored code

`Brom/T509Native/SprdNative/` is a vendored protocol implementation under GPLv3. Do not remove `LICENSE-SPRDClientCore.txt`. If you modify that code, keep the license notice and document the change in the commit message.

## Code style

- C# 12, nullable disabled, implicit usings disabled.
- Prefer explicit types in public APIs and `var` only when the type is obvious.
- Keep comments focused on why a step exists, not on what a line does.
- Match the surrounding naming: `PascalCase` methods, `_camelCase` fields, `brom_*` UI handlers.
