# Apple-File-Conduit

Minimal C# app that can either run AFC copy/list/move commands directly or launch a local browser UI for browsing photos, live photos, and videos from an iPhone via the open-source `libimobiledevice` stack.

The UI media pipeline uses a hybrid transport model:

- **PTP** for media enumeration/metadata and media delete operations (Photos-safe object deletion)
- **AFC** for file copy/move bytes and filesystem browsing operations
- **AFC fallback** for media enumeration if PTP is unavailable

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

The UI starts a local web server, opens a browser, and provides two views:

- **Media view**: choose options (including **Include PhotoData**) and click **Scan Media** to load photo/live photo/video items, then copy or move selected media to an absolute local destination.
- **File system view**: browse remote folders, select folders/files, and copy, move, or delete selected paths.

- Enable **Include PhotoData** in the toolbar to also scan the optional `PhotoData` media tree when it is available through AFC.
- The browser UI recognizes additional image formats including `.dng`, `.tif`, and `.tiff`. Formats that the browser cannot preview directly are still listed and can be copied or moved.
- Media scans are cached and refreshed in the background so repeated loads avoid full rescans (PTP first, AFC fallback, with a short retry cooldown when PTP is unavailable).
- When PTP is unavailable, the console prints extra troubleshooting details and common causes while the scan continues through AFC.
- If a scan falls back to AFC, a **Retry PTP** button appears in the toolbar. Click it after unlocking/trusting the phone to clear the retry cooldown and immediately attempt a fresh PTP scan.
- File system listings fetch metadata in parallel across multiple AFC sessions for faster large-folder browsing.

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
