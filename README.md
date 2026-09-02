# BaudRunner

BaudRunner is a native C# replacement for the legacy `SerialTerminal` WinForms application. It uses Avalonia for the desktop UI and the built-in .NET networking/serial APIs, so there is no browser, JavaScript runtime, Electron layer, or web frontend.

## Features

### Connections

- Serial with port discovery, arbitrary baud rate, data bits, parity, stop bits, and flow control (none, RTS/CTS, XON/XOFF, or both).
- Manual RTS and DTR assertion with live CTS and DSR indicators, a BREAK button, and a DTR/RTS reset pulse for ESP32/Arduino-style boards.
- TCP client and non-blocking TCP server. The server accepts up to five simultaneous clients, shows each remote endpoint, and lets you select or disconnect the target client for command sends.
- UDP client (host names as well as literal addresses) and UDP server, which replies to the last client that sent a datagram and shows which host that is.
- The TCP and UDP servers bind dual-stack where the host supports IPv6.
- Opt-in auto-reconnect for serial and TCP client. For serial it waits for the port name to reappear rather than retrying blindly at a board that is being reflashed.
- The serial port list is populated from the operating system, refreshed when the drop-down opens, and polled for hot-plug changes. A saved port that is not currently present is labelled `(not connected)` rather than failing at Open.

### Display

- Virtualized colour log: append cost is independent of scrollback size and render cost is bounded by the viewport, so high-rate streams stay responsive.
- ANSI/SGR colour is rendered in the normal log view, including bold, inverse, the 256-colour palette and 24-bit colour. Zephyr, ESP-IDF, NuttX and U-Boot logs come out in colour instead of as escape-sequence noise. Choose Interpret, Strip, or Show raw per tab.
- Display formats: Normal, Hex (all bytes), Hex (except CR/LF), ASCII only, and a VT100 terminal mode for serial.
- Per-line timestamps: time of day, time since connect, or delta since the previous line.
- Text encoding: Latin-1/raw bytes (default) or UTF-8, with a stateful decoder so a character split across two reads still renders correctly.
- Selection survives a live stream: scrolling away pauses auto-scroll, and `Jump to live output` returns. Double-click selects a word, triple-click a line, shift-click extends.
- Find in log (`Ctrl+F`) with match highlighting, match count, and optional case sensitivity and regular expressions.
- Light and dark themes, configurable scrollback (1k / 5k / 20k / 100k lines), and a draggable split between the log and the command pane.

### Sending

- A free-form send box with command history (`Up`/`Down` recall the last 100 commands) and a selectable line ending: none, LF, CR, or CR+LF.
- Twelve reusable quick commands per transport, each with its own HEX toggle and line ending, sent with `F1`–`F12`. Unparsable text is flagged in red before you send it.
- Hex input accepts `01 03 00 6B`, `01,03`, `0x01 0x03`, `01-03` and unseparated `0103006B`.
- Text input supports `<CR>`, `<LF>`, `<TAB>`, `<ESC>`, `<NUL>`, `<BEL>`, `<BS>` and `<0xNN>` tokens, as well as the `{NN}` form the log uses to display a non-printable byte — so a value copied out of the log can be pasted straight back into a command. Text is sent as Latin-1 byte-for-byte rather than replacing anything above `0x7F` with `?`.
- Repeat send on an interval, for polling a register or soak-testing a command.
- In VT100 mode, control keys reach the device: `Ctrl+A`–`Ctrl+Z`, arrows, `Home`/`End`, `PageUp`/`PageDown`, `Insert`/`Delete` and `F1`–`F12`. Use `Ctrl+Shift+C` to copy, since `Ctrl+C` is sent as `0x03`, and `Ctrl+Shift+V` or `Shift+Insert` to paste; each line break in the pasted text is sent as Enter would send it.

### Diagnostics and logging

- RX/TX byte counters with a rolling throughput figure, plus serial framing, overrun and parity error counts — an unexplained framing-error count is the tell for a wrong baud rate.
- Automatic per-transport file logging with a session header, date and size rollover, and age-based pruning. Serial and TCP captures no longer interleave into one file.
- `Save log as...` and `Save selection as...`.
- Unhandled exceptions are written to `errors.log` next to the settings instead of closing the window silently.

### Keyboard

| Shortcut | Action |
| --- | --- |
| `Ctrl+O` / `Ctrl+W` | Open / close the active connection |
| `Ctrl+L` | Clear the active log |
| `Ctrl+S` | Save the active log to a file |
| `Ctrl+F` | Find in log (`Enter` next, `Shift+Enter` previous) |
| `Ctrl+End` | Jump back to live output |
| `Esc` | Close find, or jump back to live output |
| `Ctrl+1` … `Ctrl+5` | Select a transport tab |
| `F1` … `F12` | Send quick command 1–12 |
| `Up` / `Down` | Recall previous commands in the send box |

In VT100 mode, while the terminal has focus, `Esc` and `Ctrl+letter` go to the device rather than triggering the shortcuts above. Use the menu, or click outside the terminal first. `Ctrl+Shift+C` copies and `Ctrl+Shift+V` or `Shift+Insert` pastes.

## Settings and files

Settings are stored as `config.json` under the platform's local application data directory in `BaudRunner`, and logs beside it in `BaudRunner/logs`. The settings file is written atomically with a rolling `config.json.bak`, saved a few seconds after any change rather than only at exit, and enums are written as names so the file is readable and editable by hand. Window size, position, selected tab, theme, command slots and send history are all persisted.

`BaudRunner --auto-open` opens the serial tab's saved connection on startup; `BaudRunner --auto-open="TCP Client"` opens that tab instead. This is an automation hook, mainly used for testing.

## Build

The application is permanently maintained on the v2 version line. The version comes from `<VersionPrefix>` in `Directory.Build.props`; debug builds append a `-dev` suffix automatically.

Requires the .NET 9 SDK.

```powershell
dotnet restore BaudRunner/BaudRunner.sln
dotnet build BaudRunner/BaudRunner.sln
dotnet test BaudRunner.Tests/BaudRunner.Tests.csproj
```

Run with:

```powershell
dotnet run --project BaudRunner/BaudRunner.csproj
```

Every push and pull request to `main` is built and tested on Windows and Linux by `.github/workflows/ci.yml`.

## Packaging

The automated publish script builds all four platform/runtime combinations. A normal local run creates a release candidate and increments its suffix automatically:

```powershell
.\scripts\publish.ps1
```

Consecutive local runs produce `v2.x.y-rc1`, `v2.x.y-rc2`, and so on, under `publish/release-candidates`. Windows packages are ZIPs; Linux packages are `.tar.gz`, because a ZIP written on Windows carries no POSIX permissions and the extracted `BaudRunner` apphost would not be executable.

When the same script runs in GitHub Actions for a `release/v2.x.y` tag it switches to official release mode and writes the packages under `release/v2.x.y`. Manual `Run workflow` executions use release-candidate mode and upload artifacts only. After an official release, the workflow creates and squash-merges the patch-version PR automatically using the workflow token.

Framework-dependent packages require the matching .NET runtime. Self-contained packages include .NET and are larger. The Linux packages do not ship the Windows-only WMI assemblies, and the build sets `InvariantGlobalization`, so the self-contained Linux package has no `libicu` dependency.

On Linux, serial access usually requires membership of the `dialout` group (`sudo usermod -aG dialout $USER`, then log out and back in).

## Known limitations

- VT100 mode is a run list, not a cell grid: colour, CR overwrite, backspace and erase-line are modelled; cursor addressing is not. Sequences that are not modelled are consumed whole rather than leaking into the text. For most log-reading work the normal view with ANSI colour is the better choice.
- Find is available in the normal log view, not in VT100 mode.
- One session per transport: five tabs, not arbitrary simultaneous serial sessions.
