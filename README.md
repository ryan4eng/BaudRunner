# BaudRunner

BaudRunner is a native C# replacement for the legacy `SerialTerminal` WinForms application. It uses Avalonia for the desktop UI and the built-in .NET networking/serial APIs, so there is no browser, JavaScript runtime, Electron layer, or web frontend.

## Current features

- Serial terminal with port discovery, baud rate, data bits, parity, stop bits, RTS and DTR.
- TCP client and non-blocking TCP server.
- TCP server accepts up to five simultaneous clients, shows each remote IP and port, and lets you select or disconnect the target client for command sends.
- UDP client and UDP server, including replying to the last client that sent a datagram.
- Twelve reusable command rows per transport.
- ASCII or hexadecimal command input, optional LF, normal/ASCII/hex display modes.
- Timestamp, pause, clear, automatic log-to-file, and clean shutdown.
- Focused diagnostics: CRC-16/Modbus and IMEI/Luhn validation.
- Async, cancellable byte reads. Serial input uses `SerialPort.BaseStream` rather than the older `DataReceived` event path.
- Serial settings, network endpoints, display choices, and all command rows are saved to JSON when the application closes and restored on startup.
- The serial selector is populated from the operating system and refreshed every time its drop-down opens.
- Log display format, timestamping, pausing, and clearing are available from each log's context menu.
- Serial auto-reconnect is opt-in and only retries after the user has explicitly clicked Open; Close cancels the retry loop.

## Build

The application is permanently maintained on the v2 version line. The current version is `v2.0.0`.

- Debug builds identify themselves as `v2.0.0-dev`.
- Release builds and published packages identify themselves as `v2.0.0`.
- The `-dev` suffix is controlled automatically by the build configuration.

Requires the .NET 9 SDK. Restore and build from this directory:

```powershell
dotnet restore baudrunner/BaudRunner.csproj
dotnet build baudrunner/BaudRunner.csproj
```

Run with:

```powershell
dotnet run --project baudrunner/BaudRunner.csproj
```

The automated publish script builds all four platform/runtime combinations. A normal local run creates a release candidate and increments its suffix automatically:

```powershell
.\scripts\publish.ps1
```

For example, consecutive local runs produce `v2.0.0-rc1`, `v2.0.0-rc2`, and so on. Each candidate contains Windows framework-dependent, Windows self-contained, Linux framework-dependent, and Linux self-contained ZIPs under `publish/release-candidates`.

When the same script runs in GitHub Actions for a `release/v2.x.y` tag, it automatically switches to official release mode and writes the four ZIPs under `release/v2.x.y`. Manual `Run workflow` executions use release-candidate mode and upload artifacts only.

Framework-dependent packages require the matching .NET runtime. Self-contained packages include .NET and are larger.

Logs are always written under the platform's local application data directory in `BaudRunner/logs`. Configuration is stored beside it as `BaudRunner/config.json`.
