using System.Runtime.InteropServices;
using OmniWin.Core;

namespace OmniWin.SystemTools;

internal sealed class NoAudioDeviceException(string message) : Exception(message);

internal interface IAudioBackend
{
    /// <summary>Reads the default output device. Throws <see cref="NoAudioDeviceException"/> when there is none.</summary>
    AudioState Get();

    void SetVolume(int percent);

    void SetMute(bool muted);
}

/// <summary>Windows Core Audio (default render endpoint). COM objects are created and used on the calling thread.</summary>
internal sealed class CoreAudioBackend : IAudioBackend
{
    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public AudioState Get()
    {
        var (device, volume) = Open();
        try
        {
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            return new AudioState((int)Math.Round(level * 100), muted, FriendlyName(device));
        }
        finally
        {
            Release(volume, device);
        }
    }

    public void SetVolume(int percent)
    {
        var (device, volume) = Open();
        try
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context));
        }
        finally
        {
            Release(volume, device);
        }
    }

    public void SetMute(bool muted)
    {
        var (device, volume) = Open();
        try
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMute(muted, ref context));
        }
        finally
        {
            Release(volume, device);
        }
    }

    private static (IMMDevice Device, IAudioEndpointVolume Volume) Open()
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            if (hr != 0 || device is null) throw new NoAudioDeviceException("There is no default audio output device.");
            var iid = AudioEndpointVolumeId;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var volumeObject);
            return (device, (IAudioEndpointVolume)volumeObject);
        }
        finally
        {
            Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static string FriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out var store);
            try
            {
                var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
                store.GetValue(ref key, out var value);
                try
                {
                    return value.Vt == 31 /* VT_LPWSTR */ && value.Pointer != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.Pointer) ?? "unknown device"
                        : "unknown device";
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(store);
            }
        }
        catch (COMException)
        {
            return "unknown device";
        }
    }

    private static void Release(IAudioEndpointVolume volume, IMMDevice device)
    {
        Marshal.FinalReleaseComObject(volume);
        Marshal.FinalReleaseComObject(device);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        void OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);

        void GetAt(uint index, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig] int GetChannelCount(out uint count);

        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);

        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);

        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);

        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
