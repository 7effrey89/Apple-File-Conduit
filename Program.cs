using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Security.Cryptography;

bool deleteSourceAfterCopy = false;
bool listRemotePath = false;
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
}

string remotePath;
string? localPath = null;
string? udid;

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

IntPtr device = IntPtr.Zero;
IntPtr lockdowndClient = IntPtr.Zero;
IntPtr serviceDescriptor = IntPtr.Zero;
IntPtr afcClient = IntPtr.Zero;

try
{
    ThrowIfError(NativeMethods.idevice_new(ref device, udid), "Unable to connect to iOS device");

    ThrowIfError(
        NativeMethods.lockdownd_client_new_with_handshake(device, ref lockdowndClient, "AppleFileConduitDemo"),
        "Unable to start lockdownd session"
    );

    ThrowIfError(
        NativeMethods.lockdownd_start_service(lockdowndClient, "com.apple.afc", ref serviceDescriptor),
        "Unable to start AFC service (is the device trusted and unlocked?)"
    );

    ThrowIfError(NativeMethods.afc_client_new(device, serviceDescriptor, ref afcClient), "Unable to create AFC client");

    if (listRemotePath)
    {
        ListRemoteDirectory(afcClient, remotePath);
        return 0;
    }

    if (localPath is null)
    {
        throw new InvalidOperationException("Local output path is required for copy and move modes.");
    }

    byte[] sourceHash = CopyRemoteFileToLocalAndComputeHash(afcClient, remotePath, localPath);

    if (deleteSourceAfterCopy)
    {
        byte[] localHash = ComputeLocalFileHash(localPath);

        if (!CryptographicOperations.FixedTimeEquals(sourceHash, localHash))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch for '{remotePath}' and '{localPath}'. Remote source file was not deleted."
            );
        }

        ThrowIfError(NativeMethods.afc_remove_path(afcClient, remotePath), $"Unable to delete remote file '{remotePath}'");
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
finally
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
}

static void WriteUsage()
{
    Console.WriteLine("Usage:");
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

static void ThrowIfError(int errorCode, string message)
{
    if (errorCode != 0)
    {
        throw new InvalidOperationException($"{message}. Native error code: {errorCode}");
    }
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
