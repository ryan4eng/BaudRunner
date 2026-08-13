# BaudRunner

BaudRunner is a native C# replacement for the legacy `SerialTerminal` WinForms application. It uses Avalonia for the desktop UI and the built-in .NET networking/serial APIs, so there is no browser, JavaScript runtime, Electron layer, or web frontend.

## Current features

- Serial terminal with port discovery, baud rate, data bits, parity, stop bits, RTS and DTR.
- TCP client and non-blocking TCP server.
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

Requires the .NET 9 SDK. Restore and build from this directory:

```powershell
dotnet restore baudrunner/BaudRunner.csproj
dotnet build baudrunner/BaudRunner.csproj
```

Run with:

```powershell
dotnet run --project baudrunner/BaudRunner.csproj
```

Framework-dependent publishing produces a small application but requires the matching .NET desktop runtime on the target machine:

```powershell
dotnet publish baudrunner/BaudRunner.csproj -c Release -r win-x64 --self-contained false
dotnet publish baudrunner/BaudRunner.csproj -c Release -r linux-x64 --self-contained false
```

Use a runtime-specific publish rather than a generic publish; the Windows output is approximately 25 MB and excludes unused Linux/macOS backends. A self-contained package is larger because it includes .NET itself.

For a machine without .NET installed, use `--self-contained true`; that is larger because it includes the .NET runtime.

Logs are always written under the platform's local application data directory in `BaudRunner/logs`. Configuration is stored beside it as `BaudRunner/config.json`.
