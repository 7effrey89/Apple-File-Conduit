# Apple-File-Conduit

Minimal C# app that can either run AFC copy/list/move commands directly or launch a local browser UI for browsing photos, live photos, and videos from an iPhone via the open-source `libimobiledevice` stack.

## Prerequisites

Install .NET 8 SDK (required for `dotnet run`):

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --accept-package-agreements --accept-source-agreements
```

Install native dependencies (Linux example):

```bash
sudo apt-get install -y libimobiledevice-dev libusbmuxd-dev usbmuxd
```

Windows note:

- This project references `imobiledevice-net`, which provides the native `libimobiledevice` binaries for Windows in the build output.
- The P/Invoke resolver maps `imobiledevice-1.0` to `imobiledevice` on Windows.

Trust/unlock your iPhone and verify it is visible:

```bash
idevice_id -l
```

## Build

```bash
dotnet build /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj
```

## Usage

```bash
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- [list|--list] <remoteDirectoryPath> [deviceUdid]
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- [copy|--copy] <remoteFilePath> <localOutputPath> [deviceUdid]
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- [move|--move] <remoteFilePath> <localOutputPath> [deviceUdid]
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- [ui|--ui] [deviceUdid]
```

### UI example

```bash
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- ui
```

The UI starts a local web server, opens a browser, loads media from the iPhone `DCIM` folders, labels photo/live photo/video items, and lets you copy or move the selected items to an absolute destination folder on the computer.

- Enable **Include PhotoData** in the toolbar to also scan the optional `PhotoData` media tree when it is available through AFC.
- The browser UI recognizes additional image formats including `.dng`, `.tif`, and `.tiff`. Formats that the browser cannot preview directly are still listed and can be copied or moved.

### List example

```bash
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- list "DCIM/100APPLE"
```

### Copy example

```bash
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- copy "DCIM/100APPLE/IMG_0001.JPG" "/tmp/IMG_0001.JPG"
```

### Move example

`move` copies the remote file, computes SHA-256 for the source data and copied local file, and deletes the remote file only when the hashes match.

```bash
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- move "DCIM/100APPLE/IMG_0001.JPG" "/tmp/IMG_0001.JPG"
```
