using System.Runtime.InteropServices;

if (args.Length < 2 || args.Length > 3)
{
    Console.WriteLine("Usage: AppleFileConduitDemo <remoteFilePath> <localOutputPath> [deviceUdid]");
    return 1;
}

string remotePath = args[0];
string localPath = args[1];
string? udid = args.Length == 3 ? args[2] : null;

IntPtr device = IntPtr.Zero;
IntPtr lockdowndClient = IntPtr.Zero;
IntPtr serviceDescriptor = IntPtr.Zero;
IntPtr afcClient = IntPtr.Zero;
ulong afcFileHandle = 0;

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

    ThrowIfError(
        NativeMethods.afc_file_open(afcClient, remotePath, NativeMethods.AFC_FOPEN_RDONLY, ref afcFileHandle),
        $"Unable to open remote file '{remotePath}'"
    );

    string? localDirectory = Path.GetDirectoryName(localPath);
    if (!string.IsNullOrEmpty(localDirectory))
    {
        Directory.CreateDirectory(localDirectory);
    }

    using FileStream output = File.Create(localPath);
    byte[] buffer = new byte[64 * 1024];

    while (true)
    {
        uint bytesRead = 0;
        ThrowIfError(NativeMethods.afc_file_read(afcClient, afcFileHandle, buffer, (uint)buffer.Length, ref bytesRead), "Failed while reading remote file");

        if (bytesRead == 0)
        {
            break;
        }

        output.Write(buffer, 0, (int)bytesRead);
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
    if (afcFileHandle != 0 && afcClient != IntPtr.Zero)
    {
        NativeMethods.afc_file_close(afcClient, afcFileHandle);
    }

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
}
