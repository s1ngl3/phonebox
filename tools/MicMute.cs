using System;
using System.Runtime.InteropServices;

namespace PhoneBox.MicMute
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                // 1 = eCapture (microphone), 1 = eMultimedia
                int hr = en.GetDefaultAudioEndpoint(1, 1, out dev);
                if (hr != 0 || dev == null) return 1;

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object obj;
                hr = dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out obj);
                if (hr != 0 || obj == null) return 2;

                IAudioEndpointVolume ep = (IAudioEndpointVolume)obj;
                bool isMuted;
                ep.GetMute(out isMuted);

                Guid ctx = Guid.Empty;
                if (args.Length > 0 && string.Equals(args[0], "mute", StringComparison.OrdinalIgnoreCase))
                {
                    ep.SetMute(true, ref ctx);
                }
                else if (args.Length > 0 && string.Equals(args[0], "unmute", StringComparison.OrdinalIgnoreCase))
                {
                    ep.SetMute(false, ref ctx);
                }
                else
                {
                    ep.SetMute(!isMuted, ref ctx);
                }

                return 0;
            }
            catch
            {
                return 3;
            }
        }
    }
}
