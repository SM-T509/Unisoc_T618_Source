# Usage Guide

Brom runs on Linux and Windows.

## Quick start

1. Connect the device in the mode required by the action (see below).
2. Open **Brom** and wait for the device to appear in **CONNECTED DEVICE**.
3. Choose an action on the **BROM** page.
4. Read the confirmation dialog, then start the operation.
5. Watch the **ACTIVITY LOG**. **STOP** cancels the running operation; **SNAP** saves a screenshot.

## Device modes

### MTP / ADB

- Entry: boot Android, enable **USB debugging**, connect the USB cable, and authorize the computer.
- USB IDs: `04E8:6860`, `04E8:685E`, `04E8:685C`.
- Used by: **Read Info**.

### BROM

- Entry: power the device off, hold the BROM key combination while connecting USB, or use **Force BROM** from a Diag/modem port.
- USB IDs: `1782:4D00` (BROM), `1782:4D01` (Diag).
- Used by: **Force BROM**, **Fix Handshake**, **FRP + MDM**, **Exit BROM (No Format)**, and BROM **Read Info**.

The device can re-enumerate with a new COM number after a loader starts. Re-select it in **CONNECTED DEVICE** if the port changes.

## The BROM page

### Device Information

**Read Info** chooses the transport automatically:

- If an MTP/ADB device is selected, it reads properties over ADB and, when needed, over the modem AT channel.
- Otherwise it finds a BROM/Diag port and reads the chip, flash, and protocol information from the device.

Typical fields: Model, CSC, AP/BL/CP versions, binary bit, IMEI, serial number, Android version, FRP status, chip ID, flash ID, and port.

### BootROM

#### Force BROM

Use this when the device is visible as a Diag or modem port but has not enumerated as BROM.

1. Select the device or leave the selection on automatic detection.
2. Click **Force BROM**.
3. The tool sends the entry sequence and polls for a BROM port for up to 30 seconds.
4. When the BROM port appears, continue with **Read Info**, **Fix Handshake**, or **FRP + MDM**.

#### Fix Handshake

Use this when the BROM port is present but the normal handshake fails.

1. Put the device in BROM mode.
2. Click **Fix Handshake** and confirm.
3. The tool uploads the boot SPL and recovery stub to RAM and performs the loader takeover.
4. On success the device remains in BROM and the log reports the port in use.

This action does not format or write any partition.

#### FRP + MDM

This writes the service image set in one verified session:

- `bins/t509/misc.bin`
- `bins/t509/persistent.bin`
- `bins/t509/sec_efs.bin`
- `bins/t509/splloader.bin`

> **Warning:** this is destructive. `sec_efs` contains device identity data. Back up the original partitions first.

1. Put the device in BROM mode.
2. Click **FRP + MDM**.
3. Wait for the read-back verification to finish. Do not disconnect the cable.

#### Exit BROM (No Format)

Writes `bins/t509/splloader.bin` only and resets the device.

- No user data, FRP, or MDM partition is modified.
- Use this to restore a known bootloader image or leave BROM cleanly.
- The device must be on a BROM/Diag port.

### Service Bypass

## Recommended workflows

### Read device information

1. Connect the device in MTP/ADB or BROM mode.
2. Select it in **CONNECTED DEVICE**.
3. Click **Read Info**.
4. Copy the log with **SNAP** if you need a record.

### Repair a failed BROM handshake

1. Connect the device and confirm the BROM port appears.
2. Click **Fix Handshake** and confirm.
3. Wait for the takeover to finish.
4. Click **Read Info** to confirm the device answers.
5. Continue with **FRP + MDM** or **Exit BROM** only if required.

### Remove FRP + MDM

1. Put the device in BROM mode.
2. Click **Fix Handshake** if the port does not answer.
3. Click **FRP + MDM** and confirm.
4. Wait for all four images to be written and verified.
5. The device resets when the session finishes.

## Activity log

The log uses four visual states:

| Color | Meaning |
|---|---|
| White | Normal progress and section titles. |
| Green | Success, verified value, or completed task. |
| Orange | Warning or recoverable problem. |
| Red | Error or failed operation. |

Technical protocol traffic is written to the operation log on disk when enabled. The on-screen log keeps the result-oriented lines.

## Cancelling

**STOP** cancels the current operation and force-disposes active protocol handlers. A cancelled write can leave a partition partially written. Re-run the operation only after the device is stable and the port is available again.

## Tips

- Use a data-capable USB cable and a rear-panel USB port on a desktop.
- Close other tools that may hold the COM port.
- Do not run two BROM operations at once.
- Keep `loaders/T509/` and `bins/t509/` complete; missing files fail fast in the log.
- For MTP/ADB actions, make sure `adb.exe` is available in `sdks/platform-tools/` or on `PATH`.
