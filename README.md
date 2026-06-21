# Apple-File-Conduit

Minimal C# console demo that copies a file from an iPhone using Apple's AFC service via the open-source `libimobiledevice` stack.

## Prerequisites

Install native dependencies (Linux example):

```bash
sudo apt-get install -y libimobiledevice-dev libusbmuxd-dev usbmuxd
```

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
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- <remoteFilePath> <localOutputPath> [deviceUdid]
```

Example:

```bash
dotnet run --project /home/runner/work/Apple-File-Conduit/Apple-File-Conduit/AppleFileConduitDemo.csproj -- "DCIM/100APPLE/IMG_0001.JPG" "/tmp/IMG_0001.JPG"
```
