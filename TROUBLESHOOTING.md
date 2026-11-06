# Troubleshooting

## The application does not start

**Symptom:** Windows shows a missing runtime or the window closes immediately.

- Install the .NET 9 desktop runtime, or use a self-contained publish.
- Start `Brom.exe` from its own folder, not from inside a ZIP archive.
- Make sure the publish folder contains `Brom.deps.json`, `Brom.runtimeconfig.json`, and the managed dependencies.
- Start from a command prompt to see the exception text:

```powershell
.\Brom.exe
```

## No device in CONNECTED DEVICE

- Confirm the USB cable supports data, not only charging.
- Try a rear-panel USB port on a desktop or a powered hub.
- Check Device Manager for an unknown device or a yellow warning icon.
- Reinstall the Samsung USB driver.
- For MTP/ADB, unlock the screen and authorize the computer when the RSA prompt appears.

## BROM port does not appear

- Confirm Device Manager shows `1782:4D00`.
- Try the BROM key combination again: power off, hold the keys, then connect USB.
- Use **Force BROM** from a Diag/modem port (`1782:4D01`).
- Try another USB cable and port.
- Close other tools that may hold the COM port.

## Force BROM fails

- Make sure the device is visible as a Diag/modem COM port before clicking.
- Check that the port is not already open in another program.
- Run the tool as administrator if the COM port is reserved by a driver service.
- Reconnect the device and try again; some devices need a few seconds after the Diag port appears.

## Fix Handshake fails

| Log | Likely cause | Fix |
|---|---|---|
| `No BROM port` | Device not in BROM or driver missing | Reconnect in BROM mode and check Device Manager |
| `Missing fdl1` / `Missing FDL1` | Loader file missing | Restore `loaders/T509/fdl1-dl.bin` |
| `Handshake` / `timeout` | Port hung or wrong COM port | Unplug and replug, then select the new port |
| `Cancelled` | STOP pressed | Reconnect and run again |

## FRP + MDM fails

- Verify all four files exist:
  - `bins/t509/misc.bin`
  - `bins/t509/persistent.bin`
  - `bins/t509/sec_efs.bin`
  - `bins/t509/splloader.bin`
- Check that the device is on a stable BROM/Diag port.
- Do not disconnect the cable during the write.
- If a write fails verification, the partition may be partially written. Re-run the operation only after the port is stable.
- `sec_efs` is device-specific. Do not use an image from another device.

## Exit BROM fails

- Confirm `bins/t509/splloader.bin` exists.
- The device must be in BROM/Diag mode.
- If the write fails verification, do not repeatedly retry. Check the cable and port first.

## ADB not found

The log reports the expected path. Fix it by one of these methods:

1. Place platform-tools in `sdks\platform-tools\` next to `Brom.exe`.
2. Add the platform-tools folder to `PATH`.
3. Install Android SDK platform-tools in a standard location.

Then close and reopen the application.

## ADB device is unauthorized

1. Unlock the device screen.
2. Accept the **Allow USB debugging** prompt.
3. If the prompt does not appear, revoke USB debugging authorizations in Developer Options and reconnect.
4. Confirm with:

```powershell
adb devices
```

The device must be listed as `device`, not `unauthorized`.

## The device re-enumerated and the tool lost it

A loader takeover can change the COM number. Select the new port in **CONNECTED DEVICE**, then click **Read Info** before continuing.

## STOP does not seem to work

STOP cancels the token and force-disposes active handlers. If the driver is blocked in a kernel call, unplug the USB cable, wait five seconds, and reconnect. Then restart the application.

## Screenshot is not saved

**SNAP** writes to `backup/Screenshot` under the tool root. Make sure the folder is writable. The log prints the exact path.

## Build errors on Linux

Brom now builds natively on Linux:

```bash
dotnet build Brom.csproj
```

The build can succeed on Linux, but the UI cannot run there.

## Logs

Operation logs are written under the tool root or the temporary folder, depending on the build. Search for the newest `.log` file and include the last 50 lines when reporting a problem.

Before sharing a log, remove serial numbers, IMEI values, account names, and any device-specific identifiers.
