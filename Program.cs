using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ImageMagick;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    bool deleteSourceAfterCopy = false;
    bool listRemotePath = false;
    bool launchUi = false;
  bool runPtpTest = false;
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
        else if (args[0] is "--ptp-test" or "ptp-test")
        {
          runPtpTest = true;
          firstPathArgumentIndex = 1;
        }
    }

    string remotePath;
    string? localPath = null;
    string? udid;

      if (runPtpTest)
      {
        bool continuous = false;
        int intervalSeconds = 3;
        string? ptpUdid = null;

        for (int index = firstPathArgumentIndex; index < args.Length; index++)
        {
          string argument = args[index];

          if (argument is "--continuous" or "-c")
          {
            continuous = true;
            continue;
          }

          if (argument.StartsWith("--interval=", StringComparison.Ordinal))
          {
            if (!int.TryParse(argument["--interval=".Length..], out intervalSeconds) || intervalSeconds < 1)
            {
              Console.Error.WriteLine("Invalid --interval value. Use a positive integer number of seconds.");
              return 1;
            }

            continue;
          }

          if (argument == "--interval")
          {
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out intervalSeconds) || intervalSeconds < 1)
            {
              Console.Error.WriteLine("Invalid --interval value. Use a positive integer number of seconds.");
              return 1;
            }

            index++;
            continue;
          }

          if (argument.StartsWith("--udid=", StringComparison.Ordinal))
          {
            ptpUdid = argument["--udid=".Length..];
            continue;
          }

          if (ptpUdid is null)
          {
            ptpUdid = argument;
            continue;
          }

          WriteUsage();
          return 1;
        }

        return await RunPtpDiagnosticAsync(ptpUdid, continuous, TimeSpan.FromSeconds(intervalSeconds));
      }

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

static async Task<int> RunPtpDiagnosticAsync(string? udid, bool continuous, TimeSpan interval)
{
  if (!continuous)
  {
    bool success = RunPtpDiagnosticOnce(udid, out string resultLine);
    Console.WriteLine(resultLine);
    return success ? 0 : 1;
  }

  using CancellationTokenSource stop = new();
  int totalRuns = 0;
  int passedRuns = 0;
  int failedRuns = 0;

  void StopLoop(object? _, ConsoleCancelEventArgs eventArgs)
  {
    eventArgs.Cancel = true;
    stop.Cancel();
  }

  Console.CancelKeyPress += StopLoop;
  Console.WriteLine($"Running continuous PTP diagnostic every {interval.TotalSeconds:0} second(s). Press Ctrl+C to stop.");

  try
  {
    while (!stop.IsCancellationRequested)
    {
      totalRuns++;
      bool success = RunPtpDiagnosticOnce(udid, out string resultLine);
      if (success)
      {
        passedRuns++;
      }
      else
      {
        failedRuns++;
      }

      Console.WriteLine(resultLine);

      try
      {
        await Task.Delay(interval, stop.Token);
      }
      catch (OperationCanceledException)
      {
        break;
      }
    }
  }
  finally
  {
    Console.CancelKeyPress -= StopLoop;
  }

  Console.WriteLine($"PTP diagnostic summary: {passedRuns}/{totalRuns} passed, {failedRuns}/{totalRuns} failed.");
  return passedRuns > 0 ? 0 : 1;
}

static bool RunPtpDiagnosticOnce(string? udid, out string resultLine)
{
  Stopwatch stopwatch = Stopwatch.StartNew();

  try
  {
    using PtpSession session = PtpSession.Connect(udid);
    session.OpenSession();

    uint[] storageIds = session.GetStorageIds();
    int totalObjects = 0;
    uint? sampleHandle = null;

    foreach (uint storageId in storageIds)
    {
      uint[] handles = session.GetObjectHandles(storageId);
      totalObjects += handles.Length;

      if (!sampleHandle.HasValue && handles.Length > 0)
      {
        sampleHandle = handles[0];
      }
    }

    if (sampleHandle.HasValue)
    {
      _ = session.GetObjectInfo(sampleHandle.Value);
    }

    stopwatch.Stop();
    resultLine = $"[{DateTimeOffset.Now:HH:mm:ss}] PASS PTP connected. Storages={storageIds.Length}, Objects={totalObjects}, Duration={stopwatch.ElapsedMilliseconds}ms.";
    return true;
  }
  catch (Exception ex)
  {
    stopwatch.Stop();
    bool messageIncludesNativeCode = ex.Message.Contains("Native error code:", StringComparison.Ordinal);
    string nativeErrorSuffix = !messageIncludesNativeCode && TryGetNativeErrorCode(ex, out int errorCode)
      ? $" Native error code: {errorCode}."
      : string.Empty;
    resultLine = $"[{DateTimeOffset.Now:HH:mm:ss}] FAIL PTP diagnostic after {stopwatch.ElapsedMilliseconds}ms. {ex.Message}{nativeErrorSuffix}";
    return false;
  }
}

  static PtpStatusResponse GetPtpStatus(string? udid)
  {
    Stopwatch stopwatch = Stopwatch.StartNew();

    try
    {
      using PtpSession session = PtpSession.Connect(udid);
      session.OpenSession();
      int storageCount = session.GetStorageIds().Length;

      stopwatch.Stop();
      return new PtpStatusResponse(
        true,
        $"PTP available ({storageCount} storage(s) detected).",
        null,
        null,
        stopwatch.ElapsedMilliseconds
      );
    }
    catch (Exception ex)
    {
      stopwatch.Stop();
      int? nativeErrorCode = TryGetNativeErrorCode(ex, out int parsedCode) ? parsedCode : null;
      return new PtpStatusResponse(
        false,
        "PTP unavailable for this session.",
        nativeErrorCode,
        ex.Message,
        stopwatch.ElapsedMilliseconds
      );
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

    _ = MediaIndexStore.TriggerRefreshAsync(
        udid,
        includeAdditionalRoots: false,
        () => EnumerateMediaAssetsHybrid(includeAdditionalRoots: false, udid)
    );
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
          bool forceRefresh = IsTruthy(context.Request.QueryString["forceRefresh"]);
          MediaEnumerationResult media = forceRefresh
            ? await MediaIndexStore.ForceRefreshAsync(
              udid,
              includeAdditionalRoots,
              () => EnumerateMediaAssetsHybrid(includeAdditionalRoots, udid)
            )
            : await MediaIndexStore.GetOrBuildAsync(
              udid,
              includeAdditionalRoots,
              () => EnumerateMediaAssetsHybrid(includeAdditionalRoots, udid)
            );
            IReadOnlyList<MediaAsset> assets = media.Assets;

            MediaLibraryResponse payload = new(
                assets.Select(MediaAssetView.FromAsset).ToArray(),
                assets.Count(x => x.Kind == MediaKind.Photo),
                assets.Count(x => x.Kind == MediaKind.LivePhoto),
                assets.Count(x => x.Kind == MediaKind.Video),
                media.ScannedRoots,
                media.Backend,
                media.BackendNote
            );

            await WriteJsonResponseAsync(context.Response, payload);
            return;
        }

        if (path == "/api/fs" && context.Request.HttpMethod == "GET")
        {
            string requestedPath = context.Request.QueryString["path"] ?? "DCIM";
            string normalizedPath = NormalizeRemotePath(requestedPath);

            using AfcSession session = AfcSession.Connect(udid);
            RemoteEntryInfo targetInfo = GetRemoteEntryInfo(session.AfcClient, normalizedPath, udid);
            if (!targetInfo.IsDirectory)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("The selected remote path is not a folder."));
                return;
            }

            IReadOnlyList<string> directoryEntries = ReadRemoteDirectoryEntries(session.AfcClient, normalizedPath, udid);
            ConcurrentBag<RemoteFsEntry> loadedEntries = [];
            int metadataParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
            await Parallel.ForEachAsync(
                directoryEntries,
                new ParallelOptions { MaxDegreeOfParallelism = metadataParallelism },
                (entry, _) =>
                {
                    using AfcSession workerSession = AfcSession.Connect(udid);
                    string childPath = CombineRemotePath(normalizedPath, entry);
                    RemoteEntryInfo info = GetRemoteEntryInfo(workerSession.AfcClient, childPath, udid);
                    loadedEntries.Add(new RemoteFsEntry(entry, childPath, info.IsDirectory, info.IsFile, info.SizeBytes));
                    return ValueTask.CompletedTask;
                }
            );

            RemoteFsEntry[] entries = loadedEntries
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await WriteJsonResponseAsync(
                context.Response,
                new RemoteFsListingResponse(
                    normalizedPath,
                    GetParentRemotePath(normalizedPath),
                    entries
                )
            );

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
            await StreamRemoteFileToHttpResponseAsync(session.AfcClient, remotePath, context.Request, context.Response, udid);
            return;
        }

          if (path == "/api/thumb" && context.Request.HttpMethod == "GET")
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
            await StreamThumbnailToHttpResponseAsync(session.AfcClient, remotePath, context.Response, udid);
            return;
          }

        if (path == "/api/progress" && context.Request.HttpMethod == "GET")
        {
            string? operationId = context.Request.QueryString["operationId"];
            if (string.IsNullOrWhiteSpace(operationId))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("A transfer operation id is required."));
                return;
            }

            TransferProgressResponse? snapshot = TransferProgressStore.GetSnapshot(operationId);
            if (snapshot is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("The transfer operation was not found."));
                return;
            }

            await WriteJsonResponseAsync(context.Response, snapshot);
            return;
        }

        if (path == "/api/fs/transfer" && context.Request.HttpMethod == "POST")
        {
            FsTransferRequest? request = await JsonSerializer.DeserializeAsync<FsTransferRequest>(context.Request.InputStream, AppJson.Options);
            if (request is null || request.SelectedPaths is null || request.SelectedPaths.Length == 0)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("Select at least one file or folder first."));
                return;
            }

            string operation = request.Operation?.Trim().ToLowerInvariant() ?? string.Empty;
            bool deleteOnly = operation == "delete";
            bool move = operation == "move";
            bool copy = operation == "copy";

            if (!deleteOnly && !move && !copy)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("Operation must be copy, move, or delete."));
                return;
            }

            if (!deleteOnly && (string.IsNullOrWhiteSpace(request.DestinationDirectory) || !Path.IsPathRooted(request.DestinationDirectory)))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("Enter an absolute destination directory path."));
                return;
            }

            string[] selectedPaths = NormalizeAndPruneSelectedPaths(request.SelectedPaths);
            if (selectedPaths.Any(x => x == "/"))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonResponseAsync(context.Response, new ErrorResponse("Selecting the remote root folder is not supported."));
                return;
            }

            using AfcSession session = AfcSession.Connect(udid);
            List<FailedTransferItem> failures = [];
            TransferProgressTracker? progressTracker = null;

            if (deleteOnly)
            {
                List<TransferCopyWorkItem> deleteWorkItems = selectedPaths
                    .Select(selectedPath => new TransferCopyWorkItem(
                        Guid.NewGuid().ToString("N"),
                        selectedPath,
                        string.Empty,
                        Path.GetFileName(selectedPath.TrimEnd('/')) is { Length: > 0 } name ? name : selectedPath
                    ))
                    .ToList();
                progressTracker = TransferProgressStore.Create(request.OperationId, "Deleting file system items", deleteWorkItems);

                foreach (string selectedPath in selectedPaths)
                {
                    TransferCopyWorkItem? matchingWorkItem = deleteWorkItems.FirstOrDefault(x => x.RemoteFilePath == selectedPath);
                    try
                    {
                        if (matchingWorkItem is not null)
                        {
                            progressTracker.MarkStarted(matchingWorkItem.ItemId);
                        }
                        DeleteRemotePathRecursive(session.AfcClient, selectedPath, udid);
                        AfcMetadataCache.InvalidatePath(udid, selectedPath);
                        if (matchingWorkItem is not null)
                        {
                            progressTracker.MarkProgress(matchingWorkItem.ItemId, 1, 1);
                            progressTracker.MarkSucceeded(matchingWorkItem.ItemId);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (matchingWorkItem is not null)
                        {
                            progressTracker.MarkFailed(matchingWorkItem.ItemId, ex.Message);
                        }
                        failures.Add(new FailedTransferItem(selectedPath, null, ex.Message));
                    }
                }
                progressTracker.MarkCompleted();
                MediaIndexStore.MarkDirty(udid);

                AddToQueue(failures.Select(f => new QueuedTransferItem(
                    Guid.NewGuid().ToString(), f.RemoteFilePath, null, "delete", null, f.ErrorMessage, DateTimeOffset.UtcNow
                )));

                await WriteJsonResponseAsync(
                    context.Response,
                    new TransferResponse("Deleted the selected items.", Array.Empty<string>(), failures.ToArray())
                );
                return;
            }

            // Enumerate all files first, then copy in parallel
            List<(TransferCopyWorkItem WorkItem, string SelectedPath)> filesToCopy = [];
            foreach (string selectedPath in selectedPaths)
            {
                string selectionPath = NormalizeRemotePath(selectedPath);
                RemoteEntryInfo info = GetRemoteEntryInfo(session.AfcClient, selectionPath, udid);
                IReadOnlyList<string> filePaths = EnumerateRemoteFilesForSelection(session.AfcClient, selectionPath, info, udid);

                foreach (string remoteFilePath in filePaths)
                {
                    string localPath = BuildLocalOutputPathForSelection(request.DestinationDirectory!, selectionPath, remoteFilePath);
                    filesToCopy.Add((
                        new TransferCopyWorkItem(
                            Guid.NewGuid().ToString("N"),
                            remoteFilePath,
                            localPath,
                            Path.GetFileName(remoteFilePath) is { Length: > 0 } name ? name : remoteFilePath
                        ),
                        selectionPath
                    ));
                }
            }
            progressTracker = TransferProgressStore.Create(
                request.OperationId,
                move ? "Moving file system items" : "Copying file system items",
                filesToCopy.Select(x => x.WorkItem)
            );

            (List<(string ItemId, string RemoteFilePath, string LocalPath, byte[] SourceHash)> successes, List<FailedTransferItem> copyFailures) =
                await ExecuteParallelCopiesAsync(udid, filesToCopy.Select(x => x.WorkItem), request.Parallelism, progressTracker);

            failures.AddRange(copyFailures);

            if (move && successes.Count > 0)
            {
                HashSet<string> failedRemotePaths = new(StringComparer.Ordinal);

                foreach ((string itemId, string remotePath, string localPath, byte[] sourceHash) in successes)
                {
                    try
                    {
                        byte[] localHash = ComputeLocalFileHash(localPath);
                        if (!CryptographicOperations.FixedTimeEquals(sourceHash, localHash))
                        {
                            failures.Add(new FailedTransferItem(remotePath, localPath, "SHA-256 mismatch. Remote source file was not deleted."));
                            failedRemotePaths.Add(remotePath);
                            progressTracker.MarkFailed(itemId, "SHA-256 mismatch. Remote source file was not deleted.");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new FailedTransferItem(remotePath, localPath, $"Hash verification failed: {ex.Message}"));
                        failedRemotePaths.Add(remotePath);
                        progressTracker.MarkFailed(itemId, $"Hash verification failed: {ex.Message}");
                    }
                }

                // Only delete selections where all files verified successfully
                foreach (string selectedPath in selectedPaths)
                {
                    bool allSucceeded = filesToCopy
                        .Where(x => x.SelectedPath == selectedPath)
                        .All(x => !failedRemotePaths.Contains(x.WorkItem.RemoteFilePath));

                    if (allSucceeded)
                    {
                        try
                        {
                            DeleteRemotePathRecursive(session.AfcClient, selectedPath, udid);
                            AfcMetadataCache.InvalidatePath(udid, selectedPath);
                        }
                        catch (Exception ex)
                        {
                            failures.Add(new FailedTransferItem(selectedPath, null, $"Delete after copy failed: {ex.Message}"));
                        }
                    }
                }
                MediaIndexStore.MarkDirty(udid);
            }
            progressTracker.MarkCompleted();

            AddToQueue(failures.Select(f => new QueuedTransferItem(
                Guid.NewGuid().ToString(), f.RemoteFilePath, f.LocalPath, operation, request.DestinationDirectory, f.ErrorMessage, DateTimeOffset.UtcNow
            )));

            string[] writtenPaths = successes.Select(x => x.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            await WriteJsonResponseAsync(
                context.Response,
                new TransferResponse(
                    move ? "Moved the selected items." : "Copied the selected items.",
                    writtenPaths,
                    failures.ToArray()
                )
            );

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
            IReadOnlyList<MediaAsset> assets = (await MediaIndexStore.GetOrBuildAsync(
                udid,
                request.IncludeAdditionalRoots,
                () => EnumerateMediaAssetsHybrid(request.IncludeAdditionalRoots, udid)
            )).Assets;
            Dictionary<string, MediaAsset> assetsById = assets.ToDictionary(x => x.Id, StringComparer.Ordinal);

            List<TransferCopyWorkItem> filesToCopy = [];
            List<MediaAsset> selectedAssets = [];
            foreach (string assetId in request.AssetIds.Distinct(StringComparer.Ordinal))
            {
                if (!assetsById.TryGetValue(assetId, out MediaAsset? asset))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonResponseAsync(context.Response, new ErrorResponse("One of the selected media items is no longer available on the device."));
                    return;
                }
                selectedAssets.Add(asset);

                DateTimeOffset? captureDateTime = null;
                if (string.Equals(request.PhotoNaming, "datetime", StringComparison.OrdinalIgnoreCase))
                {
                  try
                  {
                    captureDateTime = GetRemoteEntryInfo(session.AfcClient, asset.PrimaryRemotePath, udid).CaptureDateTime;
                  }
                  catch
                  {
                    captureDateTime = null;
                  }
                }

                foreach (string remotePath in asset.RemotePaths)
                {
                  string outputName = BuildOutputFileName(remotePath, request.PhotoNaming, captureDateTime);
                  string localPath = BuildLocalOutputPath(request.DestinationDirectory, remotePath, outputName);
                    filesToCopy.Add(new TransferCopyWorkItem(
                        Guid.NewGuid().ToString("N"),
                        remotePath,
                        localPath,
                        Path.GetFileName(remotePath) is { Length: > 0 } name ? name : remotePath
                    ));
                }
            }
            TransferProgressTracker progressTracker = TransferProgressStore.Create(
                request.OperationId,
                deleteAfterCopy ? "Moving media items" : "Copying media items",
                filesToCopy
            );

            (List<(string ItemId, string RemoteFilePath, string LocalPath, byte[] SourceHash)> successes, List<FailedTransferItem> failures) =
                await ExecuteParallelCopiesAsync(udid, filesToCopy, request.Parallelism, progressTracker);

            if (deleteAfterCopy && successes.Count > 0)
            {
                HashSet<string> failedRemotePaths = new(StringComparer.Ordinal);
                HashSet<string> verifiedRemotePaths = new(StringComparer.Ordinal);
                foreach ((string itemId, string remotePath, string localPath, byte[] sourceHash) in successes)
                {
                    try
                    {
                        byte[] localHash = ComputeLocalFileHash(localPath);
                        if (!CryptographicOperations.FixedTimeEquals(sourceHash, localHash))
                        {
                            failures.Add(new FailedTransferItem(remotePath, localPath, "SHA-256 mismatch. Remote source file was not deleted."));
                            failedRemotePaths.Add(remotePath);
                            progressTracker.MarkFailed(itemId, "SHA-256 mismatch. Remote source file was not deleted.");
                            continue;
                        }

                        verifiedRemotePaths.Add(remotePath);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new FailedTransferItem(remotePath, localPath, $"Hash verification failed: {ex.Message}"));
                        failedRemotePaths.Add(remotePath);
                        progressTracker.MarkFailed(itemId, $"Hash verification failed: {ex.Message}");
                    }
                }

                using PtpSession? ptpSession = selectedAssets.Any(x => x.PtpObjectHandlesByPath.Count > 0) ? PtpSession.TryConnect(udid) : null;
                foreach (MediaAsset asset in selectedAssets)
                {
                    if (asset.RemotePaths.Any(path => !verifiedRemotePaths.Contains(path)))
                    {
                        continue;
                    }

                    try
                    {
                        DeleteMediaAssetSources(asset, session.AfcClient, ptpSession, udid);
                        foreach (string remotePath in asset.RemotePaths)
                        {
                            AfcMetadataCache.InvalidatePath(udid, remotePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (string remotePath in asset.RemotePaths)
                        {
                            failures.Add(new FailedTransferItem(remotePath, null, $"Delete after copy failed: {ex.Message}"));
                        }

                        foreach (string failedPath in asset.RemotePaths)
                        {
                            failedRemotePaths.Add(failedPath);
                        }
                    }
                }
                MediaIndexStore.MarkDirty(udid);
            }
            progressTracker.MarkCompleted();

            IEnumerable<FailedTransferItem> queueableFailures = failures.Where(f => !(deleteAfterCopy && f.LocalPath is null));
            AddToQueue(queueableFailures.Select(f => new QueuedTransferItem(
                Guid.NewGuid().ToString(), f.RemoteFilePath, f.LocalPath, request.Operation, request.DestinationDirectory, f.ErrorMessage, DateTimeOffset.UtcNow
            )));

            string[] copiedPaths = successes.Select(x => x.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            await WriteJsonResponseAsync(
                context.Response,
                new TransferResponse(
                    deleteAfterCopy ? "Moved the selected items." : "Copied the selected items.",
                    copiedPaths,
                    failures.ToArray()
                )
            );

            return;
        }

        if (path == "/api/queue" && context.Request.HttpMethod == "GET")
        {
            List<QueuedTransferItem> queue;
            lock (AppJson.QueueFileLock)
            {
                queue = LoadQueue();
            }
            await WriteJsonResponseAsync(context.Response, queue);
            return;
        }

        if (path == "/api/queue" && context.Request.HttpMethod == "DELETE")
        {
            string? id = context.Request.QueryString["id"];
            lock (AppJson.QueueFileLock)
            {
                List<QueuedTransferItem> queue = LoadQueue();
                if (!string.IsNullOrEmpty(id))
                    queue.RemoveAll(x => x.Id == id);
                else
                    queue.Clear();
                SaveQueue(queue);
            }
            await WriteJsonResponseAsync(context.Response, new { message = "Queue updated." });
            return;
        }

        if (path == "/api/queue/retry" && context.Request.HttpMethod == "POST")
        {
            RetryQueueRequest? retryRequest = await JsonSerializer.DeserializeAsync<RetryQueueRequest>(context.Request.InputStream, AppJson.Options);

            List<QueuedTransferItem> itemsToRetry;
            lock (AppJson.QueueFileLock)
            {
                List<QueuedTransferItem> queue = LoadQueue();
                itemsToRetry = retryRequest?.Ids is { Length: > 0 }
                    ? queue.Where(x => retryRequest.Ids.Contains(x.Id)).ToList()
                    : queue.ToList();
            }

            List<QueuedTransferItem> stillFailed = [];
            List<string> retriedPaths = [];

            foreach (QueuedTransferItem item in itemsToRetry)
            {
                try
                {
                    using AfcSession taskSession = AfcSession.Connect(udid);

                    if (item.Operation == "delete")
                    {
                        DeleteRemotePathRecursive(taskSession.AfcClient, item.RemoteFilePath, udid);
                        AfcMetadataCache.InvalidatePath(udid, item.RemoteFilePath);
                        MediaIndexStore.MarkDirty(udid);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(item.DestinationDirectory))
                            throw new InvalidOperationException("Destination directory is not set for this queued item.");

                        string localPath = item.LocalPath ?? BuildLocalOutputPath(item.DestinationDirectory, item.RemoteFilePath);
                        byte[] sourceHash = CopyRemoteFileToLocalAndComputeHash(taskSession.AfcClient, item.RemoteFilePath, localPath);

                        if (item.Operation == "move")
                        {
                            byte[] localHash = ComputeLocalFileHash(localPath);
                            if (!CryptographicOperations.FixedTimeEquals(sourceHash, localHash))
                                throw new InvalidOperationException("SHA-256 mismatch. Remote source file was not deleted.");
                            ThrowIfError(NativeMethods.afc_remove_path(taskSession.AfcClient, item.RemoteFilePath), $"Unable to delete remote file '{item.RemoteFilePath}'");
                            AfcMetadataCache.InvalidatePath(udid, item.RemoteFilePath);
                            MediaIndexStore.MarkDirty(udid);
                        }

                        retriedPaths.Add(localPath);
                    }
                }
                catch (Exception ex)
                {
                    stillFailed.Add(item with { ErrorMessage = ex.Message, QueuedAt = DateTimeOffset.UtcNow });
                }
            }

            lock (AppJson.QueueFileLock)
            {
                List<QueuedTransferItem> queue = LoadQueue();
                HashSet<string> retriedIds = new(itemsToRetry.Select(x => x.Id), StringComparer.Ordinal);
                queue.RemoveAll(x => retriedIds.Contains(x.Id));
                queue.AddRange(stillFailed);
                SaveQueue(queue);
            }

            await WriteJsonResponseAsync(context.Response, new TransferResponse(
                $"Retried {itemsToRetry.Count} item(s). {stillFailed.Count} still failed.",
                retriedPaths.ToArray(),
                stillFailed.Select(x => new FailedTransferItem(x.RemoteFilePath, x.LocalPath, x.ErrorMessage)).ToArray()
            ));
            return;
        }

        if (path == "/api/ptp-retry" && context.Request.HttpMethod == "POST")
        {
            MediaIndexStore.PtpFallbackState.Clear(udid);
            MediaIndexStore.MarkDirty(udid);
            await WriteJsonResponseAsync(context.Response, new { message = "PTP retry state cleared. The next scan will attempt PTP." });
            return;
        }

        if (path == "/api/ptp-status" && context.Request.HttpMethod == "GET")
        {
          await WriteJsonResponseAsync(context.Response, GetPtpStatus(udid));
          return;
        }

        if (path == "/api/cache/reset" && context.Request.HttpMethod == "POST")
        {
            MediaIndexStore.PtpFallbackState.Clear(udid);
            MediaIndexStore.MarkDirty(udid);
            AfcMetadataCache.InvalidateAll(udid);
            await WriteJsonResponseAsync(context.Response, new { message = "Cache reset. The next load will fetch fresh data." });
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
  response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
  response.Headers["Pragma"] = "no-cache";
  response.Headers["Expires"] = "0";
    response.ContentLength64 = buffer.Length;
    await response.OutputStream.WriteAsync(buffer);
}

static async Task WriteJsonResponseAsync(HttpListenerResponse response, object payload)
{
    byte[] buffer = JsonSerializer.SerializeToUtf8Bytes(payload, AppJson.Options);
    response.ContentType = "application/json; charset=utf-8";
  response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
  response.Headers["Pragma"] = "no-cache";
  response.Headers["Expires"] = "0";
    response.ContentLength64 = buffer.Length;
    await response.OutputStream.WriteAsync(buffer);
}

static async Task StreamRemoteFileToHttpResponseAsync(
  IntPtr afcClient,
  string remotePath,
  HttpListenerRequest request,
  HttpListenerResponse response,
  string? udid = null
)
{
  long? totalSizeBytes = GetRemoteEntryInfo(afcClient, remotePath, udid).SizeBytes;
    ulong afcFileHandle = 0;
    ThrowIfError(
        NativeMethods.afc_file_open(afcClient, remotePath, NativeMethods.AFC_FOPEN_RDONLY, ref afcFileHandle),
        $"Unable to open remote file '{remotePath}'"
    );

    response.ContentType = GetContentType(remotePath);
    response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    response.Headers["Pragma"] = "no-cache";
    response.Headers["Expires"] = "0";
  response.Headers["Accept-Ranges"] = "bytes";

  bool hasValidSize = totalSizeBytes.HasValue && totalSizeBytes.Value >= 0;
  long startOffset = 0;
  long endOffset = hasValidSize ? totalSizeBytes!.Value - 1 : -1;
  bool isRangeResponse = false;

  string? rangeHeader = request.Headers["Range"];
  if (!string.IsNullOrWhiteSpace(rangeHeader) && hasValidSize)
  {
    if (!TryParseSingleByteRange(rangeHeader, totalSizeBytes!.Value, out startOffset, out endOffset))
    {
      response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
      response.Headers["Content-Range"] = $"bytes */{totalSizeBytes.Value}";
      return;
    }

    isRangeResponse = true;
    response.StatusCode = (int)HttpStatusCode.PartialContent;
    response.Headers["Content-Range"] = $"bytes {startOffset}-{endOffset}/{totalSizeBytes.Value}";
    response.ContentLength64 = endOffset - startOffset + 1;

    ThrowIfError(
      NativeMethods.afc_file_seek(afcClient, afcFileHandle, startOffset, NativeMethods.AFC_SEEK_SET),
      "Unable to seek remote file for ranged response"
    );
  }
  else
  {
    response.StatusCode = (int)HttpStatusCode.OK;
    if (hasValidSize)
    {
      response.ContentLength64 = totalSizeBytes!.Value;
    }
    else
    {
      response.SendChunked = true;
    }
  }

    try
    {
        byte[] buffer = new byte[64 * 1024];
    long remainingBytes = isRangeResponse ? (endOffset - startOffset + 1) : long.MaxValue;

        while (true)
        {
            uint bytesRead = 0;
      uint requestedLength = remainingBytes == long.MaxValue
        ? (uint)buffer.Length
        : (uint)Math.Min(buffer.Length, remainingBytes);

      if (requestedLength == 0)
      {
        break;
      }

            ThrowIfError(
        NativeMethods.afc_file_read(afcClient, afcFileHandle, buffer, requestedLength, ref bytesRead),
                "Failed while reading remote file"
            );

            if (bytesRead == 0)
            {
                break;
            }

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, (int)bytesRead));

      if (remainingBytes != long.MaxValue)
      {
        remainingBytes -= bytesRead;
        if (remainingBytes <= 0)
        {
          break;
        }
      }
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

  static async Task StreamRemoteFileToHttpResponseSimpleAsync(
    IntPtr afcClient,
    string remotePath,
    HttpListenerResponse response,
    string? udid = null
  )
  {
    long? totalSizeBytes = GetRemoteEntryInfo(afcClient, remotePath, udid).SizeBytes;
    ulong afcFileHandle = 0;
    ThrowIfError(
      NativeMethods.afc_file_open(afcClient, remotePath, NativeMethods.AFC_FOPEN_RDONLY, ref afcFileHandle),
      $"Unable to open remote file '{remotePath}'"
    );

    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = GetContentType(remotePath);
    response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    response.Headers["Pragma"] = "no-cache";
    response.Headers["Expires"] = "0";
    if (totalSizeBytes.HasValue && totalSizeBytes.Value >= 0)
    {
      response.ContentLength64 = totalSizeBytes.Value;
    }
    else
    {
      response.SendChunked = true;
    }

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

  static async Task StreamThumbnailToHttpResponseAsync(IntPtr afcClient, string remotePath, HttpListenerResponse response, string? udid = null)
  {
    string extension = Path.GetExtension(remotePath).ToLowerInvariant();
    if (extension is not ".heic" and not ".heif")
    {
      await StreamRemoteFileToHttpResponseSimpleAsync(afcClient, remotePath, response, udid);
      return;
    }

    try
    {
      byte[] sourceBytes = ReadRemoteFileBytes(afcClient, remotePath);
      using MagickImage image = new(sourceBytes);
      image.AutoOrient();
      image.Resize(new MagickGeometry(640, 640));
      image.Strip();
      image.Format = MagickFormat.Jpeg;
      image.Quality = 80;
      byte[] thumbBytes = image.ToByteArray();

      response.StatusCode = (int)HttpStatusCode.OK;
      response.ContentType = "image/jpeg";
      response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
      response.Headers["Pragma"] = "no-cache";
      response.Headers["Expires"] = "0";
      response.ContentLength64 = thumbBytes.Length;
      await response.OutputStream.WriteAsync(thumbBytes);
    }
    catch
    {
      await StreamRemoteFileToHttpResponseSimpleAsync(afcClient, remotePath, response, udid);
    }
  }

  static byte[] ReadRemoteFileBytes(IntPtr afcClient, string remotePath)
  {
    ulong afcFileHandle = 0;
    ThrowIfError(
      NativeMethods.afc_file_open(afcClient, remotePath, NativeMethods.AFC_FOPEN_RDONLY, ref afcFileHandle),
      $"Unable to open remote file '{remotePath}'"
    );

    try
    {
      using MemoryStream memory = new();
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

        memory.Write(buffer, 0, (int)bytesRead);
      }

      return memory.ToArray();
    }
    finally
    {
      if (afcFileHandle != 0)
      {
        NativeMethods.afc_file_close(afcClient, afcFileHandle);
      }
    }
  }

  static bool TryParseSingleByteRange(string rangeHeader, long totalLength, out long start, out long end)
  {
    start = 0;
    end = 0;

    if (totalLength <= 0)
    {
      return false;
    }

    if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    string value = rangeHeader["bytes=".Length..].Trim();
    if (string.IsNullOrWhiteSpace(value) || value.Contains(','))
    {
      // Multi-range responses are not supported here.
      return false;
    }

    int dashIndex = value.IndexOf('-');
    if (dashIndex < 0)
    {
      return false;
    }

    string startText = value[..dashIndex].Trim();
    string endText = value[(dashIndex + 1)..].Trim();

    if (startText.Length == 0)
    {
      // Suffix range: bytes=-N
      if (!long.TryParse(endText, out long suffixLength) || suffixLength <= 0)
      {
        return false;
      }

      start = Math.Max(0, totalLength - suffixLength);
      end = totalLength - 1;
      return start <= end;
    }

    if (!long.TryParse(startText, out start) || start < 0 || start >= totalLength)
    {
      return false;
    }

    if (endText.Length == 0)
    {
      end = totalLength - 1;
      return true;
    }

    if (!long.TryParse(endText, out end) || end < start)
    {
      return false;
    }

    if (end >= totalLength)
    {
      end = totalLength - 1;
    }

    return start <= end;
  }

static void WriteUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  AppleFileConduitDemo [ui|--ui] [deviceUdid]");
  Console.WriteLine("  AppleFileConduitDemo [ptp-test|--ptp-test] [--continuous] [--interval <seconds>] [deviceUdid|--udid=<deviceUdid>]");
    Console.WriteLine("  AppleFileConduitDemo [list|--list] <remoteDirectoryPath> [deviceUdid]");
    Console.WriteLine("  AppleFileConduitDemo [copy|--copy] <remoteFilePath> <localOutputPath> [deviceUdid]");
    Console.WriteLine("  AppleFileConduitDemo [move|--move] <remoteFilePath> <localOutputPath> [deviceUdid]");
}

static List<QueuedTransferItem> LoadQueue()
{
    try
    {
        if (File.Exists(AppJson.QueueFilePath))
        {
            string json = File.ReadAllText(AppJson.QueueFilePath);
            return JsonSerializer.Deserialize<List<QueuedTransferItem>>(json, AppJson.Options) ?? [];
        }
    }
    catch { }
    return [];
}

static void SaveQueue(List<QueuedTransferItem> queue)
{
    try
    {
        File.WriteAllText(AppJson.QueueFilePath, JsonSerializer.Serialize(queue, AppJson.Options));
    }
    catch { }
}

static void AddToQueue(IEnumerable<QueuedTransferItem> items)
{
    List<QueuedTransferItem> toAdd = items.ToList();
    if (toAdd.Count == 0) return;
    lock (AppJson.QueueFileLock)
    {
        List<QueuedTransferItem> queue = LoadQueue();
        queue.AddRange(toAdd);
        SaveQueue(queue);
    }
}

static async Task<(List<(string ItemId, string RemoteFilePath, string LocalPath, byte[] SourceHash)> Successes, List<FailedTransferItem> Failures)> ExecuteParallelCopiesAsync(
    string? udid,
    IEnumerable<TransferCopyWorkItem> filesToCopy,
    int parallelism,
    TransferProgressTracker? progressTracker = null)
{
    List<(string ItemId, string RemoteFilePath, string LocalPath, byte[] SourceHash)> successes = [];
    List<FailedTransferItem> failures = [];
    object resultLock = new();
    int maxDegree = Math.Max(1, Math.Min(16, parallelism));

    await Parallel.ForEachAsync(
        filesToCopy,
        new ParallelOptions { MaxDegreeOfParallelism = maxDegree },
        (item, _) =>
        {
            try
            {
                using AfcSession taskSession = AfcSession.Connect(udid);
                progressTracker?.MarkStarted(item.ItemId);
                byte[] hash = CopyRemoteFileToLocalAndComputeHash(
                    taskSession.AfcClient,
                    item.RemoteFilePath,
                    item.LocalDestPath,
                    (copiedBytes, totalBytes) => progressTracker?.MarkProgress(item.ItemId, copiedBytes, totalBytes)
                );
                progressTracker?.MarkSucceeded(item.ItemId);
                lock (resultLock) { successes.Add((item.ItemId, item.RemoteFilePath, item.LocalDestPath, hash)); }
            }
            catch (Exception ex)
            {
                progressTracker?.MarkFailed(item.ItemId, ex.Message);
                lock (resultLock) { failures.Add(new FailedTransferItem(item.RemoteFilePath, item.LocalDestPath, ex.Message)); }
            }
            return ValueTask.CompletedTask;
        }
    );

    return (successes, failures);
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

static MediaEnumerationResult EnumerateMediaAssetsHybrid(bool includeAdditionalRoots, string? udid = null)
{
    if (MediaIndexStore.PtpFallbackState.ShouldBypassPtp(udid))
    {
        LogPtpFallbackDiagnostics(cachedBypass: true);
        using AfcSession afcSession = AfcSession.Connect(udid);
        return EnumerateMediaAssetsViaAfc(afcSession.AfcClient, includeAdditionalRoots, udid) with
        {
            BackendNote = BuildPtpUnavailableNote()
        };
    }

    try
    {
        using PtpSession ptpSession = PtpSession.Connect(udid);
        MediaIndexStore.PtpFallbackState.Clear(udid);
        List<PtpMediaObject> ptpObjects = EnumerateMediaObjectsViaPtp(ptpSession);
        MediaEnumerationResult ptpResult = BuildMediaEnumerationResult(
            ptpObjects.Select(x => new RemoteFileEntry(x.RemotePath)).ToList(),
            scannedRoots: ["DCIM"],
            backend: "ptp",
            ptpHandlesByPath: ptpObjects.ToDictionary(x => x.RemotePath, x => x.ObjectHandle, StringComparer.OrdinalIgnoreCase)
        );

        if (!includeAdditionalRoots)
        {
            return ptpResult;
        }

        try
        {
            using AfcSession afcSession = AfcSession.Connect(udid);
            MediaEnumerationResult afcWithAdditionalRoots = EnumerateMediaAssetsViaAfc(afcSession.AfcClient, includeAdditionalRoots: true, udid);
            List<MediaAsset> mergedAssets = ptpResult.Assets
                .Concat(afcWithAdditionalRoots.Assets.Where(
                    candidate => ptpResult.Assets.All(existing => !string.Equals(existing.PrimaryRemotePath, candidate.PrimaryRemotePath, StringComparison.OrdinalIgnoreCase))
                ))
                .ToList();
            return new MediaEnumerationResult(mergedAssets, afcWithAdditionalRoots.ScannedRoots, "hybrid");
        }
        catch
        {
            return ptpResult;
        }
    }
    catch (Exception ex)
    {
        if (IsPtpServiceUnavailable(ex))
        {
            MediaIndexStore.PtpFallbackState.MarkPtpUnavailable(udid);
        }

        string backendNote = BuildPtpFallbackNote(ex);
        LogPtpFallbackDiagnostics(ex);
        using AfcSession afcSession = AfcSession.Connect(udid);
        return EnumerateMediaAssetsViaAfc(afcSession.AfcClient, includeAdditionalRoots, udid) with
        {
            BackendNote = backendNote
        };
    }
}

static string BuildPtpFallbackNote(Exception ex)
{
    if (IsPtpServiceUnavailable(ex))
    {
        return BuildPtpUnavailableNote();
    }

    return "PTP media enumeration was unavailable, so media was loaded through AFC instead.";
}

static string BuildPtpFallbackLogMessage(Exception ex)
{
    if (IsPtpServiceUnavailable(ex))
    {
        return "PTP service is unavailable on this device; using AFC fallback for media enumeration.";
    }

    return $"PTP media enumeration failed, falling back to AFC. {ex.Message}";
}

static void LogPtpFallbackDiagnostics(Exception? ex = null, bool cachedBypass = false)
{
    Console.WriteLine(cachedBypass
        ? "PTP service is still in a short retry cooldown for this device; using AFC fallback without another PTP probe."
        : BuildPtpFallbackLogMessage(ex!));
    Console.WriteLine("Scan note: if your scan still shows files, this fallback is informational and not a fatal error.");
    if (ex is not null)
    {
        Console.WriteLine($"PTP detail: {ex.Message}");
        if (TryGetNativeErrorCode(ex, out int errorCode))
        {
            Console.WriteLine($"PTP native error code: {errorCode}");
        }
    }

    Console.WriteLine("Common causes:");
    Console.WriteLine(" - iPhone is not fully unlocked.");
    Console.WriteLine(" - This computer has not been fully trusted by the device.");
    Console.WriteLine(" - The USB connection or cable is unstable.");
    Console.WriteLine(" - usbmuxd or libimobiledevice is not healthy on this machine.");
    Console.WriteLine(" - This iPhone/iOS session is not offering the PTP service right now.");
    Console.WriteLine("Expected behavior: the app tries PTP first and falls back to AFC when PTP is unavailable.");
}

static string BuildPtpUnavailableNote() => "PTP is not available on this device, so media was loaded through AFC instead.";

static bool IsPtpServiceUnavailable(Exception ex) => TryGetNativeErrorCode(ex, out int errorCode) && errorCode == -27;

static MediaEnumerationResult EnumerateMediaAssetsViaAfc(IntPtr afcClient, bool includeAdditionalRoots, string? udid = null)
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
        try
        {
            EnumerateRemoteFiles(afcClient, root, files, udid);
            scannedRoots.Add(root);
        }
        catch (InvalidOperationException)
        {
        }
    }

    return BuildMediaEnumerationResult(files, scannedRoots.ToArray(), "afc");
}

static MediaEnumerationResult BuildMediaEnumerationResult(
    List<RemoteFileEntry> files,
    string[] scannedRoots,
    string backend,
    Dictionary<string, uint>? ptpHandlesByPath = null
)
{
    files = files.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();

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
                    new[] { image.Path, pairedVideo.Path },
                    BuildPtpHandleMap(new[] { image.Path, pairedVideo.Path }, ptpHandlesByPath),
                    backend
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
                new[] { image.Path },
                BuildPtpHandleMap(new[] { image.Path }, ptpHandlesByPath),
                backend
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
                new[] { video.Path },
                BuildPtpHandleMap(new[] { video.Path }, ptpHandlesByPath),
                backend
            )
        );
    }

    return new MediaEnumerationResult(
        assets.OrderByDescending(x => x.PrimaryRemotePath, StringComparer.OrdinalIgnoreCase).ToArray(),
        scannedRoots,
        backend
    );
}

static IReadOnlyDictionary<string, uint> BuildPtpHandleMap(IEnumerable<string> remotePaths, Dictionary<string, uint>? ptpHandlesByPath)
{
    if (ptpHandlesByPath is null)
    {
        return new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    }

    Dictionary<string, uint> result = new(StringComparer.OrdinalIgnoreCase);
    foreach (string remotePath in remotePaths)
    {
        if (ptpHandlesByPath.TryGetValue(remotePath, out uint handle))
        {
        result[remotePath] = handle;
        }
    }

    return result;
}

static List<PtpMediaObject> EnumerateMediaObjectsViaPtp(PtpSession session)
{
    List<PtpMediaObject> mediaObjects = [];
    session.OpenSession();
    uint[] storageIds = session.GetStorageIds();
    foreach (uint storageId in storageIds)
    {
        uint[] handles = session.GetObjectHandles(storageId);
        Dictionary<uint, PtpObjectInfo> infos = new(handles.Length);
        foreach (uint handle in handles)
        {
            infos[handle] = session.GetObjectInfo(handle);
        }

        foreach ((uint handle, PtpObjectInfo info) in infos)
        {
            if (info.IsAssociation || string.IsNullOrWhiteSpace(info.FileName))
            {
                continue;
            }

            string path = BuildPtpRemotePath(infos, handle);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            mediaObjects.Add(new PtpMediaObject(handle, path));
        }
    }

    return mediaObjects
        .Where(x => x.RemotePath.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(x => x.RemotePath, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string BuildPtpRemotePath(IReadOnlyDictionary<uint, PtpObjectInfo> infos, uint handle)
{
    if (!infos.TryGetValue(handle, out PtpObjectInfo? current))
    {
        return string.Empty;
    }

    List<string> segments = [];
    if (!string.IsNullOrWhiteSpace(current.FileName))
    {
        segments.Add(current.FileName);
    }

    uint parent = current.ParentObject;
    int guard = 0;
    while (parent != 0 && parent != 0xFFFFFFFF && guard++ < 1024)
    {
        if (!infos.TryGetValue(parent, out PtpObjectInfo? parentInfo))
        {
            break;
        }

        if (!string.IsNullOrWhiteSpace(parentInfo.FileName))
        {
            segments.Add(parentInfo.FileName);
        }

        parent = parentInfo.ParentObject;
    }

    segments.Reverse();
    if (segments.Count == 0)
    {
        return string.Empty;
    }

    if (!string.Equals(segments[0], "DCIM", StringComparison.OrdinalIgnoreCase))
    {
        segments.Insert(0, "DCIM");
    }

    return string.Join("/", segments.Select(x => x.Trim('/')));
}

static void DeleteMediaAssetSources(MediaAsset asset, IntPtr afcClient, PtpSession? ptpSession, string? udid = null)
{
    HashSet<uint> ptpHandles = asset.PtpObjectHandlesByPath.Values.ToHashSet();
    if (ptpHandles.Count > 0)
    {
        if (ptpSession is null)
        {
            throw new InvalidOperationException("PTP delete requested but PTP session is unavailable.");
        }

        ptpSession.OpenSession();
        foreach (uint handle in ptpHandles)
        {
            ptpSession.DeleteObject(handle);
        }

        return;
    }

    foreach (string remotePath in asset.RemotePaths.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        ThrowIfError(NativeMethods.afc_remove_path(afcClient, remotePath), $"Unable to delete remote file '{remotePath}'");
        AfcMetadataCache.InvalidatePath(udid, remotePath);
    }
}

static void EnumerateRemoteFiles(IntPtr afcClient, string remoteDirectoryPath, List<RemoteFileEntry> files, string? udid = null)
{
    foreach (string entry in ReadRemoteDirectoryEntries(afcClient, remoteDirectoryPath, udid))
    {
        string childPath = CombineRemotePath(remoteDirectoryPath, entry);
        RemoteEntryInfo info = GetRemoteEntryInfo(afcClient, childPath, udid);

        if (info.IsDirectory)
        {
            EnumerateRemoteFiles(afcClient, childPath, files, udid);
            continue;
        }

        if (info.IsFile)
        {
            files.Add(new RemoteFileEntry(childPath));
        }
    }
}

static IReadOnlyList<string> ReadRemoteDirectoryEntries(IntPtr afcClient, string remotePath, string? udid = null, bool forceRefresh = false)
{
    string normalizedPath = NormalizeRemotePath(remotePath);
    return AfcMetadataCache.GetDirectoryEntries(
        udid,
        normalizedPath,
        () => ReadRemoteDirectoryEntriesRaw(afcClient, normalizedPath),
        forceRefresh
    );
}

static IReadOnlyList<string> ReadRemoteDirectoryEntriesRaw(IntPtr afcClient, string normalizedPath)
{
    IntPtr directoryEntries = IntPtr.Zero;
    ThrowIfError(
        NativeMethods.afc_read_directory(afcClient, normalizedPath, ref directoryEntries),
        $"Unable to list remote directory '{normalizedPath}'"
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

static RemoteEntryInfo GetRemoteEntryInfo(IntPtr afcClient, string remotePath, string? udid = null, bool forceRefresh = false)
{
    string normalizedPath = NormalizeRemotePath(remotePath);
    return AfcMetadataCache.GetEntryInfo(
        udid,
        normalizedPath,
        () => GetRemoteEntryInfoRaw(afcClient, normalizedPath),
        forceRefresh
    );
}

static RemoteEntryInfo GetRemoteEntryInfoRaw(IntPtr afcClient, string normalizedPath)
{
    IntPtr fileInfoDictionary = IntPtr.Zero;
    ThrowIfError(
        NativeMethods.afc_get_file_info(afcClient, normalizedPath, ref fileInfoDictionary),
        $"Unable to inspect remote path '{normalizedPath}'"
    );

    try
    {
        Dictionary<string, string> values = ReadDictionary(fileInfoDictionary);
        values.TryGetValue("st_ifmt", out string? fileType);
        long? size = null;
        if (values.TryGetValue("st_size", out string? sizeValue) && long.TryParse(sizeValue, out long parsedSize))
        {
            size = parsedSize;
        }

        DateTimeOffset? captureDateTime =
          TryReadAfcTimestamp(values, "st_birthtime")
          ?? TryReadAfcTimestamp(values, "st_mtime")
          ?? TryReadAfcTimestamp(values, "st_mtimespec")
          ?? TryReadAfcTimestamp(values, "st_ctime");

        return new RemoteEntryInfo(
            string.Equals(fileType, "S_IFDIR", StringComparison.Ordinal),
            string.Equals(fileType, "S_IFREG", StringComparison.Ordinal),
          size,
          captureDateTime
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

static DateTimeOffset? TryReadAfcTimestamp(IReadOnlyDictionary<string, string> values, string key)
{
  if (!values.TryGetValue(key, out string? rawValue) || string.IsNullOrWhiteSpace(rawValue))
  {
    return null;
  }

  if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp))
  {
    return null;
  }

  try
  {
    if (timestamp > 10_000_000_000L)
    {
      return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
    }

    if (timestamp > 0)
    {
      return DateTimeOffset.FromUnixTimeSeconds(timestamp);
    }
  }
  catch
  {
    return null;
  }

  return null;
}

static string NormalizeRemotePath(string remotePath)
{
    if (string.IsNullOrWhiteSpace(remotePath))
    {
        return "/";
    }

    string normalized = remotePath.Replace('\\', '/').Trim();
    if (normalized == "/")
    {
        return "/";
    }

    return normalized.Trim('/');
}

static string[] NormalizeAndPruneSelectedPaths(IEnumerable<string> selectedPaths)
{
    List<string> normalized = selectedPaths
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(NormalizeRemotePath)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x.Length)
        .ToList();

    List<string> pruned = [];
    foreach (string candidate in normalized)
    {
        if (pruned.Any(existing => IsRemotePathOrChild(existing, candidate)))
        {
            continue;
        }

        pruned.Add(candidate);
    }

    return pruned.ToArray();
}

static bool IsRemotePathOrChild(string parentPath, string childPath)
{
    string normalizedParent = NormalizeRemotePath(parentPath);
    string normalizedChild = NormalizeRemotePath(childPath);
    if (string.Equals(normalizedParent, normalizedChild, StringComparison.Ordinal))
    {
        return true;
    }

    if (normalizedParent == "/")
    {
        return true;
    }

    return normalizedChild.StartsWith($"{normalizedParent}/", StringComparison.Ordinal);
}

static IReadOnlyList<string> EnumerateRemoteFilesForSelection(IntPtr afcClient, string selectedPath, RemoteEntryInfo info, string? udid = null)
{
    if (info.IsFile)
    {
        return new[] { NormalizeRemotePath(selectedPath) };
    }

    if (!info.IsDirectory)
    {
        throw new InvalidOperationException($"The selected path '{selectedPath}' is no longer available.");
    }

    List<RemoteFileEntry> files = [];
    EnumerateRemoteFiles(afcClient, NormalizeRemotePath(selectedPath), files, udid);
    return files.Select(x => x.Path).ToArray();
}

static string BuildLocalOutputPathForSelection(string destinationDirectory, string selectedPath, string remoteFilePath)
{
    string normalizedSelectedPath = NormalizeRemotePath(selectedPath);
    string normalizedRemoteFilePath = NormalizeRemotePath(remoteFilePath);
    string selectedName = GetRemotePathName(normalizedSelectedPath);

    if (string.IsNullOrEmpty(selectedName))
    {
        selectedName = "root";
    }

    string relativeFilePath;
    if (string.Equals(normalizedSelectedPath, normalizedRemoteFilePath, StringComparison.Ordinal))
    {
        relativeFilePath = selectedName;
    }
    else
    {
        string prefix = $"{normalizedSelectedPath.TrimEnd('/')}/";
        relativeFilePath = normalizedRemoteFilePath.StartsWith(prefix, StringComparison.Ordinal)
            ? $"{selectedName}/{normalizedRemoteFilePath[prefix.Length..]}"
            : $"{selectedName}/{Path.GetFileName(normalizedRemoteFilePath)}";
    }

    string[] segments = relativeFilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    return Path.Combine(destinationDirectory, Path.Combine(segments));
}

static string GetRemotePathName(string remotePath)
{
    string normalized = NormalizeRemotePath(remotePath).Trim('/');
    if (string.IsNullOrEmpty(normalized))
    {
        return string.Empty;
    }

    int separatorIndex = normalized.LastIndexOf('/');
    return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
}

static string? GetParentRemotePath(string remotePath)
{
    string normalized = NormalizeRemotePath(remotePath);
    if (normalized == "/")
    {
        return null;
    }

    string trimmed = normalized.Trim('/');
    int separatorIndex = trimmed.LastIndexOf('/');
    if (separatorIndex < 0)
    {
        return "/";
    }

    return trimmed[..separatorIndex];
}

static void DeleteRemotePathRecursive(IntPtr afcClient, string remotePath, string? udid = null)
{
    string normalizedPath = NormalizeRemotePath(remotePath);
    RemoteEntryInfo info = GetRemoteEntryInfo(afcClient, normalizedPath, udid);

    if (info.IsFile)
    {
        ThrowIfError(NativeMethods.afc_remove_path(afcClient, normalizedPath), $"Unable to delete remote file '{normalizedPath}'");
        return;
    }

    if (!info.IsDirectory)
    {
        throw new InvalidOperationException($"Unable to delete '{normalizedPath}' because it is not a file or directory.");
    }

    foreach (string entry in ReadRemoteDirectoryEntries(afcClient, normalizedPath, udid))
    {
        string childPath = CombineRemotePath(normalizedPath, entry);
        DeleteRemotePathRecursive(afcClient, childPath, udid);
    }

    ThrowIfError(NativeMethods.afc_remove_path(afcClient, normalizedPath), $"Unable to delete remote directory '{normalizedPath}'");
}

static string BuildLocalOutputPath(string destinationDirectory, string remotePath, string? overrideFileName = null)
{
    string relativePath = remotePath.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase)
        ? remotePath["DCIM/".Length..]
        : remotePath.TrimStart('/');

    string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
  if (!string.IsNullOrWhiteSpace(overrideFileName) && segments.Length > 0)
  {
    segments[^1] = overrideFileName;
  }

    return Path.Combine(destinationDirectory, Path.Combine(segments));
}

static string BuildOutputFileName(string remotePath, string? photoNaming, DateTimeOffset? captureDateTime)
{
  string originalFileName = Path.GetFileName(remotePath);
  if (string.IsNullOrWhiteSpace(originalFileName))
  {
    return remotePath;
  }

  if (!string.Equals(photoNaming, "datetime", StringComparison.OrdinalIgnoreCase) || captureDateTime is null)
  {
    return originalFileName;
  }

  string baseName = Path.GetFileNameWithoutExtension(originalFileName);
  string extension = Path.GetExtension(originalFileName);
  string timestamp = captureDateTime.Value.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
  return $"{timestamp}_{baseName}{extension}";
}

static byte[] CopyRemoteFileToLocalAndComputeHash(IntPtr afcClient, string remotePath, string localPath, Action<long, long?>? onProgress = null)
{
    long? totalBytes = null;
    try
    {
        totalBytes = GetRemoteEntryInfo(afcClient, remotePath).SizeBytes;
    }
    catch
    {
        totalBytes = null;
    }
    onProgress?.Invoke(0, totalBytes);

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
        long copiedBytes = 0;

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
            copiedBytes += bytesRead;
            onProgress?.Invoke(copiedBytes, totalBytes);
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

static bool TryGetNativeErrorCode(Exception ex, out int errorCode)
{
    const string marker = "Native error code:";
    string message = ex.Message;
    int markerIndex = message.LastIndexOf(marker, StringComparison.Ordinal);
    if (markerIndex >= 0)
    {
        string codeText = message[(markerIndex + marker.Length)..].Trim();
        if (int.TryParse(codeText, out errorCode))
        {
            return true;
        }
    }

    errorCode = 0;
    return false;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LockdownServiceDescriptorRaw
{
    public ushort Port;
    public byte SslEnabled;
}

internal sealed class PtpSession : IDisposable
{
  private static readonly string[] PtpServiceIdentifiers = ["com.apple.ptp", "com.apple.mobile.ptp"];

    private const ushort PtpContainerTypeCommand = 1;
    private const ushort PtpContainerTypeData = 2;
    private const ushort PtpContainerTypeResponse = 3;

    private const ushort PtpOperationOpenSession = 0x1002;
    private const ushort PtpOperationGetStorageIds = 0x1004;
    private const ushort PtpOperationGetObjectHandles = 0x1007;
    private const ushort PtpOperationGetObjectInfo = 0x1008;
    private const ushort PtpOperationDeleteObject = 0x100B;

    private const ushort PtpResponseOk = 0x2001;

    private IntPtr _connection;
    private uint _nextTransactionId = 1;
    private bool _sessionOpened;

    private PtpSession(IntPtr device, IntPtr lockdowndClient, IntPtr serviceDescriptor, IntPtr connection)
    {
        Device = device;
        LockdowndClient = lockdowndClient;
        ServiceDescriptor = serviceDescriptor;
        _connection = connection;
    }

    public IntPtr Device { get; }
    public IntPtr LockdowndClient { get; }
    public IntPtr ServiceDescriptor { get; }

    public static PtpSession Connect(string? udid)
    {
        IntPtr device = IntPtr.Zero;
        IntPtr lockdowndClient = IntPtr.Zero;
        IntPtr serviceDescriptor = IntPtr.Zero;
        IntPtr connection = IntPtr.Zero;

        try
        {
            NativeError.ThrowIfError(NativeMethods.idevice_new(ref device, udid), "Unable to connect to iOS device");
            NativeError.ThrowIfError(
                NativeMethods.lockdownd_client_new_with_handshake(device, ref lockdowndClient, "AppleFileConduitDemo"),
                "Unable to start lockdownd session"
            );

            int lastServiceError = 0;
            foreach (string serviceName in PtpServiceIdentifiers)
            {
                serviceDescriptor = IntPtr.Zero;
                int startError = NativeMethods.lockdownd_start_service(lockdowndClient, serviceName, ref serviceDescriptor);
                if (startError != 0)
                {
                    // Some iOS builds only allow service startup with an escrow bag.
                    serviceDescriptor = IntPtr.Zero;
                    startError = NativeMethods.lockdownd_start_service_with_escrow_bag(lockdowndClient, serviceName, ref serviceDescriptor);
                }

                if (startError == 0 && serviceDescriptor != IntPtr.Zero)
                {
                    break;
                }

                lastServiceError = startError;
                if (serviceDescriptor != IntPtr.Zero)
                {
                    NativeMethods.lockdownd_service_descriptor_free(serviceDescriptor);
                    serviceDescriptor = IntPtr.Zero;
                }
            }

            if (serviceDescriptor == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Unable to start PTP service. Tried: {string.Join(", ", PtpServiceIdentifiers)}. Native error code: {lastServiceError}"
                );
            }

            LockdownServiceDescriptorRaw descriptor = Marshal.PtrToStructure<LockdownServiceDescriptorRaw>(serviceDescriptor);
            if (descriptor.Port == 0)
            {
                throw new InvalidOperationException("PTP service returned an invalid port.");
            }

            NativeError.ThrowIfError(NativeMethods.idevice_connect(device, descriptor.Port, ref connection), "Unable to connect to PTP service");
            if (descriptor.SslEnabled != 0)
            {
                NativeError.ThrowIfError(NativeMethods.idevice_connection_enable_ssl(connection), "Unable to enable PTP SSL");
            }

            return new PtpSession(device, lockdowndClient, serviceDescriptor, connection);
        }
        catch
        {
            if (connection != IntPtr.Zero)
            {
                NativeMethods.idevice_disconnect(connection);
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

    public static PtpSession? TryConnect(string? udid)
    {
        try
        {
            return Connect(udid);
        }
        catch
        {
            return null;
        }
    }

    public void OpenSession()
    {
        if (_sessionOpened)
        {
            return;
        }

        (_, ushort response, _) = ExecuteOperation(PtpOperationOpenSession, 1);
        if (response != PtpResponseOk)
        {
            throw new InvalidOperationException($"PTP OpenSession failed with response 0x{response:X4}.");
        }

        _sessionOpened = true;
    }

    public uint[] GetStorageIds()
    {
        byte[] payload = ExecuteDataOperation(PtpOperationGetStorageIds);
        return ReadPtpUInt32Array(payload);
    }

    public uint[] GetObjectHandles(uint storageId)
    {
        byte[] payload = ExecuteDataOperation(PtpOperationGetObjectHandles, storageId, 0, 0xFFFFFFFF);
        return ReadPtpUInt32Array(payload);
    }

    public PtpObjectInfo GetObjectInfo(uint handle)
    {
        byte[] payload = ExecuteDataOperation(PtpOperationGetObjectInfo, handle);
        return ParseObjectInfo(payload);
    }

    public void DeleteObject(uint handle)
    {
        (_, ushort response, _) = ExecuteOperation(PtpOperationDeleteObject, handle, 0);
        if (response != PtpResponseOk)
        {
            throw new InvalidOperationException($"PTP DeleteObject({handle}) failed with response 0x{response:X4}.");
        }
    }

    public void Dispose()
    {
        if (_connection != IntPtr.Zero)
        {
            NativeMethods.idevice_connection_disable_ssl(_connection);
            NativeMethods.idevice_disconnect(_connection);
            _connection = IntPtr.Zero;
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

    private byte[] ExecuteDataOperation(ushort operationCode, params uint[] parameters)
    {
        (byte[]? payload, ushort response, _) = ExecuteOperation(operationCode, parameters);
        if (response != PtpResponseOk)
        {
            throw new InvalidOperationException($"PTP op 0x{operationCode:X4} failed with response 0x{response:X4}.");
        }

        return payload ?? Array.Empty<byte>();
    }

    private (byte[]? Payload, ushort ResponseCode, uint[] ResponseParameters) ExecuteOperation(ushort operationCode, params uint[] parameters)
    {
        uint transactionId = _nextTransactionId++;
        SendContainer(PtpContainerTypeCommand, operationCode, transactionId, parameters, payload: null);

        PtpContainer first = ReadContainer();
        if (first.Type == PtpContainerTypeResponse)
        {
            return (null, first.Code, first.Parameters);
        }

        if (first.Type != PtpContainerTypeData)
        {
            throw new InvalidOperationException($"Unexpected PTP container type {first.Type}.");
        }

        PtpContainer response = ReadContainer();
        if (response.Type != PtpContainerTypeResponse)
        {
            throw new InvalidOperationException("PTP response container was missing.");
        }

        return (first.Payload, response.Code, response.Parameters);
    }

    private void SendContainer(ushort type, ushort code, uint transactionId, uint[]? parameters, byte[]? payload)
    {
        int parameterCount = parameters?.Length ?? 0;
        int payloadLength = payload?.Length ?? 0;
        int totalLength = 12 + (parameterCount * 4) + payloadLength;
        byte[] buffer = new byte[totalLength];

        WriteUInt32(buffer, 0, (uint)totalLength);
        WriteUInt16(buffer, 4, type);
        WriteUInt16(buffer, 6, code);
        WriteUInt32(buffer, 8, transactionId);

        int offset = 12;
        if (parameters is not null)
        {
            foreach (uint parameter in parameters)
            {
                WriteUInt32(buffer, offset, parameter);
                offset += 4;
            }
        }

        if (payloadLength > 0)
        {
            Buffer.BlockCopy(payload!, 0, buffer, offset, payloadLength);
        }

        SendBytes(buffer);
    }

    private void SendBytes(byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            byte[] chunk = offset == 0 ? data : data[offset..];
            uint sent = 0;
            NativeError.ThrowIfError(NativeMethods.idevice_connection_send(_connection, chunk, (uint)chunk.Length, ref sent), "Unable to send PTP data");
            if (sent == 0)
            {
                throw new IOException("PTP send returned zero bytes.");
            }

            offset += (int)sent;
        }
    }

    private PtpContainer ReadContainer()
    {
        byte[] lengthBytes = ReceiveExact(4);
        uint length = ReadUInt32(lengthBytes, 0);
        if (length < 12)
        {
            throw new InvalidOperationException($"Invalid PTP container length: {length}.");
        }

        byte[] remainder = ReceiveExact((int)length - 4);
        ushort type = ReadUInt16(remainder, 0);
        ushort code = ReadUInt16(remainder, 2);
        uint transactionId = ReadUInt32(remainder, 4);
        byte[] body = remainder[8..];

        if (type == PtpContainerTypeResponse)
        {
            uint[] responseParameters = ReadPtpUInt32ArrayWithKnownCount(body, body.Length / 4);
            return new PtpContainer(type, code, transactionId, responseParameters, Array.Empty<byte>());
        }

        return new PtpContainer(type, code, transactionId, Array.Empty<uint>(), body);
    }

    private byte[] ReceiveExact(int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            byte[] target = offset == 0 ? buffer : new byte[length - offset];
            uint received = 0;
            NativeError.ThrowIfError(
                NativeMethods.idevice_connection_receive(_connection, target, (uint)target.Length, ref received),
                "Unable to read PTP data"
            );
            if (received == 0)
            {
                throw new IOException("PTP receive returned zero bytes.");
            }

            if (offset == 0)
            {
                offset += (int)received;
                continue;
            }

            Buffer.BlockCopy(target, 0, buffer, offset, (int)received);
            offset += (int)received;
        }

        return buffer;
    }

    private static uint[] ReadPtpUInt32Array(byte[] payload)
    {
        if (payload.Length < 4)
        {
            return [];
        }

        int count = (int)ReadUInt32(payload, 0);
        if (count <= 0)
        {
            return [];
        }

        return ReadPtpUInt32ArrayWithKnownCount(payload[4..], count);
    }

    private static uint[] ReadPtpUInt32ArrayWithKnownCount(byte[] payload, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * 4;
            if (offset + 4 > payload.Length)
            {
                break;
            }

            values[i] = ReadUInt32(payload, offset);
        }

        return values;
    }

    private static PtpObjectInfo ParseObjectInfo(byte[] payload)
    {
        if (payload.Length < 52)
        {
            throw new InvalidOperationException("PTP object info payload was incomplete.");
        }

        ushort objectFormat = ReadUInt16(payload, 4);
        uint parentObject = ReadUInt32(payload, 40);
        int offset = 52;
        string fileName = ReadPtpString(payload, ref offset);
        return new PtpObjectInfo(fileName, objectFormat == 0x3001, parentObject);
    }

    private static string ReadPtpString(byte[] payload, ref int offset)
    {
        if (offset >= payload.Length)
        {
            return string.Empty;
        }

        int count = payload[offset++];
        if (count <= 0)
        {
            return string.Empty;
        }

        int byteLength = count * 2;
        if (offset + byteLength > payload.Length)
        {
            byteLength = Math.Max(0, payload.Length - offset);
        }

        string value = Encoding.Unicode.GetString(payload, offset, byteLength);
        offset += byteLength;
        return value.TrimEnd('\0');
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) => (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    private readonly record struct PtpContainer(ushort Type, ushort Code, uint TransactionId, uint[] Parameters, byte[] Payload);
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

internal sealed record RemoteEntryInfo(bool IsDirectory, bool IsFile, long? SizeBytes, DateTimeOffset? CaptureDateTime);

internal sealed record MediaAsset(
    string Id,
    string Name,
    MediaKind Kind,
    string PrimaryRemotePath,
    IReadOnlyList<string> RemotePaths,
    IReadOnlyDictionary<string, uint> PtpObjectHandlesByPath,
    string SourceBackend
);

internal enum MediaKind
{
    Photo,
    LivePhoto,
    Video
}

internal sealed record MediaEnumerationResult(IReadOnlyList<MediaAsset> Assets, string[] ScannedRoots, string Backend, string? BackendNote = null);

internal sealed record PtpMediaObject(uint ObjectHandle, string RemotePath);

internal sealed record PtpObjectInfo(string FileName, bool IsAssociation, uint ParentObject);

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
      string previewUrl = extension is ".heic" or ".heif"
        ? $"/api/thumb?id={Uri.EscapeDataString(asset.Id)}"
        : $"/api/file?id={Uri.EscapeDataString(asset.Id)}";
        string previewMode = asset.Kind == MediaKind.Video
            ? "video"
        : extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".heif" or ".dng" or ".tif" or ".tiff"
          ? "image"
          : "placeholder";
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
            previewUrl,
            previewMode,
            asset.PrimaryRemotePath.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase)
                ? asset.PrimaryRemotePath["DCIM/".Length..]
                : asset.PrimaryRemotePath
        );
    }
}

internal sealed record MediaLibraryResponse(MediaAssetView[] Items, int PhotoCount, int LivePhotoCount, int VideoCount, string[] ScannedRoots, string Backend, string? BackendNote);

internal sealed record TransferRequest(string[] AssetIds, string DestinationDirectory, string Operation, bool IncludeAdditionalRoots, int Parallelism = 4, string? OperationId = null, string PhotoNaming = "datetime");

internal sealed record TransferResponse(string Message, string[] LocalPaths, FailedTransferItem[] FailedItems);

internal sealed record FsTransferRequest(string[] SelectedPaths, string? DestinationDirectory, string Operation, int Parallelism = 4, string? OperationId = null);

internal sealed record RemoteFsEntry(string Name, string Path, bool IsDirectory, bool IsFile, long? SizeBytes);

internal sealed record RemoteFsListingResponse(string CurrentPath, string? ParentPath, RemoteFsEntry[] Entries);

internal sealed record ErrorResponse(string Message);

internal sealed record PtpStatusResponse(bool IsAvailable, string Message, int? NativeErrorCode, string? Detail, long ProbeDurationMs);

internal sealed record FailedTransferItem(string RemoteFilePath, string? LocalPath, string ErrorMessage);

internal sealed record QueuedTransferItem(
    string Id,
    string RemoteFilePath,
    string? LocalPath,
    string Operation,
    string? DestinationDirectory,
    string ErrorMessage,
    DateTimeOffset QueuedAt
);

internal sealed record RetryQueueRequest(string[]? Ids);

internal sealed record TransferCopyWorkItem(string ItemId, string RemoteFilePath, string LocalDestPath, string DisplayName);

internal sealed record TransferProgressFileView(
    string ItemId,
    string Name,
    string RemotePath,
    string Status,
    long BytesCopied,
    long? TotalBytes,
    string? ErrorMessage
);

internal sealed record TransferProgressResponse(
    string OperationId,
    string Label,
    int TotalCount,
    int CompletedCount,
    bool IsComplete,
    TransferProgressFileView[] Items
);

internal sealed class TransferProgressTracker
{
    private readonly object gate = new();
    private readonly Dictionary<string, TransferProgressItemState> itemsById;
    private readonly string operationId;
    private readonly string label;
    private DateTimeOffset? completedAtUtc;

    public TransferProgressTracker(string operationId, string label, IEnumerable<TransferCopyWorkItem> items)
    {
        this.operationId = operationId;
        this.label = label;
        itemsById = items.ToDictionary(
            item => item.ItemId,
            item => new TransferProgressItemState(item.ItemId, item.DisplayName, item.RemoteFilePath),
            StringComparer.Ordinal
        );
    }

    public string OperationId => operationId;
    public DateTimeOffset? CompletedAtUtc
    {
        get { lock (gate) { return completedAtUtc; } }
    }

    public void MarkStarted(string itemId)
    {
        lock (gate)
        {
            if (!itemsById.TryGetValue(itemId, out TransferProgressItemState? item)) return;
            item.Status = "running";
        }
    }

    public void MarkProgress(string itemId, long copiedBytes, long? totalBytes)
    {
        lock (gate)
        {
            if (!itemsById.TryGetValue(itemId, out TransferProgressItemState? item)) return;
            item.Status = "running";
            item.BytesCopied = Math.Max(0, copiedBytes);
            if (totalBytes.HasValue && totalBytes.Value >= 0)
            {
                item.TotalBytes = totalBytes.Value;
            }
        }
    }

    public void MarkSucceeded(string itemId)
    {
        lock (gate)
        {
            if (!itemsById.TryGetValue(itemId, out TransferProgressItemState? item)) return;
            if (item.TotalBytes.HasValue && item.TotalBytes.Value >= item.BytesCopied)
            {
                item.BytesCopied = item.TotalBytes.Value;
            }
            item.Status = "succeeded";
        }
    }

    public void MarkFailed(string itemId, string errorMessage)
    {
        lock (gate)
        {
            if (!itemsById.TryGetValue(itemId, out TransferProgressItemState? item)) return;
            item.Status = "failed";
            item.ErrorMessage = errorMessage;
        }
    }

    public void MarkCompleted()
    {
        lock (gate)
        {
            if (completedAtUtc is null)
            {
                completedAtUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    public TransferProgressResponse Snapshot()
    {
        lock (gate)
        {
            TransferProgressFileView[] items = itemsById.Values
                .Select(item => new TransferProgressFileView(
                    item.ItemId,
                    item.Name,
                    item.RemotePath,
                    item.Status,
                    item.BytesCopied,
                    item.TotalBytes,
                    item.ErrorMessage
                ))
                .ToArray();
            int completedCount = items.Count(item => item.Status is "succeeded" or "failed");
            bool done = completedAtUtc.HasValue && completedCount >= items.Length;
            return new TransferProgressResponse(
                operationId,
                label,
                items.Length,
                completedCount,
                done,
                items
            );
        }
    }

    private sealed class TransferProgressItemState
    {
        public TransferProgressItemState(string itemId, string name, string remotePath)
        {
            ItemId = itemId;
            Name = name;
            RemotePath = remotePath;
            Status = "pending";
            BytesCopied = 0;
        }

        public string ItemId { get; }
        public string Name { get; }
        public string RemotePath { get; }
        public string Status { get; set; }
        public long BytesCopied { get; set; }
        public long? TotalBytes { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

internal static class TransferProgressStore
{
    private static readonly ConcurrentDictionary<string, TransferProgressTracker> Operations = new(StringComparer.Ordinal);
    private const int RetentionMinutes = 10;

    public static TransferProgressTracker Create(string? requestedOperationId, string label, IEnumerable<TransferCopyWorkItem> items)
    {
        CleanupExpired();
        string operationId = string.IsNullOrWhiteSpace(requestedOperationId)
            ? Guid.NewGuid().ToString("N")
            : requestedOperationId.Trim();
        TransferProgressTracker tracker = new(operationId, label, items);
        Operations[operationId] = tracker;
        return tracker;
    }

    public static TransferProgressResponse? GetSnapshot(string operationId)
    {
        CleanupExpired();
        if (!Operations.TryGetValue(operationId, out TransferProgressTracker? tracker))
        {
            return null;
        }

        return tracker.Snapshot();
    }

    private static void CleanupExpired()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-RetentionMinutes);
        foreach ((string key, TransferProgressTracker value) in Operations)
        {
            if (value.CompletedAtUtc is { } completed && completed < cutoff)
            {
                Operations.TryRemove(key, out _);
            }
        }
    }
}

internal static class AfcMetadataCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<string>>> DirectoryEntries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CacheEntry<RemoteEntryInfo>> EntryInfos = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<string> GetDirectoryEntries(string? udid, string normalizedPath, Func<IReadOnlyList<string>> factory, bool forceRefresh = false)
    {
        string cacheKey = BuildCacheKey(udid, normalizedPath);
        if (!forceRefresh && DirectoryEntries.TryGetValue(cacheKey, out CacheEntry<IReadOnlyList<string>>? cached) && !cached.IsExpired(Ttl))
        {
            return cached.Value;
        }

        IReadOnlyList<string> fresh = factory();
        DirectoryEntries[cacheKey] = new CacheEntry<IReadOnlyList<string>>(fresh);
        return fresh;
    }

    public static RemoteEntryInfo GetEntryInfo(string? udid, string normalizedPath, Func<RemoteEntryInfo> factory, bool forceRefresh = false)
    {
        string cacheKey = BuildCacheKey(udid, normalizedPath);
        if (!forceRefresh && EntryInfos.TryGetValue(cacheKey, out CacheEntry<RemoteEntryInfo>? cached) && !cached.IsExpired(Ttl))
        {
            return cached.Value;
        }

        RemoteEntryInfo fresh = factory();
        EntryInfos[cacheKey] = new CacheEntry<RemoteEntryInfo>(fresh);
        return fresh;
    }

    public static void InvalidatePath(string? udid, string remotePath)
    {
        string normalizedPath = NormalizePath(remotePath);
        string cachePrefix = $"{GetDeviceKey(udid)}|";
        string pathPrefix = normalizedPath == "/" ? "/" : $"{normalizedPath.TrimEnd('/')}/";

        foreach ((string key, _) in EntryInfos)
        {
            if (!key.StartsWith(cachePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string keyPath = key[cachePrefix.Length..];
            if (string.Equals(keyPath, normalizedPath, StringComparison.Ordinal) ||
                keyPath.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                EntryInfos.TryRemove(key, out _);
            }
        }

        foreach ((string key, _) in DirectoryEntries)
        {
            if (!key.StartsWith(cachePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string keyPath = key[cachePrefix.Length..];
            if (string.Equals(keyPath, normalizedPath, StringComparison.Ordinal) ||
                keyPath.StartsWith(pathPrefix, StringComparison.Ordinal) ||
                string.Equals(GetParentPath(keyPath) ?? "/", normalizedPath, StringComparison.Ordinal))
            {
                DirectoryEntries.TryRemove(key, out _);
            }
        }
    }

    public static void InvalidateAll(string? udid)
    {
        string cachePrefix = $"{GetDeviceKey(udid)}|";
        foreach ((string key, _) in EntryInfos)
        {
            if (key.StartsWith(cachePrefix, StringComparison.Ordinal))
            {
                EntryInfos.TryRemove(key, out _);
            }
        }

        foreach ((string key, _) in DirectoryEntries)
        {
            if (key.StartsWith(cachePrefix, StringComparison.Ordinal))
            {
                DirectoryEntries.TryRemove(key, out _);
            }
        }
    }

    private static string BuildCacheKey(string? udid, string normalizedPath) => $"{GetDeviceKey(udid)}|{normalizedPath}";

    private static string GetDeviceKey(string? udid) => string.IsNullOrWhiteSpace(udid) ? "default-device" : udid.Trim();

    private static string NormalizePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return "/";
        }

        string normalized = remotePath.Replace('\\', '/').Trim();
        if (normalized == "/")
        {
            return "/";
        }

        return normalized.Trim('/');
    }

    private static string? GetParentPath(string remotePath)
    {
        string normalized = NormalizePath(remotePath);
        if (normalized == "/")
        {
            return null;
        }

        string trimmed = normalized.Trim('/');
        int separatorIndex = trimmed.LastIndexOf('/');
        if (separatorIndex < 0)
        {
            return "/";
        }

        return trimmed[..separatorIndex];
    }

    private sealed class CacheEntry<T>(T value)
    {
        public T Value { get; } = value;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CreatedAtUtc > ttl;
    }
}

internal static class MediaIndexStore
{
    private static readonly ConcurrentDictionary<string, CacheEntry<MediaEnumerationResult>> Snapshots = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new(StringComparer.Ordinal);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    public static async Task<MediaEnumerationResult> GetOrBuildAsync(string? udid, bool includeAdditionalRoots, Func<MediaEnumerationResult> refreshFactory)
    {
        string key = BuildKey(udid, includeAdditionalRoots);
        if (Snapshots.TryGetValue(key, out CacheEntry<MediaEnumerationResult>? snapshot))
        {
            if (snapshot.IsExpired(StaleAfter))
            {
                _ = TriggerRefreshAsync(udid, includeAdditionalRoots, refreshFactory);
            }

            return snapshot.Value;
        }

        return await RefreshAsync(udid, includeAdditionalRoots, refreshFactory);
    }

    public static async Task TriggerRefreshAsync(string? udid, bool includeAdditionalRoots, Func<MediaEnumerationResult> refreshFactory)
    {
        try
        {
            await RefreshAsync(udid, includeAdditionalRoots, refreshFactory);
        }
        catch
        {
        }
    }

      public static async Task<MediaEnumerationResult> ForceRefreshAsync(string? udid, bool includeAdditionalRoots, Func<MediaEnumerationResult> refreshFactory)
      {
        string key = BuildKey(udid, includeAdditionalRoots);
        SemaphoreSlim refreshLock = RefreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync();
        try
        {
          return await Task.Run(() =>
          {
            MediaEnumerationResult refreshed = refreshFactory();
            Snapshots[key] = new CacheEntry<MediaEnumerationResult>(refreshed);
            return refreshed;
          });
        }
        finally
        {
          refreshLock.Release();
        }
      }

    public static void MarkDirty(string? udid)
    {
        string prefix = $"{GetDeviceKey(udid)}|";
        foreach ((string key, _) in Snapshots)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                Snapshots.TryRemove(key, out _);
            }
        }
    }

    private static async Task<MediaEnumerationResult> RefreshAsync(string? udid, bool includeAdditionalRoots, Func<MediaEnumerationResult> refreshFactory)
    {
        string key = BuildKey(udid, includeAdditionalRoots);
        SemaphoreSlim refreshLock = RefreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync();
        try
        {
            if (Snapshots.TryGetValue(key, out CacheEntry<MediaEnumerationResult>? cached) && !cached.IsExpired(StaleAfter))
            {
                return cached.Value;
            }

            return await Task.Run(() =>
            {
                MediaEnumerationResult refreshed = refreshFactory();
                Snapshots[key] = new CacheEntry<MediaEnumerationResult>(refreshed);
                return refreshed;
            });
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static string BuildKey(string? udid, bool includeAdditionalRoots) => $"{GetDeviceKey(udid)}|{(includeAdditionalRoots ? "all-roots" : "dcim-only")}";

    private static string GetDeviceKey(string? udid) => string.IsNullOrWhiteSpace(udid) ? "default-device" : udid.Trim();

    private sealed class CacheEntry<T>(T value)
    {
        public T Value { get; } = value;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CreatedAtUtc > ttl;
    }

    internal static class PtpFallbackState
    {
        private static readonly ConcurrentDictionary<string, DateTimeOffset> PtpUnavailableUntil = new(StringComparer.Ordinal);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

        public static bool ShouldBypassPtp(string? udid)
        {
            string deviceKey = GetDeviceKey(udid);
            if (!PtpUnavailableUntil.TryGetValue(deviceKey, out DateTimeOffset unavailableUntil))
            {
                return false;
            }

            if (unavailableUntil <= DateTimeOffset.UtcNow)
            {
                PtpUnavailableUntil.TryRemove(deviceKey, out _);
                return false;
            }

            return true;
        }

        public static void MarkPtpUnavailable(string? udid) => PtpUnavailableUntil[GetDeviceKey(udid)] = DateTimeOffset.UtcNow.Add(RetryAfter);

        public static void Clear(string? udid) => PtpUnavailableUntil.TryRemove(GetDeviceKey(udid), out _);

        private static string GetDeviceKey(string? udid) => string.IsNullOrWhiteSpace(udid) ? "default-device" : udid.Trim();
    }
}

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
    public static readonly string QueueFilePath = Path.Combine(AppContext.BaseDirectory, "failed-transfers.json");
    public static readonly object QueueFileLock = new();
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
    input[type="checkbox"] {
      width: 18px;
      height: 18px;
      margin: 0;
      border: 1px solid #111;
      border-radius: 4px;
      background: #fff;
      appearance: none;
      -webkit-appearance: none;
      display: inline-grid;
      place-content: center;
      cursor: pointer;
    }
    input[type="checkbox"]::before {
      content: "";
      width: 10px;
      height: 10px;
      border-radius: 2px;
      background: var(--accent);
      transform: scale(0);
      transition: transform .12s ease-in-out;
    }
    input[type="checkbox"]:checked::before {
      transform: scale(1);
    }
    input[type="checkbox"]:disabled {
      opacity: .55;
      cursor: not-allowed;
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
    .scan-dropdown {
      position: relative;
      display: inline-flex;
      align-items: stretch;
      min-width: 0;
    }
    .scan-dropdown .scan-main {
      border-top-right-radius: 0;
      border-bottom-right-radius: 0;
      white-space: nowrap;
    }
    .scan-dropdown .scan-toggle {
      border-top-left-radius: 0;
      border-bottom-left-radius: 0;
      border-left: 1px solid rgba(34, 70, 183, 0.2);
      min-width: 38px;
      padding: 12px 10px;
    }
    .scan-menu {
      position: absolute;
      top: calc(100% + 6px);
      left: 0;
      min-width: 220px;
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 12px;
      box-shadow: var(--shadow);
      padding: 8px;
      display: grid;
      gap: 6px;
      z-index: 30;
    }
    .scan-menu button {
      width: 100%;
      text-align: left;
      border-radius: 9px;
      padding: 10px 12px;
      white-space: nowrap;
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
    .ptp-status {
      min-height: 20px;
      margin: -10px 0 14px;
      color: var(--muted);
      font-size: .88rem;
    }
    .ptp-status.ok { color: #2b7a3e; }
    .ptp-status.error { color: #b63838; }
    .transfer-progress-panel {
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 14px;
      box-shadow: var(--shadow);
      padding: 10px 12px;
      margin-bottom: 14px;
      display: grid;
      gap: 8px;
    }
    .transfer-progress-summary {
      color: var(--muted);
      font-size: .9rem;
    }
    .transfer-progress-list {
      max-height: 240px;
      overflow: auto;
      display: grid;
      gap: 8px;
      padding-right: 2px;
    }
    .tp-row {
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 8px 10px;
      background: rgba(255,255,255,0.72);
      display: grid;
      gap: 6px;
    }
    .tp-head {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      align-items: center;
      font-size: .87rem;
    }
    .tp-name {
      font-weight: 600;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .tp-status {
      color: var(--muted);
      white-space: nowrap;
      font-size: .82rem;
    }
    .tp-status.failed { color: #b63838; }
    .tp-status.succeeded { color: #2b7a3e; }
    .tp-track {
      height: 8px;
      background: color-mix(in srgb, #2867ff 15%, transparent);
      border-radius: 999px;
      overflow: hidden;
      position: relative;
    }
    .tp-fill {
      height: 100%;
      width: 0%;
      background: #2867ff;
      transition: width .2s ease;
    }
    .tp-fill.running.indeterminate {
      width: 40%;
      animation: indeterminate 1.2s ease-in-out infinite;
    }
    .tp-fill.failed {
      background: #c03838;
    }
    .tp-fill.succeeded {
      background: #2b7a3e;
    }
    .tp-path {
      color: var(--muted);
      font-size: .8rem;
      word-break: break-all;
    }
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
    .thumb-shell {
      position: relative;
      aspect-ratio: 1 / 1;
      overflow: hidden;
      background: linear-gradient(135deg, #d9e3ff, #edf2ff);
    }
    .thumb-shell .preview {
      width: 100%;
      height: 100%;
      aspect-ratio: auto;
    }
    .thumb-shell .thumb-loading {
      position: absolute;
      inset: 0;
      display: grid;
      place-items: center;
      background: linear-gradient(135deg, rgba(126, 140, 184, 0.24), rgba(28, 34, 56, 0.24));
      z-index: 1;
    }
    .thumb-shell.loaded .thumb-loading,
    .thumb-shell.error .thumb-loading {
      display: none;
    }
    .card-copy-progress {
      height: 4px;
      overflow: hidden;
      background: rgba(58, 208, 72, 0.26);
    }
    .card-copy-progress-fill {
      height: 100%;
      width: 0%;
      background: #32dd46;
      transition: width .2s ease;
    }
    .card-copy-progress-fill.indeterminate {
      width: 42%;
      animation: thumbProgress 0.9s linear infinite;
    }
    .card-copy-progress.failed {
      background: rgba(192,56,56,0.22);
    }
    .card-copy-progress.failed .card-copy-progress-fill {
      background: #c03838;
    }
    @keyframes thumbProgress {
      0% { transform: translateX(-120%); }
      100% { transform: translateX(320%); }
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
    .video-loading {
      position: absolute;
      inset: 0;
      display: grid;
      place-items: center;
      background: linear-gradient(135deg, rgba(126, 140, 184, 0.24), rgba(28, 34, 56, 0.34));
      z-index: 1;
    }
    .video-loading.hidden {
      display: none;
    }
    .video-spinner {
      width: 34px;
      height: 34px;
      border-radius: 50%;
      border: 3px solid rgba(255, 255, 255, 0.35);
      border-top-color: #ffffff;
      animation: videoSpin 0.8s linear infinite;
    }
    @keyframes videoSpin {
      from { transform: rotate(0deg); }
      to { transform: rotate(360deg); }
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
      top: 10px;
      left: 10px;
      width: 30px;
      height: 30px;
      display: grid;
      place-items: center;
      background: rgba(0, 0, 0, 0.40);
      color: white;
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
      padding: 4px 8px;
      display: flex;
      align-items: center;
      gap: 4px;
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
    .hidden { display: none !important; }
    .fs-view {
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 20px;
      box-shadow: var(--shadow);
      padding: 14px;
      display: grid;
      gap: 12px;
    }
    .fs-toolbar {
      display: grid;
      gap: 10px;
      grid-template-columns: minmax(200px, 1fr) auto auto;
      align-items: center;
    }
    .fs-list {
      border: 1px solid var(--border);
      border-radius: 12px;
      overflow: hidden;
      background: rgba(255,255,255,0.85);
    }
    .fs-row {
      display: grid;
      grid-template-columns: auto minmax(180px, 1fr) auto;
      gap: 10px;
      align-items: center;
      padding: 10px 12px;
      border-bottom: 1px solid var(--border);
    }
    .fs-row:last-child { border-bottom: 0; }
    .fs-row button {
      padding: 8px 10px;
      border-radius: 8px;
    }
    .fs-row .path {
      color: var(--muted);
      font-size: .88rem;
      word-break: break-all;
    }
    .fs-row .meta {
      display: flex;
      flex-direction: column;
      align-items: flex-end;
      gap: 6px;
      color: var(--muted);
      font-size: .85rem;
      white-space: nowrap;
    }
    .fs-inline-status {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: 28px;
      padding: 4px 10px;
      border-radius: 999px;
      border: 1px solid var(--border);
      background: rgba(40,103,255,0.08);
      color: #1e4fd6;
      font-size: .8rem;
      font-weight: 600;
      max-width: min(260px, 100%);
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .fs-inline-status.succeeded {
      background: rgba(43,122,62,0.1);
      color: #2b7a3e;
    }
    .fs-inline-status.failed {
      background: rgba(182,56,56,0.1);
      color: #b63838;
    }
    .fs-inline-status.queued {
      background: rgba(130,145,180,0.12);
      color: var(--muted);
    }
    @media (max-width: 900px) {
      .toolbar { grid-template-columns: 1fr 1fr; }
      .fs-toolbar { grid-template-columns: 1fr; }
    }
    @media (max-width: 640px) {
      .page { padding: 16px; }
      .toolbar { grid-template-columns: 1fr; }
      .fs-row { grid-template-columns: 1fr; }
      .fs-row .meta { align-items: flex-start; }
    }
    .settings-btn {
      background: none;
      border: 0;
      font-size: 1.4rem;
      padding: 6px 8px;
      border-radius: 10px;
      cursor: pointer;
      color: var(--muted);
      transition: color .15s ease, background .15s ease;
    }
    .settings-btn:hover { color: var(--text); background: var(--border); transform: none; }
    .modal-overlay {
      display: none;
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.4);
      z-index: 200;
      align-items: center;
      justify-content: center;
    }
    .modal-overlay.active { display: flex; }
    .modal {
      background: var(--panel);
      border-radius: 20px;
      box-shadow: var(--shadow);
      min-width: 320px;
      max-width: 720px;
      width: 92%;
      max-height: 88vh;
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }
    .modal-title {
      margin: 0;
      font-size: 1.2rem;
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 20px 24px 16px;
      border-bottom: 1px solid var(--border);
      flex-shrink: 0;
    }
    .modal-body {
      display: flex;
      flex: 1;
      overflow: hidden;
    }
    .settings-nav {
      width: 180px;
      flex-shrink: 0;
      padding: 12px 8px;
      border-right: 1px solid var(--border);
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .settings-nav-item {
      padding: 9px 14px;
      border-radius: 10px;
      cursor: pointer;
      font-size: .95rem;
      color: var(--text);
      background: none;
      border: 0;
      text-align: left;
      width: 100%;
      transition: background .12s ease, color .12s ease;
    }
    .settings-nav-item:hover { background: var(--border); }
    .settings-nav-item.active { background: var(--border); font-weight: 600; }
    .settings-panels {
      flex: 1;
      overflow-y: auto;
      padding: 20px 24px;
    }
    .settings-panel { display: none; }
    .settings-panel.active { display: block; }
    .settings-panel-title {
      font-size: 1rem;
      font-weight: 700;
      margin: 0 0 16px;
      color: var(--accent);
    }
    .settings-section {
      margin-bottom: 20px;
    }
    .settings-section-title {
      font-size: .92rem;
      font-weight: 600;
      margin: 0 0 4px;
      color: var(--text);
    }
    .settings-section-hint {
      font-size: .82rem;
      color: var(--muted);
      margin: 0 0 10px;
    }
    .settings-check-row, .settings-radio-row {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 5px 0;
      font-size: .92rem;
      cursor: pointer;
    }
    .settings-check-row input, .settings-radio-row input {
      width: 18px;
      height: 18px;
      accent-color: var(--accent);
      cursor: pointer;
      flex-shrink: 0;
    }
    .settings-radio-hint {
      font-size: .8rem;
      color: var(--muted);
      margin-left: 4px;
    }
    .modal-footer { display: flex; gap: 10px; justify-content: flex-end; padding: 14px 24px; border-top: 1px solid var(--border); flex-shrink: 0; }
    .preview-modal {
      max-width: 980px;
      width: 95%;
      max-height: 90vh;
    }
    .preview-body {
      padding: 14px;
      background: rgba(14, 18, 28, 0.94);
      display: grid;
      place-items: center;
      min-height: 280px;
    }
    .preview-body img,
    .preview-body video {
      max-width: 100%;
      max-height: 72vh;
      border-radius: 12px;
      box-shadow: 0 10px 32px rgba(0,0,0,0.35);
      background: #111626;
    }
    .context-menu {
      position: fixed;
      z-index: 350;
      min-width: 190px;
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 10px;
      box-shadow: var(--shadow);
      padding: 6px;
      display: grid;
      gap: 4px;
    }
    .context-menu button {
      width: 100%;
      text-align: left;
      padding: 9px 10px;
      border-radius: 8px;
      border: 0;
      background: transparent;
      color: var(--text);
      cursor: pointer;
    }
    .context-menu button:hover {
      background: var(--border);
      transform: none;
    }
    .setting-row {
      display: grid;
      gap: 6px;
    }
    .setting-label {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
    }
    .setting-label strong { font-weight: 600; }
    .setting-label .setting-value {
      font-size: 1.1rem;
      font-weight: 700;
      color: var(--accent);
    }
    .setting-hint { color: var(--muted); font-size: .85rem; }
    input[type="range"] {
      width: 100%;
      accent-color: var(--accent);
      cursor: pointer;
    }
    .queue-panel {
      background: var(--panel);
      border: 1px solid var(--border);
      border-radius: 20px;
      box-shadow: var(--shadow);
      margin-top: 18px;
      overflow: hidden;
    }
    .queue-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 14px 16px;
      border-bottom: 1px solid var(--border);
      gap: 10px;
      flex-wrap: wrap;
    }
    .queue-header h3 { margin: 0; font-size: 1rem; }
    .queue-header-actions { display: flex; gap: 8px; }
    .queue-item {
      display: grid;
      grid-template-columns: 1fr auto auto;
      gap: 10px;
      align-items: center;
      padding: 12px 16px;
      border-bottom: 1px solid var(--border);
    }
    .queue-item:last-child { border-bottom: 0; }
    .queue-item-info { min-width: 0; }
    .queue-item-info .qi-path { color: var(--muted); font-size: .85rem; word-break: break-all; }
    .queue-item-info .qi-error { color: #b63838; font-size: .85rem; margin-top: 2px; }
    .queue-item-info .qi-meta { color: var(--muted); font-size: .8rem; margin-top: 2px; }
    .queue-item-btns { display: flex; gap: 6px; align-items: center; }
    .queue-item-btns button { padding: 8px 10px; border-radius: 8px; white-space: nowrap; }
    @media (max-width: 640px) {
      .queue-item { grid-template-columns: 1fr; }
      .queue-item-btns { justify-content: flex-end; }
    }
  </style>
</head>
<body>
  <div class="page">
    <div class="header">
      <div>
        <h1>Apple File Conduit</h1>
        <div class="subtitle">Browse media or the remote file system from the connected iPhone and copy, move, or delete selected items.</div>
      </div>
      <button class="settings-btn" id="settingsButton" title="Settings" aria-label="Settings">⚙</button>
    </div>

    <div class="toolbar">
      <input id="destination" placeholder="Enter an absolute destination folder, for example C:\Users\you\Pictures\Imports or /home/you/Pictures/Imports">
      <select id="viewMode">
        <option value="media">Media view</option>
        <option value="filesystem">File system view</option>
      </select>
      <select id="filter">
        <option value="all">All media</option>
        <option value="photo">Photos</option>
        <option value="live-photo">Live Photos</option>
        <option value="video">Videos</option>
      </select>
      <label class="toggle" title="Any iCloud-downloaded originals that live in /var/mobile/Media/PhotoData/">
        <input type="checkbox" id="includeAdditionalRoots" title="Any iCloud-downloaded originals that live in /var/mobile/Media/PhotoData/">
        <span>Include PhotoData</span>
      </label>
      <div class="scan-dropdown" id="scanDropdown">
        <button id="scanButton" class="scan-main">Scan</button>
        <button id="scanMenuButton" class="scan-toggle" title="More scan actions" aria-label="More scan actions">▾</button>
        <div class="scan-menu hidden" id="scanMenu">
          <button id="scanCachedButton">Scan</button>
          <button id="scanRecacheButton" title="Scan and recache resets media and file-system cache before scanning.">Scan and recache</button>
        </div>
      </div>
      <button class="hidden" id="retryPtpButton" title="Clear the PTP retry cooldown and scan again using PTP">Retry PTP</button>
      <button class="primary" id="copyButton">Copy Selected</button>
      <button class="danger" id="moveButton">Move Selected</button>
      <button class="primary hidden" id="fsCopyButton">Copy Selected Paths</button>
      <button class="danger hidden" id="fsMoveButton">Move Selected Paths</button>
      <button class="danger hidden" id="fsDeleteButton">Delete Selected Paths</button>
      <button class="hidden" id="fsRefreshButton">Refresh Folder</button>
    </div>

    <div class="progress-bar" id="progressBar"></div>
    <div class="status" id="status">Choose options and click Scan to start.</div>
    <div class="ptp-status" id="ptpStatus">PTP status: checking...</div>
    <div class="transfer-progress-panel hidden" id="transferProgressPanel">
      <div class="transfer-progress-summary" id="transferProgressSummary"></div>
      <div class="transfer-progress-list" id="transferProgressList"></div>
    </div>

    <div id="mediaView">
      <div class="summary" id="summary"></div>
      <div class="grid" id="grid"></div>
    </div>

    <div class="fs-view hidden" id="fsView">
      <div class="fs-toolbar">
        <input id="fsPath" value="DCIM" placeholder="Remote folder path, for example DCIM or PhotoData">
        <button id="fsOpenButton">Open Folder</button>
        <button id="fsUpButton">Up</button>
      </div>
      <div class="summary" id="fsSummary"></div>
      <div class="fs-list" id="fsList"></div>
    </div>

    <div id="queuePanel" class="queue-panel hidden">
      <div class="queue-header">
        <h3 id="queueTitle">⚠ Failed Transfers</h3>
        <div class="queue-header-actions">
          <button id="retryAllButton">↺ Retry All</button>
          <button class="danger" id="clearQueueButton">✕ Clear All</button>
        </div>
      </div>
      <div id="queueList"></div>
    </div>
  </div>

  <div class="modal-overlay" id="settingsOverlay" role="dialog" aria-modal="true" aria-labelledby="settingsTitle">
    <div class="modal">
      <h2 class="modal-title" id="settingsTitle">⚙ Settings</h2>
      <div class="modal-body">
        <nav class="settings-nav" aria-label="Settings navigation">
          <button class="settings-nav-item active" data-panel="general">General</button>
          <button class="settings-nav-item" data-panel="photos">Photos</button>
        </nav>
        <div class="settings-panels">

          <!-- General panel -->
          <div class="settings-panel active" id="panel-general">
            <p class="settings-panel-title">General</p>
            <div class="setting-row">
              <div class="setting-label">
                <strong>Parallel transfers</strong>
                <span class="setting-value" id="parallelismValue">4</span>
              </div>
              <div class="setting-hint">Number of files to copy, move, or delete simultaneously (1–16).</div>
              <input type="range" id="parallelismInput" min="1" max="16" value="4" aria-label="Parallel transfers">
            </div>
            <div class="settings-section" style="margin-top:14px;">
              <p class="settings-section-title">Media Refresh</p>
              <p class="settings-section-hint">When enabled, copy operations in Media view automatically rescan items after completion. Move operations always rescan.</p>
              <label class="settings-check-row">
                <input type="checkbox" id="settingAutoRefreshAfterTransfer">
                Auto-refresh after copy
              </label>
            </div>
          </div>

          <!-- Photos panel -->
          <div class="settings-panel" id="panel-photos">
            <p class="settings-panel-title">Photos</p>

            <div class="settings-section">
              <p class="settings-section-title">Edited Photos:</p>
              <p class="settings-section-hint">Includes filters or adjustments to saturation, contrast, brightness, or aspect ratio</p>
              <label class="settings-check-row">
                <input type="checkbox" id="settingExportOriginal" checked>
                Export Original Photo
              </label>
              <label class="settings-check-row">
                <input type="checkbox" id="settingExportEdited" checked>
                Export Edited Version
              </label>
            </div>

            <div class="settings-section">
              <p class="settings-section-title">Photo Export Naming Method</p>
              <label class="settings-radio-row">
                <input type="radio" name="photoNaming" id="settingNamingOriginal" value="original">
                Original Filename
              </label>
              <label class="settings-radio-row">
                <input type="radio" name="photoNaming" id="settingNamingDatetime" value="datetime" checked>
                Name by Capture Date/Time
              </label>
              <p class="settings-section-hint">Capture Date/Time format: yyyy_MM_dd_HH_mm_ss_originalName (example: 2023_04_11_21_59_57_IMG_2814).</p>
            </div>

            <div class="settings-section">
              <p class="settings-section-title">Live Photo Viewing Mode</p>
              <label class="settings-radio-row">
                <input type="radio" name="livePhotoMode" id="settingLiveStatic" value="static" checked>
                Static
              </label>
              <label class="settings-radio-row">
                <input type="radio" name="livePhotoMode" id="settingLiveDynamic" value="dynamic">
                Dynamic
              </label>
            </div>

            <div class="settings-section">
              <p class="settings-section-title">Live Photo Export Files</p>
              <label class="settings-radio-row">
                <input type="radio" name="livePhotoExport" id="settingLiveImageVideo" value="image+video" checked>
                Image + Video
              </label>
              <label class="settings-radio-row">
                <input type="radio" name="livePhotoExport" id="settingLiveImageOnly" value="image">
                Image Only
              </label>
            </div>

            <div class="settings-section">
              <p class="settings-section-title">HEIC Photo Export Format</p>
              <label class="settings-radio-row">
                <input type="radio" name="heicFormat" id="settingHeicJpg" value="jpg">
                JPG
              </label>
              <label class="settings-radio-row">
                <input type="radio" name="heicFormat" id="settingHeicHeic" value="heic" checked>
                HEIC <span class="settings-radio-hint">ⓘ Apple-specific format, may not be viewable in regular viewers</span>
              </label>
            </div>
          </div>

        </div>
      </div>
      <div class="modal-footer">
        <button class="primary" id="settingsCloseButton">Done</button>
      </div>
    </div>
  </div>

  <div class="modal-overlay" id="previewOverlay" role="dialog" aria-modal="true" aria-labelledby="previewTitle">
    <div class="modal preview-modal">
      <h2 class="modal-title" id="previewTitle">Preview</h2>
      <div class="preview-body" id="previewBody"></div>
      <div class="modal-footer">
        <button class="primary" id="previewCloseButton">Close</button>
      </div>
    </div>
  </div>

  <div class="context-menu hidden" id="cardContextMenu">
    <button id="contextPreviewButton">Preview</button>
  </div>

  <script>
    const settingsStorageKey = 'afc-settings';

    function loadSettings() {
      try { return JSON.parse(localStorage.getItem(settingsStorageKey) || '{}'); } catch { return {}; }
    }

    function saveSettings() {
      localStorage.setItem(settingsStorageKey, JSON.stringify(settings));
    }

    const settings = Object.assign({
      parallelism: 4,
      autoRefreshAfterTransfer: false,
      exportOriginalPhoto: true,
      exportEditedVersion: true,
      photoNaming: 'datetime',
      livePhotoViewingMode: 'static',
      livePhotoExportFiles: 'image+video',
      heicExportFormat: 'heic'
    }, loadSettings());

    const state = {
      items: [],
      selected: new Set(),
      filter: 'all',
      busy: false,
      viewMode: 'media',
      fsCurrentPath: 'DCIM',
      fsParentPath: '/',
      fsEntries: [],
      fsSelected: new Set(),
      transferProgressSnapshot: null,
      transferProgressOperationId: null,
      transferProgressTimer: null,
      ptpFallbackActive: false,
      videoPreviewCache: new Map(),
      videoHydrating: new Set(),
      videoHydrationQueue: [],
      videoHydrationActive: 0,
      contextMenuItem: null
    };

    const summary = document.getElementById('summary');
    const fsSummary = document.getElementById('fsSummary');
    const status = document.getElementById('status');
    const ptpStatus = document.getElementById('ptpStatus');
    const progressBar = document.getElementById('progressBar');
    const grid = document.getElementById('grid');
    const fsList = document.getElementById('fsList');
    const mediaView = document.getElementById('mediaView');
    const fsView = document.getElementById('fsView');
    const destinationInput = document.getElementById('destination');
    const viewModeInput = document.getElementById('viewMode');
    const filterInput = document.getElementById('filter');
    const includeAdditionalRootsInput = document.getElementById('includeAdditionalRoots');
    const scanDropdown = document.getElementById('scanDropdown');
    const scanButton = document.getElementById('scanButton');
    const scanMenuButton = document.getElementById('scanMenuButton');
    const scanMenu = document.getElementById('scanMenu');
    const scanCachedButton = document.getElementById('scanCachedButton');
    const scanRecacheButton = document.getElementById('scanRecacheButton');
    const retryPtpButton = document.getElementById('retryPtpButton');
    const copyButton = document.getElementById('copyButton');
    const moveButton = document.getElementById('moveButton');
    const fsCopyButton = document.getElementById('fsCopyButton');
    const fsMoveButton = document.getElementById('fsMoveButton');
    const fsDeleteButton = document.getElementById('fsDeleteButton');
    const fsRefreshButton = document.getElementById('fsRefreshButton');
    const fsPathInput = document.getElementById('fsPath');
    const fsOpenButton = document.getElementById('fsOpenButton');
    const fsUpButton = document.getElementById('fsUpButton');
    const settingsButton = document.getElementById('settingsButton');
    const settingsOverlay = document.getElementById('settingsOverlay');
    const settingsCloseButton = document.getElementById('settingsCloseButton');
    const previewOverlay = document.getElementById('previewOverlay');
    const previewBody = document.getElementById('previewBody');
    const previewTitle = document.getElementById('previewTitle');
    const previewCloseButton = document.getElementById('previewCloseButton');
    const cardContextMenu = document.getElementById('cardContextMenu');
    const contextPreviewButton = document.getElementById('contextPreviewButton');
    const parallelismInput = document.getElementById('parallelismInput');
    const parallelismValue = document.getElementById('parallelismValue');
    const settingAutoRefreshAfterTransfer = document.getElementById('settingAutoRefreshAfterTransfer');
    const settingExportOriginal = document.getElementById('settingExportOriginal');
    const settingExportEdited = document.getElementById('settingExportEdited');
    const settingNamingOriginal = document.getElementById('settingNamingOriginal');
    const settingNamingDatetime = document.getElementById('settingNamingDatetime');
    const settingLiveStatic = document.getElementById('settingLiveStatic');
    const settingLiveDynamic = document.getElementById('settingLiveDynamic');
    const settingLiveImageVideo = document.getElementById('settingLiveImageVideo');
    const settingLiveImageOnly = document.getElementById('settingLiveImageOnly');
    const settingHeicJpg = document.getElementById('settingHeicJpg');
    const settingHeicHeic = document.getElementById('settingHeicHeic');
    const queuePanel = document.getElementById('queuePanel');
    const queueList = document.getElementById('queueList');
    const queueTitle = document.getElementById('queueTitle');
    const retryAllButton = document.getElementById('retryAllButton');
    const clearQueueButton = document.getElementById('clearQueueButton');
    const transferProgressPanel = document.getElementById('transferProgressPanel');
    const transferProgressSummary = document.getElementById('transferProgressSummary');
    const transferProgressList = document.getElementById('transferProgressList');
    let ptpStatusTimer = null;

    filterInput.addEventListener('change', () => {
      state.filter = filterInput.value;
      renderGrid();
      renderSummary();
    });

    viewModeInput.addEventListener('change', () => setViewMode(viewModeInput.value));
    scanButton.addEventListener('click', () => loadMedia(false));
    scanMenuButton.addEventListener('click', event => {
      event.stopPropagation();
      scanMenu.classList.toggle('hidden');
    });
    scanCachedButton.addEventListener('click', () => {
      closeScanMenu();
      loadMedia(false);
    });
    scanRecacheButton.addEventListener('click', () => {
      closeScanMenu();
      scanAndRecache();
    });
    retryPtpButton.addEventListener('click', () => retryPtp());
    copyButton.addEventListener('click', () => transfer('copy'));
    moveButton.addEventListener('click', () => transfer('move'));
    fsCopyButton.addEventListener('click', () => transferFs('copy'));
    fsMoveButton.addEventListener('click', () => transferFs('move'));
    fsDeleteButton.addEventListener('click', () => transferFs('delete'));
    fsRefreshButton.addEventListener('click', () => loadFs(state.fsCurrentPath));
    fsOpenButton.addEventListener('click', () => loadFs(fsPathInput.value));
    fsUpButton.addEventListener('click', () => {
      if (state.fsParentPath) {
        loadFs(state.fsParentPath);
      }
    });

    settingsButton.addEventListener('click', () => openSettings());
    settingsOverlay.addEventListener('click', event => {
      if (event.target === settingsOverlay) closeSettings();
    });
    previewOverlay.addEventListener('click', event => {
      if (event.target === previewOverlay) closePreview();
    });
    settingsCloseButton.addEventListener('click', () => closeSettings());
    previewCloseButton.addEventListener('click', () => closePreview());
    contextPreviewButton.addEventListener('click', () => {
      if (state.contextMenuItem) {
        openPreview(state.contextMenuItem);
      }
      closeContextMenu();
    });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && settingsOverlay.classList.contains('active')) closeSettings();
      if (event.key === 'Escape' && previewOverlay.classList.contains('active')) closePreview();
      if (event.key === 'Escape') closeScanMenu();
      if (event.key === 'Escape') closeContextMenu();
    });
    document.addEventListener('click', event => {
      if (!scanDropdown.contains(event.target)) {
        closeScanMenu();
      }
      if (!cardContextMenu.contains(event.target)) {
        closeContextMenu();
      }
    });
    parallelismInput.addEventListener('input', () => {
      settings.parallelism = parseInt(parallelismInput.value);
      parallelismValue.textContent = settings.parallelism;
      saveSettings();
    });
    settingAutoRefreshAfterTransfer.addEventListener('change', () => {
      settings.autoRefreshAfterTransfer = settingAutoRefreshAfterTransfer.checked;
      saveSettings();
    });
    settingExportOriginal.addEventListener('change', () => { settings.exportOriginalPhoto = settingExportOriginal.checked; saveSettings(); });
    settingExportEdited.addEventListener('change', () => { settings.exportEditedVersion = settingExportEdited.checked; saveSettings(); });
    document.querySelectorAll('input[name="photoNaming"]').forEach(r => r.addEventListener('change', () => { settings.photoNaming = r.value; saveSettings(); }));
    document.querySelectorAll('input[name="livePhotoMode"]').forEach(r => r.addEventListener('change', () => { settings.livePhotoViewingMode = r.value; saveSettings(); }));
    document.querySelectorAll('input[name="livePhotoExport"]').forEach(r => r.addEventListener('change', () => { settings.livePhotoExportFiles = r.value; saveSettings(); }));
    document.querySelectorAll('input[name="heicFormat"]').forEach(r => r.addEventListener('change', () => { settings.heicExportFormat = r.value; saveSettings(); }));
    document.querySelectorAll('.settings-nav-item').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.settings-nav-item').forEach(b => b.classList.remove('active'));
        document.querySelectorAll('.settings-panel').forEach(p => p.classList.remove('active'));
        btn.classList.add('active');
        document.getElementById('panel-' + btn.dataset.panel).classList.add('active');
      });
    });
    retryAllButton.addEventListener('click', () => retryQueueItems(null));
    clearQueueButton.addEventListener('click', () => clearQueue());

    function setBusy(value) {
      state.busy = value;
      copyButton.disabled = value;
      moveButton.disabled = value;
      scanButton.disabled = value;
      scanMenuButton.disabled = value;
      scanCachedButton.disabled = value;
      scanRecacheButton.disabled = value;
      retryPtpButton.disabled = value;
      fsCopyButton.disabled = value;
      fsMoveButton.disabled = value;
      fsDeleteButton.disabled = value;
      fsRefreshButton.disabled = value;
      fsOpenButton.disabled = value;
      fsUpButton.disabled = value || !state.fsParentPath;
      filterInput.disabled = value;
      includeAdditionalRootsInput.disabled = value;
      viewModeInput.disabled = value;
      fsPathInput.disabled = value;
      progressBar.classList.toggle('active', value);
    }

    function closeScanMenu() {
      scanMenu.classList.add('hidden');
    }

    function setStatus(message, isError = false) {
      status.textContent = message;
      status.classList.toggle('error', isError);
    }

    function setPtpStatus(message, isAvailable) {
      ptpStatus.textContent = message;
      ptpStatus.classList.toggle('ok', isAvailable === true);
      ptpStatus.classList.toggle('error', isAvailable === false);
    }

    async function refreshPtpStatus() {
      try {
        const response = await fetch(`/api/ptp-status?_=${Date.now()}`, { cache: 'no-store' });
        if (!response.ok) {
          throw new Error('PTP status endpoint unavailable.');
        }

        const data = await response.json();
        const codeNote = data.nativeErrorCode != null ? ` (error ${data.nativeErrorCode})` : '';
        const detailNote = !data.isAvailable && data.detail ? ` ${data.detail}` : '';
        const timingNote = data.probeDurationMs != null ? ` [${data.probeDurationMs} ms]` : '';
        setPtpStatus(
          data.isAvailable
            ? `PTP status: Available${timingNote}`
            : `PTP status: Unavailable${codeNote}${timingNote}.${detailNote}`,
          Boolean(data.isAvailable)
        );
      } catch {
        setPtpStatus('PTP status: unavailable (status probe failed).', false);
      }
    }

    function createOperationId() {
      if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
      }
      return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }

    function buildMediaLoadStatus(data) {
      const scannedRoots = formatRootList(data.scannedRoots);
      const backendNote = data.backendNote ? ` ${data.backendNote}` : '';
      return `Loaded ${state.items.length} items from ${scannedRoots}.${backendNote}`;
    }

    function statusTextForProgressItem(item) {
      if (item.status === 'failed') return item.errorMessage || 'Failed';
      if (item.status === 'succeeded') return 'Done';
      if (item.status === 'running') return item.totalBytes > 0
        ? `${formatBytes(item.bytesCopied)} / ${formatBytes(item.totalBytes)}`
        : 'In progress…';
      return 'Queued';
    }

    function isFsTransferProgress(progress = state.transferProgressSnapshot) {
      return Boolean(progress?.label && progress.label.toLowerCase().includes('file system items'));
    }

    function getFsProgressByPath() {
      if (!isFsTransferProgress() || !state.transferProgressSnapshot?.items?.length) {
        return new Map();
      }

      return new Map(
        state.transferProgressSnapshot.items
          .filter(item => item?.remotePath)
          .map(item => [item.remotePath, item])
      );
    }

    function getMediaProgressByPath() {
      if (isFsTransferProgress() || !state.transferProgressSnapshot?.items?.length) {
        return new Map();
      }

      return new Map(
        state.transferProgressSnapshot.items
          .filter(item => item?.remotePath)
          .map(item => [item.remotePath, item])
      );
    }

    function progressPercentForItem(item) {
      if (item.status === 'succeeded') return 100;
      if (item.status === 'failed') return item.totalBytes > 0 ? Math.min(100, (item.bytesCopied / item.totalBytes) * 100) : 100;
      if (!item.totalBytes || item.totalBytes <= 0) return 0;
      return Math.min(100, Math.max(0, (item.bytesCopied / item.totalBytes) * 100));
    }

    function renderTransferProgress(progress) {
      const previousWasFsProgress = isFsTransferProgress();
      state.transferProgressSnapshot = progress ?? null;
      const fsProgress = isFsTransferProgress(progress);

      if (!progress) {
        transferProgressPanel.classList.add('hidden');
        transferProgressList.innerHTML = '';
        transferProgressSummary.textContent = '';
        if (previousWasFsProgress) {
          renderFsList();
        } else {
          renderGrid();
        }
        return;
      }

      if (fsProgress) {
        transferProgressPanel.classList.add('hidden');
        transferProgressList.innerHTML = '';
        transferProgressSummary.textContent = '';
        renderFsList();
        if (!previousWasFsProgress) {
          renderGrid();
        }
        return;
      }

      transferProgressPanel.classList.remove('hidden');
      transferProgressSummary.textContent = `${progress.label}: ${progress.completedCount}/${progress.totalCount} complete`;

      if (!progress.items || progress.items.length === 0) {
        transferProgressList.innerHTML = '<div class="empty">Preparing transfer list…</div>';
        return;
      }

      transferProgressList.innerHTML = '';
      for (const item of progress.items) {
        const row = document.createElement('div');
        row.className = 'tp-row';
        const percent = progressPercentForItem(item);
        const fillClasses = ['tp-fill', item.status];
        if (item.status === 'running' && (!item.totalBytes || item.totalBytes <= 0)) {
          fillClasses.push('indeterminate');
        }

        row.innerHTML = `
          <div class="tp-head">
            <div class="tp-name" title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</div>
            <div class="tp-status ${escapeHtml(item.status)}">${escapeHtml(statusTextForProgressItem(item))}</div>
          </div>
          <div class="tp-track">
            <div class="${fillClasses.join(' ')}" style="width:${percent.toFixed(2)}%"></div>
          </div>
          <div class="tp-path">${escapeHtml(item.remotePath)}</div>
        `;
        transferProgressList.appendChild(row);
      }

      if (previousWasFsProgress) {
        renderFsList();
      }

      renderGrid();
    }

    async function fetchTransferProgress(operationId) {
      if (!operationId) return;
      try {
        const response = await fetch(`/api/progress?operationId=${encodeURIComponent(operationId)}`);
        if (!response.ok) return;
        const progress = await response.json();
        renderTransferProgress(progress);
      } catch { }
    }

    function startTransferProgress(operationId, label) {
      stopTransferProgressPolling();
      state.transferProgressOperationId = operationId;
      renderTransferProgress({
        label,
        completedCount: 0,
        totalCount: 0,
        items: []
      });
      fetchTransferProgress(operationId);
      state.transferProgressTimer = window.setInterval(() => fetchTransferProgress(operationId), 350);
    }

    function stopTransferProgressPolling() {
      if (state.transferProgressTimer) {
        window.clearInterval(state.transferProgressTimer);
        state.transferProgressTimer = null;
      }
    }

    function openSettings() {
      parallelismInput.value = settings.parallelism;
      parallelismValue.textContent = settings.parallelism;
      settingAutoRefreshAfterTransfer.checked = Boolean(settings.autoRefreshAfterTransfer);
      settingExportOriginal.checked = settings.exportOriginalPhoto;
      settingExportEdited.checked = settings.exportEditedVersion;
      settingNamingOriginal.checked = settings.photoNaming === 'original';
      settingNamingDatetime.checked = settings.photoNaming === 'datetime';
      settingLiveStatic.checked = settings.livePhotoViewingMode === 'static';
      settingLiveDynamic.checked = settings.livePhotoViewingMode === 'dynamic';
      settingLiveImageVideo.checked = settings.livePhotoExportFiles === 'image+video';
      settingLiveImageOnly.checked = settings.livePhotoExportFiles === 'image';
      settingHeicJpg.checked = settings.heicExportFormat === 'jpg';
      settingHeicHeic.checked = settings.heicExportFormat === 'heic';
      settingsOverlay.classList.add('active');
      settingsCloseButton.focus();
    }

    function closeSettings() {
      settingsOverlay.classList.remove('active');
    }

    function openPreview(item) {
      if (!item) return;
      previewTitle.textContent = `Preview: ${item.name}`;
      previewBody.innerHTML = '';

      if (item.previewMode === 'video') {
        const video = document.createElement('video');
        video.controls = true;
        video.autoplay = true;
        video.preload = 'metadata';
        video.playsInline = true;
        video.src = item.previewUrl;
        previewBody.appendChild(video);
      } else if (item.previewMode === 'image') {
        const image = document.createElement('img');
        image.loading = 'eager';
        image.alt = item.name;
        image.src = item.previewUrl;
        image.addEventListener('error', () => {
          previewBody.innerHTML = '<div class="empty">Unable to preview this item.</div>';
        });
        previewBody.appendChild(image);
      } else {
        previewBody.innerHTML = '<div class="empty">Preview is not available for this file type.</div>';
      }

      previewOverlay.classList.add('active');
      previewCloseButton.focus();
    }

    function closePreview() {
      previewOverlay.classList.remove('active');
      previewBody.innerHTML = '';
    }

    function openCardContextMenu(item, x, y) {
      state.contextMenuItem = item;
      cardContextMenu.classList.remove('hidden');
      const menuWidth = 200;
      const menuHeight = 52;
      const left = Math.min(x, window.innerWidth - menuWidth - 8);
      const top = Math.min(y, window.innerHeight - menuHeight - 8);
      cardContextMenu.style.left = `${Math.max(8, left)}px`;
      cardContextMenu.style.top = `${Math.max(8, top)}px`;
    }

    function closeContextMenu() {
      state.contextMenuItem = null;
      cardContextMenu.classList.add('hidden');
    }

    async function loadQueue() {
      try {
        const response = await fetch('/api/queue');
        if (!response.ok) return;
        const items = await response.json();
        renderQueue(items);
      } catch { }
    }

    function renderQueue(items) {
      if (!items || items.length === 0) {
        queuePanel.classList.add('hidden');
        return;
      }

      queuePanel.classList.remove('hidden');
      queueTitle.textContent = `⚠ Failed Transfers (${items.length})`;
      queueList.innerHTML = '';

      for (const item of items) {
        const row = document.createElement('div');
        row.className = 'queue-item';

        const info = document.createElement('div');
        info.className = 'queue-item-info';
        const dateStr = item.queuedAt ? new Date(item.queuedAt).toLocaleString() : '';
        info.innerHTML = `
          <div><strong>${escapeHtml(item.remoteFilePath)}</strong></div>
          ${item.localPath ? `<div class="qi-path">→ ${escapeHtml(item.localPath)}</div>` : ''}
          <div class="qi-error">${escapeHtml(item.errorMessage)}</div>
          <div class="qi-meta">${escapeHtml(dateStr)}${item.operation ? ` · ${escapeHtml(item.operation)}` : ''}</div>
        `;

        const btns = document.createElement('div');
        btns.className = 'queue-item-btns';

        const retryBtn = document.createElement('button');
        retryBtn.textContent = '↺ Retry';
        retryBtn.addEventListener('click', () => retryQueueItems([item.id]));

        const removeBtn = document.createElement('button');
        removeBtn.className = 'danger';
        removeBtn.textContent = '✕';
        removeBtn.title = 'Remove from queue';
        removeBtn.addEventListener('click', () => removeQueueItem(item.id));

        btns.appendChild(retryBtn);
        btns.appendChild(removeBtn);
        row.appendChild(info);
        row.appendChild(btns);
        queueList.appendChild(row);
      }
    }

    async function retryQueueItems(ids) {
      setBusy(true);
      setStatus('Retrying failed transfers…');
      try {
        const response = await fetch('/api/queue/retry', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ids: ids ?? null })
        });
        const data = await response.json();
        if (!response.ok) throw new Error(data.message || 'Retry failed.');
        const note = data.failedItems && data.failedItems.length > 0
          ? ` (${data.failedItems.length} still failed)`
          : '';
        setStatus(data.message + note, data.failedItems && data.failedItems.length > 0);
        await loadQueue();
      } catch (error) {
        setStatus(error.message, true);
      } finally {
        setBusy(false);
      }
    }

    async function removeQueueItem(id) {
      try {
        await fetch(`/api/queue?id=${encodeURIComponent(id)}`, { method: 'DELETE' });
        await loadQueue();
      } catch { }
    }

    async function clearQueue() {
      if (!window.confirm('Clear all failed transfer items from the queue?')) return;
      try {
        await fetch('/api/queue', { method: 'DELETE' });
        await loadQueue();
      } catch { }
    }

    function setViewMode(mode) {
      state.viewMode = mode === 'filesystem' ? 'filesystem' : 'media';
      const fsMode = state.viewMode === 'filesystem';
      mediaView.classList.toggle('hidden', fsMode);
      fsView.classList.toggle('hidden', !fsMode);
      filterInput.classList.toggle('hidden', fsMode);
      scanDropdown.classList.toggle('hidden', fsMode);
      retryPtpButton.classList.toggle('hidden', fsMode || !state.ptpFallbackActive);
      copyButton.classList.toggle('hidden', fsMode);
      moveButton.classList.toggle('hidden', fsMode);
      fsCopyButton.classList.toggle('hidden', !fsMode);
      fsMoveButton.classList.toggle('hidden', !fsMode);
      fsDeleteButton.classList.toggle('hidden', !fsMode);
      fsRefreshButton.classList.toggle('hidden', !fsMode);
      fsUpButton.disabled = state.busy || !state.fsParentPath;

      if (fsMode && state.fsEntries.length === 0) {
        loadFs(state.fsCurrentPath);
        return;
      }

      setStatus(fsMode
        ? 'File system view: open a folder and select files or folders to copy, move, or delete.'
        : 'Media view: choose options and click Scan to start.');
    }

    function filteredItems() {
      if (state.filter === 'all') {
        return state.items;
      }

      return state.items.filter(item => item.kind === state.filter);
    }

    function chip(text) {
      return `<div class="chip">${text}</div>`;
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

    function renderFsSummary() {
      const folderCount = state.fsEntries.filter(entry => entry.isDirectory).length;
      const fileCount = state.fsEntries.filter(entry => entry.isFile).length;
      fsSummary.innerHTML = [
        chip(`Path: ${state.fsCurrentPath}`),
        chip(`${folderCount} folders`),
        chip(`${fileCount} files`),
        chip(`${state.fsSelected.size} selected`)
      ].join('');
      fsUpButton.disabled = state.busy || !state.fsParentPath;
    }

    function renderGrid() {
      const visible = filteredItems();
      const mediaProgressByPath = getMediaProgressByPath();

      if (visible.length === 0) {
        grid.innerHTML = '<div class="empty">No matching media was found. Click Scan to load items.</div>';
        return;
      }

      grid.innerHTML = '';

      for (const item of visible) {
        const selected = state.selected.has(item.id);
        const card = document.createElement('article');
        card.className = `card${selected ? ' selected' : ''}`;
        card.tabIndex = 0;
        card.addEventListener('click', () => toggleSelection(item.id));
        card.addEventListener('contextmenu', event => {
          event.preventDefault();
          openCardContextMenu(item, event.clientX, event.clientY);
        });
        card.addEventListener('dblclick', event => {
          event.preventDefault();
          openPreview(item);
        });
        card.addEventListener('keydown', event => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            toggleSelection(item.id);
          }
        });

        const livePhotoIcon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="20" height="20"><circle cx="12" cy="12" r="2.5" fill="white"/><circle cx="12" cy="12" r="7" fill="none" stroke="white" stroke-width="1.5"/><circle cx="12" cy="12" r="11" fill="none" stroke="white" stroke-width="1.5"/></svg>`;
        const timerIcon = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 14 14" width="12" height="12" fill="none" stroke="white" stroke-width="1.5" stroke-linecap="round"><circle cx="7" cy="7" r="6"/><polyline points="7 3.5 7 7 9.5 8.5"/></svg>`;
        const cachedVideo = item.previewMode === 'video' ? state.videoPreviewCache.get(item.id) : null;
        const progressItem = mediaProgressByPath.get(item.primaryRemotePath);
        const badgeMarkup = item.kind === 'live-photo' ? `<div class="badge">${livePhotoIcon}</div>` : '';
        const durationMarkup = item.kind === 'video' ? `<div class="duration">${timerIcon}<span class="dur-text">${cachedVideo?.durationText || '--:--'}</span></div>` : '';
        const progressMarkup = buildMediaCopyProgressMarkup(progressItem);
        const selectorText = selected ? '&#10003;' : '';
        const preview = item.previewMode === 'video'
          ? cachedVideo?.posterDataUrl
            ? `<img class="preview" loading="lazy" src="${cachedVideo.posterDataUrl}" alt="${escapeHtml(item.name)}">`
            : `<div class="video-preview"><div class="video-loading"><div class="video-spinner"></div></div><div class="fallback-icon hidden">▶</div></div>`
          : item.previewMode === 'image'
            ? `<div class="thumb-shell image-thumb"><img class="preview" loading="lazy" src="${item.previewUrl}" alt="${escapeHtml(item.name)}"><div class="thumb-loading"><div class="video-spinner"></div></div></div>`
            : buildImageFallbackMarkup(item);

        card.innerHTML = `
          ${preview}
          ${badgeMarkup}
          <div class="selector">${selectorText}</div>
          ${durationMarkup}
          ${progressMarkup}
          <div class="card-footer">
            <h2 class="card-title">${escapeHtml(item.name)}</h2>
            <p class="card-path">${escapeHtml(item.relativePath)}</p>
          </div>
        `;

        if (item.previewMode === 'video' && !cachedVideo) {
          enqueueVideoHydration(item, card);
        }

        const imageThumb = card.querySelector('.image-thumb');
        const image = imageThumb ? imageThumb.querySelector('img') : card.querySelector('img');
        if (image) {
          if (imageThumb) {
            const markLoaded = () => imageThumb.classList.add('loaded');
            image.addEventListener('load', markLoaded);
            if (image.complete && image.naturalWidth > 0) {
              markLoaded();
            }
          }

          image.addEventListener('error', () => {
            if (imageThumb) {
              imageThumb.classList.add('error');
              imageThumb.replaceWith(buildImageFallback(item));
              return;
            }

            image.replaceWith(buildImageFallback(item));
          });
        }

        grid.appendChild(card);
      }
    }

    function buildMediaCopyProgressMarkup(progressItem) {
      if (!progressItem) {
        return '';
      }

      if (progressItem.status !== 'running' && progressItem.status !== 'queued' && progressItem.status !== 'failed' && progressItem.status !== 'succeeded') {
        return '';
      }

      const percent = progressPercentForItem(progressItem);
      const barClasses = ['card-copy-progress-fill'];
      if ((progressItem.status === 'running' || progressItem.status === 'queued') && (!progressItem.totalBytes || progressItem.totalBytes <= 0)) {
        barClasses.push('indeterminate');
      }

      const containerClass = progressItem.status === 'failed' ? 'card-copy-progress failed' : 'card-copy-progress';
      return `<div class="${containerClass}" title="${escapeHtml(statusTextForProgressItem(progressItem))}"><div class="${barClasses.join(' ')}" style="width:${percent.toFixed(2)}%"></div></div>`;
    }

    function enqueueVideoHydration(item, card) {
      if (!item || state.videoHydrating.has(item.id) || state.videoPreviewCache.has(item.id)) {
        return;
      }

      state.videoHydrating.add(item.id);
      state.videoHydrationQueue.push({ item, card });
      pumpVideoHydrationQueue();
    }

    function pumpVideoHydrationQueue() {
      const maxConcurrent = 2;
      while (state.videoHydrationActive < maxConcurrent && state.videoHydrationQueue.length > 0) {
        const next = state.videoHydrationQueue.shift();
        state.videoHydrationActive += 1;
        hydrateVideoPreview(next.item, next.card)
          .catch(() => { })
          .finally(() => {
            state.videoHydrationActive -= 1;
            state.videoHydrating.delete(next.item.id);
            pumpVideoHydrationQueue();
          });
      }
    }

    async function hydrateVideoPreview(item, card) {
      const video = document.createElement('video');
      video.preload = 'auto';
      video.muted = true;
      video.playsInline = true;
      video.src = item.previewUrl;

      await new Promise((resolve, reject) => {
        const timeoutId = window.setTimeout(() => reject(new Error('Timed out loading video metadata.')), 15000);
        video.addEventListener('loadedmetadata', async () => {
          if (Number.isFinite(video.duration)) {
            const durationText = formatDuration(video.duration);
            const existing = state.videoPreviewCache.get(item.id) || {};
            state.videoPreviewCache.set(item.id, { ...existing, durationText });
            const duration = card.querySelector('.dur-text');
            if (duration) {
              duration.textContent = durationText;
            }
          }

          try {
            const posterDataUrl = await captureVideoPoster(video);
            if (posterDataUrl) {
              const existing = state.videoPreviewCache.get(item.id) || {};
              state.videoPreviewCache.set(item.id, { ...existing, posterDataUrl });
              applyVideoPosterToCard(card, item, posterDataUrl);
            } else {
              showVideoFallbackState(card);
            }
          } catch {
            showVideoFallbackState(card);
          }

          window.clearTimeout(timeoutId);
          resolve();
        }, { once: true });

        video.addEventListener('error', () => {
          window.clearTimeout(timeoutId);
          const duration = card.querySelector('.dur-text');
          if (duration && duration.textContent === '--:--') {
            duration.textContent = 'N/A';
          }
          showVideoFallbackState(card);
          reject(new Error('Failed to load video metadata.'));
        }, { once: true });
      });
    }

    function showVideoFallbackState(card) {
      const loading = card.querySelector('.video-loading');
      const fallback = card.querySelector('.fallback-icon');
      if (loading) {
        loading.classList.add('hidden');
      }
      if (fallback) {
        fallback.classList.remove('hidden');
        fallback.style.display = 'grid';
      }
    }

    async function captureVideoPoster(video) {
      const duration = Number.isFinite(video.duration) ? video.duration : 0;
      const timestamps = buildPosterCaptureTimestamps(duration);

      for (const timestamp of timestamps) {
        try {
          const poster = await captureFrameAtTime(video, timestamp);
          if (poster) {
            return poster;
          }
        } catch { }
      }

      return null;
    }

    function buildPosterCaptureTimestamps(duration) {
      const candidates = [0.45, 1.1, 2.0, duration * 0.2, duration * 0.4, duration * 0.65]
        .filter(value => Number.isFinite(value) && value >= 0);

      const upperBound = duration > 0.2 ? Math.max(0.1, duration - 0.05) : 0;
      const clamped = candidates
        .map(value => Math.min(value, upperBound))
        .filter(value => value >= 0);

      return [...new Set(clamped.map(value => Number(value.toFixed(2))))];
    }

    async function captureFrameAtTime(video, timestampSeconds) {
      await seekVideo(video, timestampSeconds);

      const targetSize = 360;
      const canvas = document.createElement('canvas');
      canvas.width = targetSize;
      canvas.height = targetSize;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) {
        return null;
      }

      const sourceWidth = video.videoWidth || targetSize;
      const sourceHeight = video.videoHeight || targetSize;
      const scale = Math.max(targetSize / sourceWidth, targetSize / sourceHeight);
      const drawWidth = sourceWidth * scale;
      const drawHeight = sourceHeight * scale;
      const offsetX = (targetSize - drawWidth) / 2;
      const offsetY = (targetSize - drawHeight) / 2;
      context.drawImage(video, offsetX, offsetY, drawWidth, drawHeight);

      if (isPosterLikelyBlack(canvas, context)) {
        return null;
      }

      return canvas.toDataURL('image/jpeg', 0.75);
    }

    function seekVideo(video, timestampSeconds) {
      return new Promise((resolve, reject) => {
        if (!Number.isFinite(timestampSeconds) || timestampSeconds < 0) {
          resolve();
          return;
        }

        const timeout = window.setTimeout(() => reject(new Error('Seek timed out.')), 5000);
        const onSeeked = () => {
          window.clearTimeout(timeout);
          video.removeEventListener('seeked', onSeeked);
          resolve();
        };

        video.addEventListener('seeked', onSeeked, { once: true });
        try {
          video.currentTime = timestampSeconds;
        } catch (error) {
          window.clearTimeout(timeout);
          video.removeEventListener('seeked', onSeeked);
          reject(error);
        }
      });
    }

    function isPosterLikelyBlack(canvas, context) {
      try {
        const { width, height } = canvas;
        const data = context.getImageData(0, 0, width, height).data;
        const pixelCount = width * height;
        if (!pixelCount || data.length < 4) {
          return true;
        }

        const step = Math.max(1, Math.floor(pixelCount / 1200));
        let sampled = 0;
        let luminanceSum = 0;

        for (let pixelIndex = 0; pixelIndex < pixelCount; pixelIndex += step) {
          const i = pixelIndex * 4;
          const r = data[i];
          const g = data[i + 1];
          const b = data[i + 2];
          luminanceSum += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
          sampled += 1;
        }

        if (sampled === 0) {
          return true;
        }

        const averageLuminance = luminanceSum / sampled;
        return averageLuminance < 20;
      } catch {
        return false;
      }
    }

    function applyVideoPosterToCard(card, item, posterDataUrl) {
      if (!posterDataUrl) {
        return;
      }

      const currentPreview = card.querySelector('.video-preview');
      if (!currentPreview) {
        return;
      }

      const image = document.createElement('img');
      image.className = 'preview';
      image.loading = 'lazy';
      image.alt = item.name;
      image.src = posterDataUrl;
      image.addEventListener('error', () => {
        image.replaceWith(buildImageFallback(item));
      });
      currentPreview.replaceWith(image);
    }

    function renderFsList() {
      if (state.fsEntries.length === 0) {
        fsList.innerHTML = '<div class="empty">No entries were found in this folder.</div>';
        return;
      }

      const progressByPath = getFsProgressByPath();
      fsList.innerHTML = '';
      for (const entry of state.fsEntries) {
        const row = document.createElement('div');
        row.className = 'fs-row';

        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = state.fsSelected.has(entry.path);
        checkbox.addEventListener('change', () => {
          if (checkbox.checked) {
            state.fsSelected.add(entry.path);
          } else {
            state.fsSelected.delete(entry.path);
          }
          renderFsSummary();
        });

        const info = document.createElement('div');
        info.innerHTML = `
          <div><strong>${escapeHtml(entry.name)}</strong></div>
          <div class="path">${escapeHtml(entry.path)}</div>
        `;

        const controls = document.createElement('div');
        controls.className = 'meta';
        const progressItem = progressByPath.get(entry.path);
        if (progressItem) {
          const statusBadge = document.createElement('div');
          statusBadge.className = `fs-inline-status ${progressItem.status}`;
          const statusText = statusTextForProgressItem(progressItem);
          statusBadge.textContent = statusText;
          statusBadge.title = statusText;
          controls.appendChild(statusBadge);
        }

        const metaText = document.createElement('div');
        metaText.textContent = entry.isDirectory
          ? 'Folder'
          : `${formatBytes(entry.sizeBytes ?? 0)}`;
        controls.appendChild(metaText);

        if (entry.isDirectory) {
          const openButton = document.createElement('button');
          openButton.textContent = 'Open';
          openButton.addEventListener('click', event => {
            event.preventDefault();
            loadFs(entry.path);
          });
          controls.appendChild(openButton);
        }

        row.appendChild(checkbox);
        row.appendChild(info);
        row.appendChild(controls);
        fsList.appendChild(row);
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

    async function loadMedia(forceRefresh = false) {
      // A new scan starts a fresh media view; clear stale transfer bars from previous operations.
      if (!state.transferProgressTimer) {
        state.transferProgressSnapshot = null;
        state.transferProgressOperationId = null;
      }

      setBusy(true);
      setStatus('Scanning media from the device…');

      try {
        const cacheBuster = Date.now();
        const response = await fetch(
          `/api/media?includeAdditionalRoots=${includeAdditionalRootsInput.checked}&forceRefresh=${forceRefresh}&_=${cacheBuster}`,
          { cache: 'no-store' }
        );
        const data = await response.json();

        if (!response.ok) {
          throw new Error(data.message || 'Unable to load media.');
        }

        state.items = data.items || [];
        state.selected.clear();
        renderSummary();
        renderGrid();
        state.ptpFallbackActive = Boolean(data.backendNote);
        retryPtpButton.classList.toggle('hidden', !state.ptpFallbackActive);
        setStatus(buildMediaLoadStatus(data));
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

    async function retryPtp() {
      try {
        await fetch('/api/ptp-retry', { method: 'POST' });
      } catch { }
      await loadMedia(true);
      await refreshPtpStatus();
    }

    async function scanAndRecache() {
      setBusy(true);
      setStatus('Resetting cache and rescanning media…');
      try {
        const response = await fetch('/api/cache/reset', { method: 'POST' });
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || 'Unable to reset cache.');
        }
        state.items = [];
        state.selected.clear();
        state.fsEntries = [];
        state.fsSelected.clear();
        renderGrid();
        renderSummary();
        renderFsList();
        renderFsSummary();
        state.ptpFallbackActive = false;
        retryPtpButton.classList.add('hidden');

        setBusy(false);
        await loadMedia(true);
        await refreshPtpStatus();
      } catch (error) {
        setStatus(error.message, true);
        setBusy(false);
      }
    }

    async function loadFs(path) {
      const requestedPath = (path || fsPathInput.value || 'DCIM').trim();
      if (!requestedPath) {
        setStatus('Enter a folder path to open.', true);
        fsPathInput.focus();
        return;
      }

      setBusy(true);
      setStatus(`Loading folder ${requestedPath}…`);

      try {
        const response = await fetch(`/api/fs?path=${encodeURIComponent(requestedPath)}`);
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || 'Unable to load folder.');
        }

        state.fsCurrentPath = data.currentPath;
        state.fsParentPath = data.parentPath;
        state.fsEntries = data.entries || [];
        state.fsSelected.clear();
        fsPathInput.value = data.currentPath;
        renderFsSummary();
        renderFsList();
        setStatus(`Loaded ${state.fsEntries.length} entries from ${data.currentPath}.`);
      } catch (error) {
        state.fsEntries = [];
        state.fsSelected.clear();
        renderFsSummary();
        renderFsList();
        setStatus(error.message, true);
      } finally {
        setBusy(false);
      }
    }

    async function transfer(operation) {
      if (state.selected.size === 0) {
        setStatus('Select at least one media item first.', true);
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
      setStatus(`${operation === 'move' ? 'Moving' : 'Copying'} selected media items…`);
      const operationId = createOperationId();
      startTransferProgress(operationId, `${operation === 'move' ? 'Move' : 'Copy'} media`);

      try {
        const response = await fetch('/api/transfer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            assetIds: Array.from(state.selected),
            destinationDirectory,
            operation,
            includeAdditionalRoots: includeAdditionalRootsInput.checked,
            parallelism: settings.parallelism,
            operationId,
            photoNaming: settings.photoNaming
          })
        });

        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || 'Transfer failed.');
        }

        const failCount = data.failedItems ? data.failedItems.length : 0;
        const note = failCount > 0 ? ` ⚠ ${failCount} file(s) failed — see queue below.` : '';
        setStatus(`${data.message} ${data.localPaths.length} file(s) written.${note}`, failCount > 0);
        await fetchTransferProgress(operationId);
        await loadQueue();

        const shouldAutoRefreshMedia = operation === 'move' || settings.autoRefreshAfterTransfer;
        if (shouldAutoRefreshMedia) {
          await loadMedia(true);
        }
      } catch (error) {
        setStatus(error.message, true);
        await fetchTransferProgress(operationId);
      } finally {
        stopTransferProgressPolling();
        setBusy(false);
      }
    }

    async function transferFs(operation) {
      if (state.fsSelected.size === 0) {
        setStatus('Select at least one file or folder first.', true);
        return;
      }

      const destinationDirectory = destinationInput.value.trim();
      if (operation !== 'delete' && !destinationDirectory) {
        setStatus('Enter an absolute destination directory path first.', true);
        destinationInput.focus();
        return;
      }

      if (operation === 'move' && !window.confirm('Move deletes the selected remote files and folders after hash-verified copy. Continue?')) {
        return;
      }

      if (operation === 'delete' && !window.confirm('Delete permanently removes the selected remote files and folders. Continue?')) {
        return;
      }

      setBusy(true);
      setStatus(`${operation[0].toUpperCase() + operation.slice(1)} selected file system items…`);
      const operationId = createOperationId();
      startTransferProgress(operationId, `${operation[0].toUpperCase() + operation.slice(1)} file system items`);

      try {
        const response = await fetch('/api/fs/transfer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            selectedPaths: Array.from(state.fsSelected),
            destinationDirectory: operation === 'delete' ? null : destinationDirectory,
            operation,
            parallelism: settings.parallelism,
            operationId
          })
        });

        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || 'File system transfer failed.');
        }

        const failCount = data.failedItems ? data.failedItems.length : 0;
        const countNote = operation === 'delete' ? '' : ` ${data.localPaths.length} file(s) written.`;
        const failNote = failCount > 0 ? ` ⚠ ${failCount} item(s) failed — see queue below.` : '';
        setStatus(`${data.message}${countNote}${failNote}`, failCount > 0);
        await fetchTransferProgress(operationId);
        await loadQueue();
        await loadFs(state.fsCurrentPath);
      } catch (error) {
        setStatus(error.message, true);
        await fetchTransferProgress(operationId);
      } finally {
        stopTransferProgressPolling();
        setBusy(false);
      }
    }

    function formatBytes(value) {
      const bytes = Number(value) || 0;
      if (bytes < 1024) return `${bytes} B`;
      if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
      if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
      return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
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
      return (value ?? '')
        .toString()
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
    }

    renderSummary();
    renderGrid();
    renderFsSummary();
    renderFsList();
    setViewMode('media');
    parallelismInput.value = settings.parallelism;
    parallelismValue.textContent = settings.parallelism;
    loadQueue();
    refreshPtpStatus();
    ptpStatusTimer = window.setInterval(refreshPtpStatus, 7000);
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
    public static extern int idevice_connect(IntPtr device, ushort port, ref IntPtr connection);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_disconnect(IntPtr connection);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_connection_enable_ssl(IntPtr connection);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_connection_disable_ssl(IntPtr connection);

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_connection_send(
        IntPtr connection,
        [In] byte[] data,
        uint len,
        ref uint sentBytes
    );

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_connection_receive(
        IntPtr connection,
        [Out] byte[] data,
        uint len,
        ref uint receivedBytes
    );

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
    public static extern int lockdownd_start_service_with_escrow_bag(
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

    public const int AFC_SEEK_SET = 0;

    [DllImport("imobiledevice-1.0", CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_file_seek(IntPtr afcClient, ulong handle, long offset, int whence);

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
