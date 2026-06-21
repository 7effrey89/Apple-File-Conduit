using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    bool deleteSourceAfterCopy = false;
    bool listRemotePath = false;
    bool launchUi = false;
    int firstPathArgumentIndex = 0;

    if (args.Length > 0)
    {
        if (args[0] is "--list" or "list")
        {
            listRemotePath = true;
            firstPathArgumentIndex = 1;
        }
        else if (args[0] is "--move" or "move")
        {
            deleteSourceAfterCopy = true;
            firstPathArgumentIndex = 1;
        }
        else if (args[0] is "--copy" or "copy")
        {
            firstPathArgumentIndex = 1;
        }
        else if (args[0] is "--ui" or "ui")
        {
            launchUi = true;
            firstPathArgumentIndex = 1;
        }
    }

    string remotePath;
    string? localPath = null;
    string? udid;

    if (launchUi)
    {
        if (args.Length - firstPathArgumentIndex > 1)
        {
            WriteUsage();
            return 1;
        }

        udid = args.Length - firstPathArgumentIndex == 1 ? args[firstPathArgumentIndex] : null;
        return await RunUiAsync(udid);
    }

    if (listRemotePath)
    {
        if (args.Length - firstPathArgumentIndex < 1 || args.Length - firstPathArgumentIndex > 2)
        {
            WriteUsage();
            return 1;
        }

        remotePath = args[firstPathArgumentIndex];
        udid = args.Length - firstPathArgumentIndex == 2 ? args[firstPathArgumentIndex + 1] : null;
    }
    else
    {
        if (args.Length - firstPathArgumentIndex < 2 || args.Length - firstPathArgumentIndex > 3)
        {
            WriteUsage();
            return 1;
        }

        remotePath = args[firstPathArgumentIndex];
        localPath = args[firstPathArgumentIndex + 1];
        udid = args.Length - firstPathArgumentIndex == 3 ? args[firstPathArgumentIndex + 2] : null;
    }

    try
    {
        using AfcSession session = AfcSession.Connect(udid);

        if (listRemotePath)
        {
            ListRemoteDirectory(session.AfcClient, remotePath);
            return 0;
        }

        if (localPath is null)
        {
            throw new InvalidOperationException("Local output path is required for copy and move modes.");
        }

        byte[] sourceHash = CopyRemoteFileToLocalAndComputeHash(session.AfcClient, remotePath, localPath);

        if (deleteSourceAfterCopy)
        {
            byte[] localHash = ComputeLocalFileHash(localPath);

            if (!CryptographicOperations.FixedTimeEquals(sourceHash, localHash))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for '{remotePath}' and '{localPath}'. Remote source file was not deleted."
                );
            }

            ThrowIfError(NativeMethods.afc_remove_path(session.AfcClient, remotePath), $"Unable to delete remote file '{remotePath}'");
            Console.WriteLine($"Copied '{remotePath}' to '{localPath}', verified SHA-256, and deleted the remote source file.");
            return 0;
        }

        Console.WriteLine($"Copied '{remotePath}' to '{localPath}'.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> RunUiAsync(string? udid)
{
    using HttpListener listener = new();
    int port = GetAvailablePort();
    string prefix = $"http://127.0.0.1:{port}/";

    listener.Prefixes.Add(prefix);
    listener.Start();

    using CancellationTokenSource shutdown = new();

    void StopServer(object? _, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        shutdown.Cancel();
        if (listener.IsListening)
        {
            listener.Stop();
        }
    }

    Console.CancelKeyPress += StopServer;

    TryOpenBrowser(prefix);
    Console.WriteLine($"Media browser UI available at {prefix}");
    Console.WriteLine("Press Ctrl+C to stop.");

    try
    {
        while (!shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleUiRequestAsync(context, udid));
        }

        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= StopServer;

        if (listener.IsListening)
        {
            listener.Stop();
        }
    }
}

static async Task HandleUiRequestAsync(HttpListenerContext context, string? udid)
{
    try
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";

        if (path is "/" or "/index.html")
        {
            await WriteHtmlResponseAsync(context.Response, UiPage.Html);
            return;
        }

        if (path == "/api/media" && context.Request.HttpMethod == "GET")
        {
            bool includeAdditionalRoots = IsTruthy(context.Request.QueryString["includeAdditionalRoots"]);
            using AfcSession session = AfcSession.Connect(udid);
            MediaEnumerationResult media = EnumerateMediaAssets(session.AfcClient, includeAdditionalRoots);
            IReadOnlyList<MediaAsset> assets = media.Assets;

            MediaLibraryResponse payload = new(
                assets.Select(MediaAssetView.FromAsset).ToArray(),
                assets.Count(x => x.Kind == MediaKind.Photo),
                assets.Count(x => x.Kind == MediaKind.LivePhoto),
                assets.Count(x => x.Kind == MediaKind.Video),
                media.ScannedRoots
            );

            await WriteJsonResponseAsync(context.Response, payload);
            return;
        }

        if (path == "/api/file" && context.Request.HttpMethod == "GET")
        {
            string? id = context.Request.QueryString["id"];

            if (string.IsNullOrWhiteSpace(id))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("A media id is required."));
                return;
            }

            string remotePath = DecodeAssetId(id);

            using AfcSession session = AfcSession.Connect(udid);
            await StreamRemoteFileToHttpResponseAsync(session.AfcClient, remotePath, context.Response);
            return;
        }

        if (path == "/api/transfer" && context.Request.HttpMethod == "POST")
        {
            TransferRequest? request = await JsonSerializer.DeserializeAsync<TransferRequest>(context.Request.InputStream, AppJson.Options);

            if (request is null || request.AssetIds is null || request.AssetIds.Length == 0)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("Select at least one item before copying or moving."));
                return;
            }

            if (string.IsNullOrWhiteSpace(request.DestinationDirectory) || !Path.IsPathRooted(request.DestinationDirectory))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("Enter an absolute destination directory path."));
                return;
            }

            bool deleteAfterCopy = string.Equals(request.Operation, "move", StringComparison.OrdinalIgnoreCase);

            using AfcSession session = AfcSession.Connect(udid);
            IReadOnlyList<MediaAsset> assets = EnumerateMediaAssets(session.AfcClient, request.IncludeAdditionalRoots).Assets;
            Dictionary<string, MediaAsset> assetsById = assets.ToDictionary(x => x.Id, StringComparer.Ordinal);
            List<string> copiedPaths = [];

            foreach (string assetId in request.AssetIds.Distinct(StringComparer.Ordinal))
            {
                if (!assetsById.TryGetValue(assetId, out MediaAsset? asset))
                {
                    throw new InvalidOperationException("One of the selected media items is no longer available on the device.");
                }

                List<(string RemotePath, string LocalPath, byte[] SourceHash)> transferredFiles = [];

                foreach (string remotePath in asset.RemotePaths)
                {
                    string localPath = BuildLocalOutputPath(request.DestinationDirectory, remotePath);
                    byte[] sourceHash = CopyRemoteFileToLocalAndComputeHash(session.AfcClient, remotePath, localPath);
                    transferredFiles.Add((remotePath, localPath, sourceHash));
                    copiedPaths.Add(localPath);
                }

                if (deleteAfterCopy)
                {
                    foreach ((string remotePath, string localPath, byte[] sourceHash) in transferredFiles)
                    {
                        byte[] localHash = ComputeLocalFileHash(localPath);

                        if (!CryptographicOperations.FixedTimeEquals(sourceHash, localHash))
                        {
                            throw new InvalidOperationException(
                                $"SHA-256 mismatch for '{remotePath}' and '{localPath}'. Remote source files were not deleted."
                            );
                        }
                    }

                    foreach ((string remotePath, _, _) in transferredFiles)
                    {
                        ThrowIfError(NativeMethods.afc_remove_path(session.AfcClient, remotePath), $"Unable to delete remote file '{remotePath}'");
                    }
                }
            }

            await WriteJsonResponseAsync(
                context.Response,
                new TransferResponse(
                    deleteAfterCopy ? "Moved the selected items." : "Copied the selected items.",
                    copiedPaths.ToArray()
                )
            );

            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        await WriteJsonResponseAsync(context.Response, new ErrorResponse("The requested resource was not found."));
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        await WriteJsonResponseAsync(context.Response, new ErrorResponse(ex.Message));
    }
    finally
    {
        context.Response.OutputStream.Close();
    }
}

static async Task WriteHtmlResponseAsync(HttpListenerResponse response, string html)
{
    byte[] buffer = Encoding.UTF8.GetBytes(html);
    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = "text/html; charset=utf-8";
    response.ContentLength64 = buffer.Length;
    await response.OutputStream.WriteAsync(buffer);
}

static async Task WriteJsonResponseAsync(HttpListenerResponse response, object payload)
{
    byte[] buffer = JsonSerializer.SerializeToUtf8Bytes(payload, AppJson.Options);
    response.ContentType = "application/json; charset=utf-8";
    response.ContentLength64 = buffer.Length;
    await response.OutputStream.WriteAsync(buffer);
}

static async Task StreamRemoteFileToHttpResponseAsync(IntPtr afcClient, string remotePath, HttpListenerResponse response)
{
    ulong afcFileHandle = 0;
    ThrowIfError(
        NativeMethods.afc_file_open(afcClient, remotePath, NativeMethods.AFC_FOPEN_RDONLY, ref afcFileHandle),
        $"Unable to open remote file '{remotePath}'"
    );

    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = GetContentType(remotePath);
    response.SendChunked = true;

    try
    {
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            uint bytesRead = 0;
            ThrowIfError(
                NativeMethods.afc_file_read(afcClient, afcFileHandle, buffer, (uint)buffer.Length, ref bytesRead),
                "Failed while reading remote file"
            );

            if (bytesRead == 0)
            {
                break;
            }

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, (int)bytesRead));
        }
    }
    finally
    {
        if (afcFileHandle != 0)
        {
            NativeMethods.afc_file_close(afcClient, afcFileHandle);
        }
    }
}

static void WriteUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  AppleFileConduitDemo [ui|--ui] [deviceUdid]");
    Console.WriteLine("  AppleFileConduitDemo [list|--list] <remoteDirectoryPath> [deviceUdid]");
    Console.WriteLine("  AppleFileConduitDemo [copy|--copy] <remoteFilePath> <localOutputPath> [deviceUdid]");
    Console.WriteLine("  AppleFileConduitDemo [move|--move] <remoteFilePath> <localOutputPath> [deviceUdid]");
}

static void ListRemoteDirectory(IntPtr afcClient, string remotePath)
{
    IntPtr directoryEntries = IntPtr.Zero;
    ThrowIfError(
        NativeMethods.afc_read_directory(afcClient, remotePath, ref directoryEntries),
        $"Unable to list remote directory '{remotePath}'"
    );

    try
    {
        Console.WriteLine($"Listing '{remotePath}':");

        nint offset = 0;
        bool hasVisibleEntries = false;

        while (true)
        {
            IntPtr entryPointer = Marshal.ReadIntPtr(directoryEntries, (int)offset);
            if (entryPointer == IntPtr.Zero)
            {
                break;
            }

            string? entry = Marshal.PtrToStringUTF8(entryPointer);
            offset += IntPtr.Size;

            if (string.IsNullOrEmpty(entry) || entry is "." or "..")
            {
                continue;
            }

            hasVisibleEntries = true;
            Console.WriteLine(entry);
        }

        if (!hasVisibleEntries)
        {
            Console.WriteLine("(empty)");
        }
    }
    finally
    {
        if (directoryEntries != IntPtr.Zero)
        {
            NativeMethods.afc_dictionary_free(directoryEntries);
        }
    }
}

static MediaEnumerationResult EnumerateMediaAssets(IntPtr afcClient, bool includeAdditionalRoots)
{
    List<RemoteFileEntry> files = [];
    List<string> scannedRoots = [];
    List<string> rootsToScan = ["DCIM"];

    if (includeAdditionalRoots)
    {
        rootsToScan.Add("PhotoData");
    }

    foreach (string root in rootsToScan)
    {
        if (string.Equals(root, "DCIM", StringComparison.OrdinalIgnoreCase))
        {
            EnumerateRemoteFiles(afcClient, root, files);
            scannedRoots.Add(root);
            continue;
        }

        try
        {
            EnumerateRemoteFiles(afcClient, root, files);
            scannedRoots.Add(root);
        }
        catch (InvalidOperationException)
        {
        }
    }

    files = files
        .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    List<RemoteFileEntry> images = files.Where(x => IsImageFile(x.Path)).OrderByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    List<RemoteFileEntry> videos = files.Where(x => IsVideoFile(x.Path)).OrderByDescending(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();

    Dictionary<string, RemoteFileEntry> videosByStem = videos
        .GroupBy(GetStemKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    HashSet<string> pairedVideos = new(StringComparer.OrdinalIgnoreCase);
    List<MediaAsset> assets = [];

    foreach (RemoteFileEntry image in images)
    {
        string stemKey = GetStemKey(image);

        if (videosByStem.TryGetValue(stemKey, out RemoteFileEntry? pairedVideo))
        {
            pairedVideos.Add(pairedVideo.Path);
            assets.Add(
                new MediaAsset(
                    EncodeAssetId(image.Path),
                    Path.GetFileNameWithoutExtension(image.Path),
                    MediaKind.LivePhoto,
                    image.Path,
                    new[] { image.Path, pairedVideo.Path }
                )
            );
            continue;
        }

        assets.Add(
            new MediaAsset(
                EncodeAssetId(image.Path),
                Path.GetFileNameWithoutExtension(image.Path),
                MediaKind.Photo,
                image.Path,
                new[] { image.Path }
            )
        );
    }

    foreach (RemoteFileEntry video in videos)
    {
        if (pairedVideos.Contains(video.Path))
        {
            continue;
        }

        assets.Add(
            new MediaAsset(
                EncodeAssetId(video.Path),
                Path.GetFileNameWithoutExtension(video.Path),
                MediaKind.Video,
                video.Path,
                new[] { video.Path }
            )
        );
    }

    return new MediaEnumerationResult(
        assets.OrderByDescending(x => x.PrimaryRemotePath, StringComparer.OrdinalIgnoreCase).ToArray(),
        scannedRoots.ToArray()
    );
}

static void EnumerateRemoteFiles(IntPtr afcClient, string remoteDirectoryPath, List<RemoteFileEntry> files)
{
    foreach (string entry in ReadRemoteDirectoryEntries(afcClient, remoteDirectoryPath))
    {
        string childPath = CombineRemotePath(remoteDirectoryPath, entry);
        RemoteEntryInfo info = GetRemoteEntryInfo(afcClient, childPath);

        if (info.IsDirectory)
        {
            EnumerateRemoteFiles(afcClient, childPath, files);
            continue;
        }

        if (info.IsFile)
        {
            files.Add(new RemoteFileEntry(childPath));
        }
    }
}

static IReadOnlyList<string> ReadRemoteDirectoryEntries(IntPtr afcClient, string remotePath)
{
    IntPtr directoryEntries = IntPtr.Zero;
    ThrowIfError(
        NativeMethods.afc_read_directory(afcClient, remotePath, ref directoryEntries),
        $"Unable to list remote directory '{remotePath}'"
    );

    try
    {
        List<string> entries = [];
        nint offset = 0;

        while (true)
        {
            IntPtr entryPointer = Marshal.ReadIntPtr(directoryEntries, (int)offset);
            if (entryPointer == IntPtr.Zero)
            {
                break;
            }

            string? entry = Marshal.PtrToStringUTF8(entryPointer);
            offset += IntPtr.Size;

            if (string.IsNullOrEmpty(entry) || entry is "." or "..")
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }
    finally
    {
        if (directoryEntries != IntPtr.Zero)
        {
            NativeMethods.afc_dictionary_free(directoryEntries);
        }
    }
}

static RemoteEntryInfo GetRemoteEntryInfo(IntPtr afcClient, string remotePath)
{
    IntPtr fileInfoDictionary = IntPtr.Zero;
    ThrowIfError(
        NativeMethods.afc_get_file_info(afcClient, remotePath, ref fileInfoDictionary),
        $"Unable to inspect remote path '{remotePath}'"
    );

    try
    {
        Dictionary<string, string> values = ReadDictionary(fileInfoDictionary);
        values.TryGetValue("st_ifmt", out string? fileType);
        return new RemoteEntryInfo(
            string.Equals(fileType, "S_IFDIR", StringComparison.Ordinal),
            string.Equals(fileType, "S_IFREG", StringComparison.Ordinal)
        );
    }
    finally
    {
        if (fileInfoDictionary != IntPtr.Zero)
        {
            NativeMethods.afc_dictionary_free(fileInfoDictionary);
        }
    }
}

static Dictionary<string, string> ReadDictionary(IntPtr dictionary)
{
    Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    nint offset = 0;

    while (true)
    {
        IntPtr keyPointer = Marshal.ReadIntPtr(dictionary, (int)offset);
        if (keyPointer == IntPtr.Zero)
        {
            break;
        }

        offset += IntPtr.Size;
        IntPtr valuePointer = Marshal.ReadIntPtr(dictionary, (int)offset);
        if (valuePointer == IntPtr.Zero)
        {
            break;
        }

        offset += IntPtr.Size;

        string? key = Marshal.PtrToStringUTF8(keyPointer);
        string? value = Marshal.PtrToStringUTF8(valuePointer);

        if (!string.IsNullOrWhiteSpace(key) && value is not null)
        {
            values[key] = value;
        }
    }

    return values;
}

static string BuildLocalOutputPath(string destinationDirectory, string remotePath)
{
    string relativePath = remotePath.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase)
        ? remotePath["DCIM/".Length..]
        : remotePath.TrimStart('/');

    string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    return Path.Combine(destinationDirectory, Path.Combine(segments));
}

static byte[] CopyRemoteFileToLocalAndComputeHash(IntPtr afcClient, string remotePath, string localPath)
{
    ulong afcFileHandle = 0;
    ThrowIfError(
        NativeMethods.afc_file_open(afcClient, remotePath, NativeMethods.AFC_FOPEN_RDONLY, ref afcFileHandle),
        $"Unable to open remote file '{remotePath}'"
    );

    try
    {
        string? localDirectory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(localDirectory))
        {
            Directory.CreateDirectory(localDirectory);
        }

        using FileStream output = File.Create(localPath);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            uint bytesRead = 0;
            ThrowIfError(
                NativeMethods.afc_file_read(afcClient, afcFileHandle, buffer, (uint)buffer.Length, ref bytesRead),
                "Failed while reading remote file"
            );

            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, (int)bytesRead);
            output.Write(buffer, 0, (int)bytesRead);
        }

        output.Flush();
        return hash.GetHashAndReset();
    }
    finally
    {
        if (afcFileHandle != 0)
        {
            NativeMethods.afc_file_close(afcClient, afcFileHandle);
        }
    }
}

static byte[] ComputeLocalFileHash(string localPath)
{
    using SHA256 sha256 = SHA256.Create();
    using FileStream stream = File.OpenRead(localPath);
    return sha256.ComputeHash(stream);
}

static int GetAvailablePort()
{
    TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();

    try
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}

static void TryOpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Process.Start(
                new ProcessStartInfo("xdg-open", url)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            );
        }
    }
    catch
    {
    }
}

static string GetContentType(string remotePath)
{
    return Path.GetExtension(remotePath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".heif" => "image/heif",
        ".dng" => "image/x-adobe-dng",
        ".tif" or ".tiff" => "image/tiff",
        ".mov" => "video/quicktime",
        ".mp4" => "video/mp4",
        ".m4v" => "video/x-m4v",
        _ => "application/octet-stream"
    };
}

static string GetStemKey(RemoteFileEntry entry)
{
    string directory = Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? string.Empty;
    string fileName = Path.GetFileNameWithoutExtension(entry.Path);
    return $"{directory}/{fileName}";
}

static string CombineRemotePath(string left, string right)
{
    return $"{left.TrimEnd('/')}/{right.TrimStart('/')}";
}

static bool IsImageFile(string remotePath)
{
    return Path.GetExtension(remotePath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".heif" or ".dng" or ".tif" or ".tiff" => true,
        _ => false
    };
}

static bool IsTruthy(string? value)
{
    return value is not null && (
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
    );
}

static bool IsVideoFile(string remotePath)
{
    return Path.GetExtension(remotePath).ToLowerInvariant() switch
    {
        ".mov" or ".mp4" or ".m4v" => true,
        _ => false
    };
}

static string EncodeAssetId(string remotePath)
{
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(remotePath)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static string DecodeAssetId(string id)
{
    string normalized = id.Replace('-', '+').Replace('_', '/');

    switch (normalized.Length % 4)
    {
        case 2:
            normalized += "==";
            break;
        case 3:
            normalized += "=";
            break;
    }

    return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
}

static void ThrowIfError(int errorCode, string message)
{
    NativeError.ThrowIfError(errorCode, message);
}

internal sealed class AfcSession : IDisposable
{
    private AfcSession(IntPtr device, IntPtr lockdowndClient, IntPtr serviceDescriptor, IntPtr afcClient)
    {
        Device = device;
        LockdowndClient = lockdowndClient;
        ServiceDescriptor = serviceDescriptor;
        AfcClient = afcClient;
    }

    public IntPtr Device { get; }
    public IntPtr LockdowndClient { get; }
    public IntPtr ServiceDescriptor { get; }
    public IntPtr AfcClient { get; }

    public static AfcSession Connect(string? udid)
    {
        IntPtr device = IntPtr.Zero;
        IntPtr lockdowndClient = IntPtr.Zero;
        IntPtr serviceDescriptor = IntPtr.Zero;
        IntPtr afcClient = IntPtr.Zero;

        try
        {
            NativeError.ThrowIfError(NativeMethods.idevice_new(ref device, udid), "Unable to connect to iOS device");
            NativeError.ThrowIfError(
                NativeMethods.lockdownd_client_new_with_handshake(device, ref lockdowndClient, "AppleFileConduitDemo"),
                "Unable to start lockdownd session"
            );
            NativeError.ThrowIfError(
                NativeMethods.lockdownd_start_service(lockdowndClient, "com.apple.afc", ref serviceDescriptor),
                "Unable to start AFC service (is the device trusted and unlocked?)"
            );
            NativeError.ThrowIfError(NativeMethods.afc_client_new(device, serviceDescriptor, ref afcClient), "Unable to create AFC client");
            return new AfcSession(device, lockdowndClient, serviceDescriptor, afcClient);
        }
        catch
        {
            if (afcClient != IntPtr.Zero)
            {
                NativeMethods.afc_client_free(afcClient);
            }

            if (serviceDescriptor != IntPtr.Zero)
            {
                NativeMethods.lockdownd_service_descriptor_free(serviceDescriptor);
            }

            if (lockdowndClient != IntPtr.Zero)
            {
                NativeMethods.lockdownd_client_free(lockdowndClient);
            }

            if (device != IntPtr.Zero)
            {
                NativeMethods.idevice_free(device);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (AfcClient != IntPtr.Zero)
        {
            NativeMethods.afc_client_free(AfcClient);
        }

        if (ServiceDescriptor != IntPtr.Zero)
        {
            NativeMethods.lockdownd_service_descriptor_free(ServiceDescriptor);
        }

        if (LockdowndClient != IntPtr.Zero)
        {
            NativeMethods.lockdownd_client_free(LockdowndClient);
        }

        if (Device != IntPtr.Zero)
        {
            NativeMethods.idevice_free(Device);
        }
    }
}

internal sealed record RemoteFileEntry(string Path);

internal sealed record RemoteEntryInfo(bool IsDirectory, bool IsFile);

internal sealed record MediaAsset(
    string Id,
    string Name,
    MediaKind Kind,
    string PrimaryRemotePath,
    IReadOnlyList<string> RemotePaths
);

internal enum MediaKind
{
    Photo,
    LivePhoto,
    Video
}

internal sealed record MediaEnumerationResult(IReadOnlyList<MediaAsset> Assets, string[] ScannedRoots);

internal sealed record MediaAssetView(
    string Id,
    string Name,
    string Kind,
    string PrimaryRemotePath,
    string PreviewUrl,
    string PreviewMode,
    string RelativePath
)
{
    public static MediaAssetView FromAsset(MediaAsset asset)
    {
        string extension = Path.GetExtension(asset.PrimaryRemotePath).ToLowerInvariant();
        string previewMode = asset.Kind == MediaKind.Video
            ? "video"
            : extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" ? "image" : "placeholder";
        return new MediaAssetView(
            asset.Id,
            asset.Name,
            asset.Kind switch
            {
                MediaKind.LivePhoto => "live-photo",
                MediaKind.Video => "video",
                _ => "photo"
            },
            asset.PrimaryRemotePath,
            $"/api/file?id={Uri.EscapeDataString(asset.Id)}",
            previewMode,
            asset.PrimaryRemotePath.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase)
                ? asset.PrimaryRemotePath["DCIM/".Length..]
                : asset.PrimaryRemotePath
        );
    }
}

internal sealed record MediaLibraryResponse(MediaAssetView[] Items, int PhotoCount, int LivePhotoCount, int VideoCount, string[] ScannedRoots);

internal sealed record TransferRequest(string[] AssetIds, string DestinationDirectory, string Operation, bool IncludeAdditionalRoots);

internal sealed record TransferResponse(string Message, string[] LocalPaths);

internal sealed record ErrorResponse(string Message);

internal static class NativeError
{
    public static void ThrowIfError(int errorCode, string message)
    {
        if (errorCode != 0)
        {
            throw new InvalidOperationException($"{message}. Native error code: {errorCode}");
        }
    }
}

internal static class AppJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal static class UiPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Apple File Conduit</title>
  <style>
    :root {
      color-scheme: light dark;
      --bg: #f4f6fb;
      --panel: rgba(255,255,255,0.92);
      --text: #162033;
      --muted: #6c768a;
      --accent: #2867ff;
      --accent-strong: #0d4eff;
      --border: rgba(34, 50, 84, 0.12);
      --selected: rgba(40, 103, 255, 0.16);
      --shadow: 0 14px 40px rgba(26, 39, 73, 0.14);
      font-family: Inter, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: linear-gradient(180deg, #edf2ff 0%, #f7f9fc 100%);
      color: var(--text);
    }
    .page {
      max-width: 1400px;
      margin: 0 auto;
      padding: 24px;
    }
    .header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      margin-bottom: 20px;
    }
    h1 {
      margin: 0;
      font-size: 1.9rem;
    }
    .subtitle {
      color: var(--muted);
      margin-top: 6px;
    }
    .toolbar, .summary {
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 18px;
      box-shadow: var(--shadow);
    }
    .toolbar {
      padding: 16px;
      display: grid;
      gap: 12px;
      grid-template-columns: minmax(280px, 1fr) repeat(5, auto);
      align-items: center;
      margin-bottom: 16px;
    }
    .summary {
      padding: 14px 16px;
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
      margin-bottom: 18px;
    }
    input, select, button {
      font: inherit;
    }
    input:not([type="checkbox"]), select {
      width: 100%;
      padding: 12px 14px;
      border-radius: 12px;
      border: 1px solid var(--border);
      background: rgba(255,255,255,0.9);
      color: inherit;
    }
    .toggle {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      padding: 0 6px;
      color: var(--muted);
      white-space: nowrap;
    }
    .toggle input[type="checkbox"] {
      width: 18px;
      height: 18px;
      margin: 0;
      accent-color: var(--accent);
    }
    button {
      border: 0;
      border-radius: 12px;
      padding: 12px 16px;
      cursor: pointer;
      background: #e8edff;
      color: #2246b7;
      transition: transform .15s ease, background .15s ease;
    }
    button:hover { transform: translateY(-1px); }
    button.primary {
      background: var(--accent);
      color: white;
    }
    button.danger {
      background: #ffe5e5;
      color: #c03838;
    }
    button:disabled {
      cursor: not-allowed;
      opacity: .6;
      transform: none;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      background: rgba(40,103,255,0.08);
      border-radius: 999px;
      color: #3152c7;
      font-size: .95rem;
    }
    .progress-bar {
      height: 4px;
      background: color-mix(in srgb, #2867ff 15%, transparent);
      border-radius: 2px;
      overflow: hidden;
      margin-bottom: 12px;
      display: none;
      position: relative;
    }
    .progress-bar.active { display: block; }
    .progress-bar::after {
      content: '';
      position: absolute;
      top: 0; bottom: 0; left: 0;
      width: 40%;
      background: #2867ff;
      border-radius: 2px;
      animation: indeterminate 1.4s ease-in-out infinite;
    }
    @keyframes indeterminate {
      0%   { transform: translateX(-100%); }
      100% { transform: translateX(250%); }
    }
    .status {
      min-height: 24px;
      margin-bottom: 16px;
      color: var(--muted);
      font-size: .98rem;
    }
    .status.error { color: #b63838; }
    .grid {
      display: grid;
      gap: 16px;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
    }
    .card {
      position: relative;
      overflow: hidden;
      border-radius: 20px;
      background: var(--panel);
      border: 2px solid transparent;
      box-shadow: var(--shadow);
      cursor: pointer;
      transition: transform .15s ease, border-color .15s ease, background .15s ease;
    }
    .card:hover { transform: translateY(-2px); }
    .card.selected {
      border-color: var(--accent);
      background: var(--selected);
    }
    .preview {
      aspect-ratio: 1 / 1;
      width: 100%;
      object-fit: cover;
      display: block;
      background: linear-gradient(135deg, #d9e3ff, #edf2ff);
    }
    .video-preview {
      position: relative;
      aspect-ratio: 1 / 1;
      background: linear-gradient(135deg, #7e8cb8, #1c2238);
      display: grid;
      place-items: center;
      color: white;
    }
    .video-preview video {
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
      background: transparent;
    }
    .fallback-icon {
      font-size: 3rem;
      opacity: .9;
    }
    .card-footer {
      padding: 12px 14px 16px;
    }
    .card-title {
      margin: 0 0 6px;
      font-size: 1rem;
      word-break: break-word;
    }
    .card-path {
      margin: 0;
      color: var(--muted);
      font-size: .88rem;
      word-break: break-word;
    }
    .badge, .selector, .duration {
      position: absolute;
      z-index: 2;
      border-radius: 999px;
      backdrop-filter: blur(10px);
    }
    .badge {
      top: 12px;
      left: 12px;
      padding: 7px 10px;
      background: rgba(20, 30, 58, 0.72);
      color: white;
      font-size: .8rem;
      font-weight: 600;
    }
    .selector {
      top: 12px;
      right: 12px;
      width: 28px;
      height: 28px;
      display: grid;
      place-items: center;
      background: rgba(255,255,255,0.78);
      border: 2px solid rgba(20,30,58,0.15);
      color: white;
      font-weight: 700;
    }
    .card.selected .selector {
      background: var(--accent);
      border-color: white;
    }
    .duration {
      right: 12px;
      bottom: 72px;
      padding: 6px 10px;
      background: rgba(20, 30, 58, 0.75);
      color: white;
      font-size: .82rem;
    }
    .empty {
      padding: 48px 24px;
      text-align: center;
      color: var(--muted);
      background: var(--panel);
      border-radius: 20px;
      border: 1px solid var(--border);
      box-shadow: var(--shadow);
    }
    @media (max-width: 900px) {
      .toolbar { grid-template-columns: 1fr 1fr; }
    }
    @media (max-width: 640px) {
      .page { padding: 16px; }
      .toolbar { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>
  <div class="page">
    <div class="header">
      <div>
        <h1>Apple File Conduit</h1>
        <div class="subtitle">Browse photos, live photos, and videos from the connected iPhone and copy or move the selected items.</div>
      </div>
    </div>

    <div class="toolbar">
      <input id="destination" placeholder="Enter an absolute destination folder, for example C:\Users\you\Pictures\Imports or /home/you/Pictures/Imports">
      <select id="filter">
        <option value="all">All media</option>
        <option value="photo">Photos</option>
        <option value="live-photo">Live Photos</option>
        <option value="video">Videos</option>
      </select>
      <label class="toggle">
        <input type="checkbox" id="includeAdditionalRoots">
        <span>Include PhotoData</span>
      </label>
      <button class="primary" id="copyButton">Copy Selected</button>
      <button class="danger" id="moveButton">Move Selected</button>
      <button id="refreshButton">Refresh</button>
    </div>

    <div class="progress-bar" id="progressBar"></div>
    <div class="summary" id="summary"></div>
    <div class="status" id="status">Loading media from the device…</div>
    <div class="grid" id="grid"></div>
  </div>

  <script>
    const state = {
      items: [],
      selected: new Set(),
      filter: 'all',
      busy: false
    };

    const summary = document.getElementById('summary');
    const status = document.getElementById('status');
    const progressBar = document.getElementById('progressBar');
    const grid = document.getElementById('grid');
    const destinationInput = document.getElementById('destination');
    const filterInput = document.getElementById('filter');
    const includeAdditionalRootsInput = document.getElementById('includeAdditionalRoots');
    const copyButton = document.getElementById('copyButton');
    const moveButton = document.getElementById('moveButton');
    const refreshButton = document.getElementById('refreshButton');

    filterInput.addEventListener('change', () => {
      state.filter = filterInput.value;
      renderGrid();
      renderSummary();
    });

    includeAdditionalRootsInput.addEventListener('change', () => loadMedia());
    refreshButton.addEventListener('click', () => loadMedia());
    copyButton.addEventListener('click', () => transfer('copy'));
    moveButton.addEventListener('click', () => transfer('move'));

    function setBusy(value) {
      state.busy = value;
      copyButton.disabled = value;
      moveButton.disabled = value;
      refreshButton.disabled = value;
      filterInput.disabled = value;
      includeAdditionalRootsInput.disabled = value;
      progressBar.classList.toggle('active', value);
    }

    function setStatus(message, isError = false) {
      status.textContent = message;
      status.classList.toggle('error', isError);
    }

    function filteredItems() {
      if (state.filter === 'all') {
        return state.items;
      }

      return state.items.filter(item => item.kind === state.filter);
    }

    function renderSummary() {
      const visible = filteredItems();
      const selectedVisible = visible.filter(item => state.selected.has(item.id)).length;
      const totalSelected = state.selected.size;
      const photoCount = state.items.filter(item => item.kind === 'photo').length;
      const liveCount = state.items.filter(item => item.kind === 'live-photo').length;
      const videoCount = state.items.filter(item => item.kind === 'video').length;

      summary.innerHTML = [
        chip(`${state.items.length} items`),
        chip(`${photoCount} photos`),
        chip(`${liveCount} live photos`),
        chip(`${videoCount} videos`),
        chip(`${totalSelected} selected`),
        state.filter === 'all' ? '' : chip(`${selectedVisible} selected in current filter`)
      ].join('');
    }

    function chip(text) {
      return `<div class="chip">${text}</div>`;
    }

    function renderGrid() {
      const visible = filteredItems();

      if (visible.length === 0) {
        grid.innerHTML = '<div class="empty">No matching media was found.</div>';
        return;
      }

      grid.innerHTML = '';

      for (const item of visible) {
        const selected = state.selected.has(item.id);
        const card = document.createElement('article');
        card.className = `card${selected ? ' selected' : ''}`;
        card.tabIndex = 0;
        card.addEventListener('click', () => toggleSelection(item.id));
        card.addEventListener('keydown', event => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            toggleSelection(item.id);
          }
        });

        const badgeText = item.kind === 'live-photo' ? 'Live' : item.kind === 'video' ? 'Video' : 'Photo';
        const durationMarkup = item.kind === 'video' ? '<div class="duration">Video</div>' : '';
        const selectorText = selected ? '&#10003;' : '';
        const preview = item.previewMode === 'video'
          ? `<div class="video-preview"><video preload="auto" muted playsinline src="${item.previewUrl}"></video><div class="fallback-icon">▶</div></div>`
          : item.previewMode === 'image'
            ? `<img class="preview" loading="lazy" src="${item.previewUrl}" alt="${escapeHtml(item.name)}">`
            : buildImageFallbackMarkup(item);

        card.innerHTML = `
          ${preview}
          <div class="badge">${badgeText}</div>
          <div class="selector">${selectorText}</div>
          ${durationMarkup}
          <div class="card-footer">
            <h2 class="card-title">${escapeHtml(item.name)}</h2>
            <p class="card-path">${escapeHtml(item.relativePath)}</p>
          </div>
        `;

        if (item.previewMode === 'video') {
          const video = card.querySelector('video');
          const fallback = card.querySelector('.fallback-icon');
          const duration = card.querySelector('.duration');

          video.addEventListener('loadedmetadata', () => {
            if (Number.isFinite(video.duration)) {
              duration.textContent = formatDuration(video.duration);
            }
          });

          video.addEventListener('loadeddata', () => {
            fallback.style.display = 'none';
          });

          video.addEventListener('error', () => {
            fallback.style.display = 'grid';
            duration.textContent = 'Video';
          });
        }

        const image = card.querySelector('img');
        if (image) {
          image.addEventListener('error', () => {
            image.replaceWith(buildImageFallback(item));
          });
        }

        grid.appendChild(card);
      }
    }

    function buildImageFallbackMarkup(item) {
      return `<div class="video-preview"><div class="fallback-icon">${escapeHtml(getFileExtension(item))}</div></div>`;
    }

    function buildImageFallback(item) {
      const fallback = document.createElement('div');
      fallback.innerHTML = buildImageFallbackMarkup(item);
      return fallback.firstElementChild;
    }

    function getFileExtension(item) {
      const match = item.relativePath.match(/\.([^.\/]+)$/);
      return match ? match[1].toUpperCase() : 'FILE';
    }

    function formatRootList(roots) {
      if (!roots || roots.length === 0) {
        return 'the device';
      }

      if (roots.length === 1) {
        return roots[0];
      }

      return `${roots.slice(0, -1).join(', ')} and ${roots[roots.length - 1]}`;
    }

    function toggleSelection(id) {
      if (state.selected.has(id)) {
        state.selected.delete(id);
      } else {
        state.selected.add(id);
      }

      renderGrid();
      renderSummary();
    }

    async function loadMedia() {
      setBusy(true);
      setStatus('Loading media from the device…');

      try {
        const response = await fetch(`/api/media?includeAdditionalRoots=${includeAdditionalRootsInput.checked}`);
        const data = await response.json();

        if (!response.ok) {
          throw new Error(data.message || 'Unable to load media.');
        }

        state.items = data.items || [];
        state.selected.clear();
        renderSummary();
        renderGrid();
        setStatus(`Loaded ${state.items.length} items from ${formatRootList(data.scannedRoots)}.`);
      } catch (error) {
        state.items = [];
        state.selected.clear();
        renderSummary();
        renderGrid();
        setStatus(error.message, true);
      } finally {
        setBusy(false);
      }
    }

    async function transfer(operation) {
      if (state.selected.size === 0) {
        setStatus('Select at least one item first.', true);
        return;
      }

      const destinationDirectory = destinationInput.value.trim();
      if (!destinationDirectory) {
        setStatus('Enter an absolute destination directory path first.', true);
        destinationInput.focus();
        return;
      }

      if (operation === 'move' && !window.confirm('Move deletes the source files from the iPhone after the copied files are verified. Continue?')) {
        return;
      }

      setBusy(true);
      setStatus(`${operation === 'move' ? 'Moving' : 'Copying'} selected items…`);

      try {
        const response = await fetch('/api/transfer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            assetIds: Array.from(state.selected),
            destinationDirectory,
            operation,
            includeAdditionalRoots: includeAdditionalRootsInput.checked
          })
        });

        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || 'Transfer failed.');
        }

        setStatus(`${data.message} ${data.localPaths.length} file(s) written.`);
        await loadMedia();
      } catch (error) {
        setStatus(error.message, true);
      } finally {
        setBusy(false);
      }
    }

    function formatDuration(durationInSeconds) {
      const totalSeconds = Math.max(0, Math.round(durationInSeconds));
      const hours = Math.floor(totalSeconds / 3600);
      const minutes = Math.floor((totalSeconds % 3600) / 60);
      const seconds = totalSeconds % 60;

      if (hours > 0) {
        return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
      }

      return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
    }

    function escapeHtml(value) {
      return value
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
    }

    loadMedia();
  </script>
</body>
</html>
""";
}

internal static class NativeMethods
{
    public const ulong AFC_FOPEN_RDONLY = 0x00000001;

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_new(ref IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string? udid);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_free(IntPtr device);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_client_new_with_handshake(
        IntPtr device,
        ref IntPtr client,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_client_free(IntPtr client);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_start_service(
        IntPtr client,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier,
        ref IntPtr serviceDescriptor
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern void lockdownd_service_descriptor_free(IntPtr serviceDescriptor);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_client_new(IntPtr device, IntPtr serviceDescriptor, ref IntPtr afcClient);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_client_free(IntPtr afcClient);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_file_open(
        IntPtr afcClient,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        ulong mode,
        ref ulong handle
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_file_read(
        IntPtr afcClient,
        ulong handle,
        [Out] byte[] data,
        uint length,
        ref uint bytesRead
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_file_close(IntPtr afcClient, ulong handle);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_remove_path(IntPtr afcClient, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_read_directory(
        IntPtr afcClient,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref IntPtr directoryEntries
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_get_file_info(
        IntPtr afcClient,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref IntPtr fileInfo
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern void afc_dictionary_free(IntPtr dictionary);
}

internal static class NativeLibraryBootstrapper
{
    [ModuleInitializer]
    public static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "imobiledevice-1.0" && OperatingSystem.IsWindows())
        {
            if (NativeLibrary.TryLoad("imobiledevice", assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
