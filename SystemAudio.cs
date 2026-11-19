using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace UtilMaster3000;

// ======================================================
// SYSTEM SOUND RECORDER
// Records whatever the PC is playing (WASAPI loopback of the default
// output device) into a WAV file. No extra libraries: plain COM calls.
//
// Windows delivers nothing while the PC is silent, so the file is kept
// in step with the wall clock by filling the gaps with silence. That way
// the sound stays in sync with the screen recording next to it.
// ======================================================

internal sealed class SystemAudioRecorder : IDisposable
{
    private readonly string path;
    private readonly Thread thread;
    private readonly ManualResetEventSlim started = new();
    private volatile bool stopRequested;

    public bool Working { get; private set; }

    public SystemAudioRecorder(string path)
    {
        this.path = path;

        thread = new Thread(Run) { IsBackground = true, Name = "System audio" };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Starts capturing; returns false when there is no sound device.</summary>
    public bool Start()
    {
        thread.Start();
        started.Wait(4000);

        return Working;
    }

    /// <summary>Stops and finishes the WAV file.</summary>
    public void Stop()
    {
        stopRequested = true;

        if (thread.IsAlive)
            thread.Join(5000);
    }

    public void Dispose() => Stop();

    private void Run()
    {
        object? deviceObject = null;
        object? clientObject = null;
        IntPtr format = IntPtr.Zero;
        FileStream? file = null;

        try
        {
            IMMDeviceEnumerator enumerator =
                (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            Marshal.ThrowExceptionForHR(
                enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice device));

            deviceObject = device;

            Guid clientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

            Marshal.ThrowExceptionForHR(
                device.Activate(ref clientId, 23, IntPtr.Zero, out clientObject));

            IAudioClient client = (IAudioClient)clientObject;

            Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));

            ushort channels = (ushort)Marshal.ReadInt16(format, 2);
            int sampleRate = Marshal.ReadInt32(format, 4);
            int bytesPerSecond = Marshal.ReadInt32(format, 8);
            int blockAlign = Marshal.ReadInt16(format, 12);
            int extra = Marshal.ReadInt16(format, 16);
            int formatSize = 18 + extra;

            byte[] formatBytes = new byte[formatSize];
            Marshal.Copy(format, formatBytes, 0, formatSize);

            Guid session = Guid.Empty;

            // Loopback: capture what the render device plays (1 s buffer).
            Marshal.ThrowExceptionForHR(
                client.Initialize(0, 0x00020000, 10_000_000, 0, format, ref session));

            Guid captureId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

            Marshal.ThrowExceptionForHR(
                client.GetService(ref captureId, out object captureObject));

            IAudioCaptureClient capture = (IAudioCaptureClient)captureObject;

            file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            WriteHeader(file, formatBytes, 0);

            Marshal.ThrowExceptionForHR(client.Start());

            Working = true;
            started.Set();

            Stopwatch clock = Stopwatch.StartNew();
            long written = 0;
            byte[] silence = new byte[blockAlign * 4800];

            void Pad(long target)
            {
                target -= target % blockAlign;

                while (written < target)
                {
                    int n = (int)Math.Min(silence.Length, target - written);
                    file.Write(silence, 0, n);
                    written += n;
                }
            }

            while (true)
            {
                bool last = stopRequested;

                while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (capture.GetBuffer(out IntPtr data, out uint frames,
                            out uint flags, out _, out _) != 0)
                    {
                        break;
                    }

                    int bytes = (int)frames * blockAlign;

                    // Fill any quiet stretch before this piece of sound.
                    long expected = (long)(clock.Elapsed.TotalSeconds * bytesPerSecond);
                    Pad(expected - bytes);

                    if ((flags & 2) != 0)
                    {
                        Pad(written + bytes);
                    }
                    else
                    {
                        byte[] chunk = new byte[bytes];
                        Marshal.Copy(data, chunk, 0, bytes);
                        file.Write(chunk, 0, bytes);
                        written += bytes;
                    }

                    capture.ReleaseBuffer(frames);
                }

                if (last)
                    break;

                Thread.Sleep(10);
            }

            // Same length as the time that passed.
            Pad((long)(clock.Elapsed.TotalSeconds * bytesPerSecond));

            client.Stop();
            WriteHeader(file, formatBytes, written);
        }
        catch (Exception)
        {
            Working = false;
        }
        finally
        {
            started.Set();

            if (format != IntPtr.Zero)
                Marshal.FreeCoTaskMem(format);

            file?.Dispose();

            if (clientObject != null) Marshal.ReleaseComObject(clientObject);
            if (deviceObject != null) Marshal.ReleaseComObject(deviceObject);
        }
    }

    // (Re)writes the WAV header with the real data length.
    private static void WriteHeader(FileStream file, byte[] format, long dataBytes)
    {
        file.Seek(0, SeekOrigin.Begin);

        using BinaryWriter w = new(file, System.Text.Encoding.ASCII, leaveOpen: true);

        w.Write("RIFF".ToCharArray());
        w.Write((uint)(4 + 8 + format.Length + 8 + dataBytes));
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write((uint)format.Length);
        w.Write(format);
        w.Write("data".ToCharArray());
        w.Write((uint)dataBytes);

        file.Seek(0, SeekOrigin.End);
    }

    // ---- Core Audio COM ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(
            ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(
            int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, ref Guid sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(
            int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(
            ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(
            out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
