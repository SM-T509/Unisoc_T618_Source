# MTP, AT, and ADB Reference

`MtpProtocol` handles the non-BROM side of Brom: device information and CSC reads. It uses the modem AT channel and `adb` on Linux/Windows.

## Transport selection

`MtpProtocol` can reach the device through:

1. **ADB** — preferred when the device is booted and debugging is authorized.
2. **Modem serial port** — used for AT commands when ADB is unavailable or the value only exists in the modem.

`ReadInfoAsync` tries ADB first, then falls back to the modem AT channel. A failure in one transport does not stop the other from being attempted.

## ADB discovery

The tool searches for `adb.exe` in this order:

1. `sdks/platform-tools/adb.exe` next to the tool.
2. `sdks/android-sdk/platform-tools/adb.exe`.
3. `PATH`.
4. Android SDK locations under `%LOCALAPPDATA%`, `%USERPROFILE%`, `Program Files`, and `Program Files (x86)`.

If none is found, the operation fails with the expected path in the activity log.

## Device information

### ADB properties

| Property | Field |
|---|---|
| `ro.product.model` | Model |
| `ro.csc.sales_code` | CSC |
| `ro.build.version.incremental` | AP version |
| `ro.baseband` | CP version |
| `ro.build.version.release` | Android version |
| `ril.serialnumber` | IMEI (when exposed) |
| `ro.serialno` | Serial number |

### Modem AT commands

| Command | Purpose | Response |
|---|---|---|
| `AT+DEVCONINFO` | Read model, firmware, CSC, SN, IMEI, country, lock state | `+DEVCONINFO: ...` |
| `AT+PRECONFG=1,0` | Read the sales code | `+PRECONFG:1,XXX` |
| `AT+PRECONFG=2,XXX` | Write the sales code | `OK` / `ERROR` |
| `AT+SERIALNO?` | Read the serial number | `+SERIALNO:...` |
| `AT+SERIALNO=2,SN` | Write the serial number | `OK` / `ERROR` |
| `AT+SN=SN` | Serial write fallback | `OK` / `ERROR` |
| `AT+CDSN=SN` | Serial write fallback | `OK` / `ERROR` |
| `AT+GSN=SN` | Serial write fallback | `OK` / `ERROR` |
| `AT+REACTIVE=1,0,0` | Read the reactivation lock | `+REACTIVE:1,UNLOCK` / `LOCK` |
| `AT+VERSNAME=3,2,3` | Read firmware version names | `+VERSNAME:...` |
| `AT+SWATD=0` | Enter download/service mode | `[DR]: CHANGE TO DDEXE` |
| `AT+SWATD=1` | Leave download/service mode | `OK` |
| `AT+ACTIVATE=0,0,0` | Activate the download agent | `OK` |
| `AT+FACTORST=0,0` | Factory reset | `OK` |
| `AT+POWRESET=0,0` | Reboot | `OK` |

The serial path is retried across `AT+SERIALNO`, `AT+SN`, `AT+CDSN`, and `AT+GSN`. A read-back with `AT+SERIALNO?` confirms the write when the firmware supports it.

## Read Info

### MTP/ADB present

1. Wait for ADB to report `device`.
2. Read the ADB properties.
3. If fields are missing, open the modem port and read `AT+DEVCONINFO`, `AT+PRECONFG`, `AT+REACTIVE`, and `AT+VERSNAME`.
4. Print the fields in the activity log.

### BROM/Diag only

1. Find the BROM or Diag port.
2. Probe the protocol stage with `CHECK_BAUD`/`CONNECT`.
3. Report the port, stage, and chip/flash information that the device returns.
4. Do not fabricate values when the device does not answer; the log reports the concrete failure.

## Log behavior

- `Task # N: ... Done/Fail` rows are produced for each phase.
- AT responses are compacted to a single line.
- ADB errors (`device unauthorized`, `device not found`, `error:`) are surfaced as warnings or errors.
- Debug lines include the raw response when the operation needs it.

## Safety

- Serial-number and CSC writes are persistent device changes.
- The device may reboot during an operation; keep the cable connected until the final log line.
- Do not run an MTP/ADB action and a BROM action at the same time.
