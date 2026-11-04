using System.Runtime.InteropServices;
using HomeTV.Core.Platform;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Playback devices through Windows' Core Audio API. Windows has no public API for changing the default device;
/// <c>IPolicyConfig</c> is what the Sound settings page itself uses, and what tools like EarTrumpet and SoundSwitch
/// rely on. It has been stable since Windows 7.
/// </summary>
public sealed class WindowsAudioDevices : IAudioDevices
{
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int DeviceStateActive = 0x1;
    private const int DeviceStateUnplugged = 0x4;
    private const int StgmRead = 0;

    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public IReadOnlyList<AudioDevice> List()
    {
        var enumerator = CreateEnumerator();
        try
        {
            // Unplugged too, so a Bluetooth speaker that's out of range still shows up (as not connected).
            Check(enumerator.EnumAudioEndpoints(ERender, DeviceStateActive | DeviceStateUnplugged, out var collection));
            try
            {
                Check(collection.GetCount(out var count));
                var result = new List<AudioDevice>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    Check(collection.Item(i, out var device));
                    try
                    {
                        Check(device.GetId(out var id));
                        Check(device.GetState(out var state));
                        result.Add(new AudioDevice(id, ReadName(device) ?? id, (state & DeviceStateActive) != 0));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
                return result;
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public string? DefaultId()
    {
        var enumerator = CreateEnumerator();
        try
        {
            // E_NOTFOUND when there's no playback device at all.
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0)
                return null;
            try
            {
                return device.GetId(out var id) == 0 ? id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public void SetDefault(string id)
    {
        var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;
        try
        {
            // Console (games, system sounds), Multimedia (video, music) and Communications (calls): all of them.
            for (var role = 0; role < 3; role++)
                Check(policy.SetDefaultEndpoint(id, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("bcde0395-e52f-467c-8e3d-c4579291692e"))!)!;

    private static string? ReadName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0)
            return null;
        try
        {
            var key = FriendlyName;
            if (store.GetValue(ref key, out var value) != 0)
                return null;
            try
            {
                const ushort VtLpwstr = 31;
                return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Data) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, int id)
    {
        public readonly Guid Format = format;
        public readonly int Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Data;
        public IntPtr Data2;
    }

    // Methods are listed in vtable order; the ones HomeTV never calls take placeholder parameters.

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}
