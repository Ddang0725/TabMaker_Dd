using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TabMaker {
    internal sealed class AudioClip {
        public float[] Samples;
        public string PcmPath;
        public int Rate, Channels;
        public long Frames;
    }
    // Windows Media Foundation: decode using the codecs installed on the computer.
    internal static class AudioDecoder {
        [DllImport("mfplat.dll", ExactSpelling=true)] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling=true)] static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling=true)] static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfreadwrite.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetGuid(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetUInt(IntPtr self, ref Guid key, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetSelection(IntPtr self, int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetType(IntPtr self, int stream, IntPtr reserved, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMediaType(IntPtr self, int stream, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReadSample(IntPtr self, int stream, int flags, out int actual, out int resultFlags, out long timestamp, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBuffer(IntPtr self, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LockBuffer(IntPtr self, out IntPtr data, out int capacity, out int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UnlockBuffer(IntPtr self);

        static T Method<T>(IntPtr obj, int slot) where T : class {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T;
        }
        static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Marshal.Release(obj); }
        static void Set(IntPtr type, string key, string value) {
            Guid k = new Guid(key), v = new Guid(value);
            Check(Method<SetGuid>(type, 24)(type, ref k, ref v));
        }
        static int UInt(IntPtr type, string key) {
            Guid k = new Guid(key); int value;
            Check(Method<GetUInt>(type, 7)(type, ref k, out value)); return value;
        }
        public static float[] Decode(string path, CancellationToken token, Action<int> progress) {
            AudioClip clip=Load(path,token,progress);
            try { return clip.Samples; } finally { File.Delete(clip.PcmPath); }
        }
        public static AudioClip Load(string path, CancellationToken token, Action<int> progress) {
            const int Audio = unchecked((int)0xFFFFFFFD);
            IntPtr reader = IntPtr.Zero, desired = IntPtr.Zero, format = IntPtr.Zero;
            Check(MFStartup(0x20070, 0));
            string pcmPath=Path.Combine(Path.GetTempPath(),"TabMakerDd-"+Guid.NewGuid().ToString("N")+".pcm");
            BinaryWriter pcm=null; bool success=false;
            try {
                Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader));
                Check(Method<SetSelection>(reader, 4)(reader, unchecked((int)0xFFFFFFFE), false));
                Check(Method<SetSelection>(reader, 4)(reader, Audio, true));
                Check(MFCreateMediaType(out desired));
                Set(desired, "48eba18e-f8c9-4687-bf11-0a74c9f96a8f", "73647561-0000-0010-8000-00aa00389b71");
                Set(desired, "f7e34c9a-42e8-4714-b74b-cb29d72c35e5", "00000003-0000-0010-8000-00aa00389b71");
                Check(Method<SetType>(reader, 7)(reader, Audio, IntPtr.Zero, desired));
                Check(Method<GetMediaType>(reader, 6)(reader, Audio, out format));
                int rate = UInt(format, "5faeeae7-0290-4c31-9e8a-c534f68d9dba");
                int channels = UInt(format, "37e48bf5-645e-4c5b-89de-ada9e29b696a");
                if (rate < 8000 || channels < 1 || channels > 32) throw new InvalidDataException("지원하지 않는 오디오 형식입니다.");
                int outputChannels=Math.Min(2,channels);
                long nativeFrames=0;
                pcm=new BinaryWriter(File.Create(pcmPath));
                List<float> mono = new List<float>();
                int phase = 0, count = 0; double total = 0;
                while (true) {
                    token.ThrowIfCancellationRequested();
                    int actual, flags; long timestamp; IntPtr sample;
                    Check(Method<ReadSample>(reader, 9)(reader, Audio, 0, out actual, out flags, out timestamp, out sample));
                    try {
                        if ((flags & 0x20) != 0) throw new InvalidDataException("중간에 형식이 바뀌는 오디오는 지원하지 않습니다.");
                        if (sample != IntPtr.Zero) {
                            IntPtr block = IntPtr.Zero;
                            try {
                                Check(Method<GetBuffer>(sample, 41)(sample, out block));
                                IntPtr ptr; int capacity, length;
                                Check(Method<LockBuffer>(block, 3)(block, out ptr, out capacity, out length));
                                float[] values;
                                try { values = new float[length / 4]; Marshal.Copy(ptr, values, 0, values.Length); }
                                finally { Check(Method<UnlockBuffer>(block, 4)(block)); }
                                for (int i = 0; i + channels <= values.Length; i += channels) {
                                    double value = 0;
                                    for (int c = 0; c < channels; c++) value += values[i + c];
                                    total += value / channels; count++; phase += 8000;
                                    for(int c=0;c<outputChannels;c++) {
                                        double channel=channels>2 ? value/channels : values[i+c];
                                        pcm.Write((short)(Math.Max(-1,Math.Min(1,channel))*32767));
                                    }
                                    nativeFrames++;
                                    if (phase >= rate) {
                                        mono.Add((float)(total / count)); phase -= rate; count = 0; total = 0;
                                    }
                                }
                            } finally { Release(block); }
                        }
                        if (mono.Count > 8000 * 60 * 30) throw new InvalidDataException("30분 이하의 음원을 선택해주세요.");
                        if ((flags & 2) != 0) break;
                    } finally { Release(sample); }
                }
                if (mono.Count < 1200) throw new InvalidDataException("분석 가능한 음원이 너무 짧습니다.");
                progress(25); success=true;
                return new AudioClip {Samples=mono.ToArray(),PcmPath=pcmPath,Rate=rate,Channels=outputChannels,Frames=nativeFrames};
            } finally {
                if(pcm!=null) pcm.Dispose();
                if(!success && File.Exists(pcmPath)) File.Delete(pcmPath);
                Release(format); Release(desired); Release(reader); MFShutdown();
            }
        }
        public static void WriteDemo(string path) {
            const int rate = 44100; int[] melody = {64,67,69,71,69,67,64,62,64,67,71,76};
            int frames = rate * 5;
            using (BinaryWriter w = new BinaryWriter(File.Create(path))) {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + frames * 2);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(frames * 2);
                for (int i = 0; i < frames; i++) {
                    double time = i / (double)rate - 0.05, v = 0;
                    int note = (int)Math.Floor(time / 0.4); double t = time - note * 0.4;
                    if (time >= 0 && note < melody.Length && t < 0.34) {
                        double f = 440 * Math.Pow(2, (melody[note] - 69) / 12.0);
                        double e = Math.Min(1, t / 0.008) * Math.Min(1, (0.34 - t) / 0.035) * Math.Exp(-t * 2);
                        v = e * (0.45 * Math.Sin(2 * Math.PI * f * t) + 0.14 * Math.Sin(4 * Math.PI * f * t));
                    }
                    w.Write((short)(v * 32767));
                }
            }
        }
    }
}
