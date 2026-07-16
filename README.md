# QuickShot

QuickShot is a tiny Windows screenshot utility built for games and ordinary
desktop windows. It runs in the system tray, captures the complete virtual
desktop with **F11**, and lets you choose the screenshot folder.

## Features

- Global F11 capture while a game or other window has focus
- Multi-monitor screenshots saved as PNG
- Selectable output folder with the choice remembered between launches
- System-tray operation with Capture, Open, and Exit commands
- Key-repeat protection so one press creates one screenshot
- No telemetry, network access, accounts, or background services

## Download

Download `QuickShot.exe` from the [latest release](../../releases/latest).

QuickShot requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

## Use

1. Run `QuickShot.exe` and approve the Windows administrator prompt. Elevated
   access allows the F11 listener to work when an elevated game has focus.
2. Select **Choose Folder** if you want a custom destination.
3. Minimize QuickShot to the system tray.
4. Press **F11** to capture the complete desktop.

By default, screenshots are stored in a `Screenshots` folder beside the EXE.

## Build from source

Requirements: Windows and the .NET 9 SDK.

```powershell
dotnet build .\QuickShot.csproj -c Release
dotnet publish .\QuickShot.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugSymbols=false -p:DebugType=None
```

## Compatibility notes

QuickShot captures the Windows desktop image. Protected video, secure Windows
screens, and some true exclusive-fullscreen or anti-cheat-protected games can
block normal desktop capture. Borderless or windowed mode is the most broadly
compatible option in those cases.

The administrator requirement is declared in `app.manifest`. The hotkey uses a
low-level keyboard hook plus physical-key polling; all processing remains local.

## License

[MIT](LICENSE)
