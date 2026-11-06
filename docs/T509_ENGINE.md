# T509 BROM Engine

The T509 engine runs entirely inside the Brom process. It does not launch an external flashing executable.

## USB identities

| Mode | VID | PID | Notes |
|---|---|---|---|
| BROM | `1782` | `4D00` | Primary loader-entry mode. |
| Diag | `1782` | `4D01` | AT/HDLC entry channel. |
| MTP/ADB | `04E8` | `6860`, `685E`, `685C` | Normal Android service mode. |

The engine also reads the registry under `HKLM\SYSTEM\CurrentControlSet\Enum\USB` to resolve the COM port for a VID/PID pair, so detection does not depend only on the port name.

## Memory layout

| Component | Address | File |
|---|---|---|
| FDL1 / boot SPL | `0x5500` | `loaders/T509/T509_Fix.bin` or `loaders/T509/fdl1-dl.bin` |
| Recovery stub | `0x3EE8` | `loaders/T509/custom_exec_no_verify_3ee8.bin` |
| FDL2 | `0x9EFFFE00` | `loaders/T509/fdl2.bin` (or `uboot.bin` / `fdl2-dl.bin`) |

The stub is 128 bytes. The boot SPL capture is preferred for handshake repair; the download build is preferred when the session must stay in FDL1 for partition writes.

## Startup chain

```
BROM (1782:4D00)
  │  CHECK_BAUD / CONNECT
  ▼
Upload FDL1 at 0x5500
  │  END_DATA
  ▼
Upload recovery stub at 0x3EE8
  │  no END_DATA
  ▼
EXEC stub
  │  CHECK_BAUD + CONNECT
  ▼
Upload FDL2 at 0x9EFFFE00
  │  EXEC
  ▼
FDL2 partition session
  │  read / write / erase / verify
  ▼
NORMAL_RESET
```

The engine waits for the loader to settle after each stage:

- `FDL1_SETTLE_MS = 15000` — DRAM and clock initialization, USB re-enumeration.
- `FDL2_SETTLE_MS = 5000` — FDL1 finalizes the image before `EXEC`.

## Loader resolution

`T509Engine.LoadFdl1` chooses the payload in this order:

1. `fdl1-dl.bin` when `preferDownloadBuild` is set and the file is non-empty.
2. `T509_Fix.bin` when the captured SPL can be parsed at `0x5500`.
3. `fdl1-dl.bin` as the fallback.

`LoadStub` reads `custom_exec_no_verify_3ee8.bin`. If the stub is missing, the takeover fails before any write.

FDL2 candidates:

1. `loaders/T509/fdl2.bin`
2. `loaders/T509/uboot.bin`
3. `loaders/T509/fdl2-dl.bin`

`PathManager` exposes these as `GetFdl1Path()`, `GetFdl2Path()`, `GetStubPath()`, and `GetFixPath()`.

## Port discovery

`T509FlashService.FindBromPort` and `SpdMode.FindBromPort` use several passes:

1. `HARDWARE\DEVICEMAP\SERIALCOMM` values matching `SPRD` or `SPREADTRUM`.
2. `Win32_SerialPort` entries whose PNP ID is `VID_1782&PID_4D00`.
3. `Win32_SerialPort` entries whose name contains `SPRD` and does not contain `Diag` or `Modem`.
4. `Win32_PnPEntity` entries with a COM name and the BROM VID/PID.
5. `Win32_PnPDevice` name fallback.

The Diag lookup uses `VID_1782&PID_4D01` first, then a `SPRD` + `Diag` name match.

## Entry sequence

`SpdMode.ForceBromAsync`:

1. Checks whether the selected port is already BROM; returns immediately if so.
2. Sends `AT+REBOOT` to the Diag/modem port.
3. Polls for a BROM port for up to 30 seconds.
4. If no port appears, opens the Diag port at 19200 baud and sends an AT command barrage:
   - `AT+SPREF="AUTODLOADER"`
   - `AT+FUS=1`
   - `AT+REACTIVE=1,0,0`
   - `AT+SPREF="AUTODLOADER"`
   - `AT+DUMPCTRL=1,0`
   - `AT+SWATD=0`
   - `AT+ACTIVATE=0,0,0`
5. Writes the framed autoloader command and polls again.
6. Reports `BROM on COMx`, or a failure hint suggesting the volume-key USB entry.

`SpdMode.KickDiagToBromAsync` performs the same style of Diag handoff with a framed HDLC autoloader command.

## Fix Handshake

`T509FlashService.FixHandshake` maps to `T509Engine.FixHandshake`:

1. Resolve the BROM port.
2. Upload the boot SPL and the recovery stub.
3. Run `CHECK_BAUD` and `CONNECT`.
4. Optionally leave the device in BROM (`T509BootTarget.Brom`) or continue booting Android (`T509BootTarget.Android`).
5. No partition is written in this flow.

The UI calls the BROM target, so the device stays available for the next operation.

## FRP + MDM

`T509FlashService.FlashFrpMdm` validates this set before opening a session:

```
bins/t509/misc.bin
bins/t509/persistent.bin
bins/t509/sec_efs.bin
bins/t509/splloader.bin
```

The engine writes each image to its partition and reads it back for verification. `splloader` is written last. `sec_efs` is device identity data; it is included in this action and must come from the correct device family.

The session ends with `NORMAL_RESET`.

## Exit BROM (No Format)

`T509FlashService.FlashSplloaderOnly`:

1. Uses `bins/t509/splloader.bin`.
2. If it is missing, falls back to `bins/t509/T509_Fix.bin` and then `loaders/T509/T509_Fix.bin`.
3. Starts a normal FDL session.
4. Writes only the `splloader` partition.
5. Resets the device.

No user data, FRP, or MDM partition is touched.

## Partition I/O

`T509Engine` exposes read, write, erase, and full-dump operations used by the protocol layer. A write is always followed by a read-back comparison. The engine reports:

- the partition name,
- the byte count,
- the verification result,
- the concrete error when a stage fails.

## Error handling

| Failure | Result |
|---|---|
| No BROM port | `No BROM port - put the device in BROM.` |
| Missing loader | `Missing FDL2 loader: <path>` or equivalent |
| Handshake timeout | The port is retried at the next baud rate, then the operation fails |
| Write verification mismatch | The partition is reported as failed; do not continue with the same device state |
| Cancellation | The token is honored between stages and during port polling |
| Disposed handler | `STOP` force-disposes active handlers so a blocked read can return |

## Cancellation

`T509Engine.CancelAllHandlers()` calls `SprdProtocolHandler.CancelAll()`. The UI calls it from the STOP button. A cancellation during a write can leave a partially written partition; re-run the operation only after the device is stable.

## Log format

The engine emits aligned fields with a 12-character label and a 43-character cap:

```
Port        : COM7 @ 115200
Loader      : fdl2.bin (641492 bytes)
Partitions  : 4 partitions to write
```

The UI renders `[PARTIAL]` task rows and `Done`/`Fail` states in green/red. Protocol-level debug lines should use `LogDebug`.

## Invariants

- The engine is the only writer of device partitions.
- Loader files are validated before the session starts.
- FDL1 and FDL2 addresses are constants; do not change them without a matching payload.
- The recovery stub is uploaded without `END_DATA`.
- The BROM port can change after a takeover; always use the port returned by the engine.
- The loader set and image set must match the device family.
