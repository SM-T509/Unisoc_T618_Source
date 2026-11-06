# Installation

## Supported environment

| Item | Requirement |
|---|---|
| Operating system | Linux (x64) or Windows 10/11 (x64) |
| Runtime | .NET 9 runtime (or a self-contained publish) |
| USB | Samsung USB driver and a working COM port |
| ADB | Android platform-tools, placed in `sdks/platform-tools/` or installed system-wide |
| Build tooling | .NET SDK 9.0 |

Brom is an Avalonia application and runs natively on Linux and Windows.

## Build from source

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

Build a Release executable:

```bash
dotnet build Brom.csproj -c Release
```

Publish a folder you can copy to another machine:

```bash
dotnet publish Brom.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

```powershell
dotnet publish Brom.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

The build output is `bin/Debug/net9.0/` or `bin/Release/net9.0/`.

## First run

1. Extract or copy the whole application folder. Do not copy only the executable.
2. Keep the folder structure:
   ```
   Brom (or Brom.exe on Windows)
   bins/t509/
   loaders/T509/
   sdks/platform-tools/adb (optional, for MTP/ADB)
   ```
3. Connect the device.
4. Start `Brom.exe`.
5. Select the device in **CONNECTED DEVICE** and use the **BROM** page.

No installer and no registry configuration are required. The application writes temporary files under `%TEMP%\Brom`.

## Drivers

Install the Samsung USB driver before connecting the device. For BROM operations, Windows must expose the device as a COM port:

- BROM: `1782:4D00`
- Diag: `1782:4D01`
- MTP/ADB: `04E8:6860`, `04E8:685E`, `04E8:685C`

If Device Manager shows an unknown device, install the matching Samsung or serial driver and reconnect.

## ADB

MTP/ADB actions need `adb.exe`. The tool searches, in order:

1. `sdks\platform-tools\adb.exe` next to the tool.
2. `sdks\android-sdk\platform-tools\adb.exe`.
3. The `PATH` environment variable.
4. Common Android SDK locations under `%LOCALAPPDATA%`, `%USERPROFILE%`, and `Program Files`.

If ADB is missing, the log reports the exact path it expected.

## Required runtime files

| File | Required for |
|---|---|
| `loaders\T509\fdl1-dl.bin` | All BROM/FDL sessions |
| `loaders\T509\fdl2.bin` | Partition I/O and FRP + MDM |
| `loaders\T509\custom_exec_no_verify_3ee8.bin` | Loader takeover |
| `loaders\T509\T509_Fix.bin` | Fix Handshake |
| `bins\t509\splloader.bin` | Exit BROM |
| `bins\t509\misc.bin` | FRP + MDM |
| `bins\t509\persistent.bin` | FRP + MDM |
| `bins\t509\sec_efs.bin` | FRP + MDM |

Missing files are reported by name before any write starts. Keep backups of the original partitions for the device you are servicing.

## Portable installation

The tool root is resolved automatically:

1. `BROM_ROOT`, when it points to an existing folder.
2. The folder containing `Brom.exe`, when it has `bins/`, `loaders/`, or `sdks/`.
3. The nearest ancestor that looks like a tool root.

Copy the entire folder to a USB drive or another computer and run it there. See [`docs/PORTABLE_PATHS_AR.md`](docs/PORTABLE_PATHS_AR.md).

## Updating

1. Close the application.
2. Replace `Brom.exe` and the managed libraries.
3. Keep your local `bins\` and `loaders\` folders unless the update explicitly ships new images.
4. Start the application and run **Read Info** to confirm the device still answers.

## Uninstalling

Close the application and delete its folder. Remove `%TEMP%\Brom` if you also want to delete temporary files.
