using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TabMaker {
    internal sealed class AudioFailureEventArgs : EventArgs {
        public Exception ErrorException;
        public AudioFailureEventArgs(Exception e) { ErrorException=e; }
    }
    // PCM queue with the hardware playback position as the clock. No WMP dependency.
    internal sealed class WavePlayer {
        [StructLayout(LayoutKind.Sequential,Pack=2)] struct Format {
            public ushort Tag, Channels;
            public uint Rate, BytesPerSecond;
            public ushort Align, Bits, Extra;
        }
        [StructLayout(LayoutKind.Sequential)] struct Header {
            public IntPtr Data;
            public uint Length, Recorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }
        [StructLayout(LayoutKind.Explicit,Size=12)] struct Clock {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Value;
        }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr device,uint id,ref Format format,IntPtr callback,IntPtr instance,uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr device,IntPtr header,uint size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr device,IntPtr header,uint size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr device,IntPtr header,uint size);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr device);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr device);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr device);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr device,ref Clock clock,uint size);
        sealed class Block { public IntPtr Data, Header; public bool Queued, Prepared; }
        AudioClip clip;
        FileStream pcm;
        IntPtr device;
        List<Block> blocks=new List<Block>();
        DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(12)};
        CancellationTokenSource loading;
        Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
        double origin, readFrame, speed=1;
        bool playing;
        public double Volume=1;
        public event EventHandler MediaOpened, MediaEnded;
        public event EventHandler<AudioFailureEventArgs> MediaFailed;
        public float[] Samples { get { return clip==null ? null : clip.Samples; } }
        public Duration NaturalDuration { get { return new Duration(TimeSpan.FromSeconds(clip==null ? 0 : clip.Frames/(double)clip.Rate)); } }
        static uint HeaderSize { get { return (uint)Marshal.SizeOf(typeof(Header)); } }
        public WavePlayer() { timer.Tick+=delegate { Pump(); }; }
        static void Check(int result) { if(result!=0) throw new IOException("Windows 오디오 출력 오류 ("+result+"). 출력 장치를 확인해주세요."); }
        public void Open(Uri uri) {
            Close();
            CancellationTokenSource current=new CancellationTokenSource(); loading=current;
            Task<AudioClip> task=Task.Run(delegate { return AudioDecoder.Load(uri.LocalPath,current.Token,delegate{}); });
            task.ContinueWith(delegate(Task<AudioClip> completed) {
                dispatcher.BeginInvoke(new Action(delegate {
                    if(loading!=current) {
                        if(completed.Status==TaskStatus.RanToCompletion) File.Delete(completed.Result.PcmPath);
                        current.Dispose(); return;
                    }
                    loading=null; current.Dispose();
                    if(completed.IsCanceled) return;
                    if(completed.IsFaulted) { Fail(completed.Exception.GetBaseException()); return; }
                    try {
                        clip=completed.Result; pcm=File.OpenRead(clip.PcmPath);
                        Format format=new Format {Tag=1,Channels=(ushort)clip.Channels,Rate=(uint)clip.Rate,BytesPerSecond=(uint)(clip.Rate*clip.Channels*2),Align=(ushort)(clip.Channels*2),Bits=16};
                        Check(waveOutOpen(out device,uint.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0));
                        for(int i=0;i<3;i++) blocks.Add(new Block {Data=Marshal.AllocHGlobal(2048*clip.Channels*2),Header=Marshal.AllocHGlobal((int)HeaderSize)});
                        timer.Start();
                        if(MediaOpened!=null) MediaOpened(this,EventArgs.Empty);
                    } catch(Exception e) { Close(); Fail(e); }
                }));
            });
        }
        void Fail(Exception e) { if(MediaFailed!=null) MediaFailed(this,new AudioFailureEventArgs(e)); }
        public TimeSpan Position {
            get {
                if(clip==null || device==IntPtr.Zero) return TimeSpan.Zero;
                Clock clock=new Clock {Type=2}; // TIME_BYTES, supported by every wave device.
                if(waveOutGetPosition(device,ref clock,12)!=0) return TimeSpan.FromSeconds(origin);
                double seconds=clock.Type==2 ? clock.Value/(double)(clip.Rate*clip.Channels*2) : clock.Type==4 ? clock.Value/(double)clip.Rate : clock.Value/1000.0;
                return TimeSpan.FromSeconds(Math.Min(clip.Frames/(double)clip.Rate,origin+seconds*speed));
            }
            set { Reset(Math.Max(0,Math.Min(NaturalDuration.TimeSpan.TotalSeconds,value.TotalSeconds))); }
        }
        public double SpeedRatio {
            get { return speed; }
            set { double position=Position.TotalSeconds; speed=Math.Max(.25,Math.Min(2,value)); Reset(position); }
        }
        void Reset(double position) {
            if(device==IntPtr.Zero || clip==null) return;
            Check(waveOutReset(device));
            foreach(Block block in blocks) {
                if(block.Prepared) { Check(waveOutUnprepareHeader(device,block.Header,HeaderSize)); block.Prepared=false; }
                block.Queued=false;
            }
            origin=position; readFrame=position*clip.Rate;
            if(!playing) Check(waveOutPause(device));
            Fill();
            if(playing) Check(waveOutRestart(device));
        }
        public void Play() { if(device==IntPtr.Zero) return; playing=true; Fill(); Check(waveOutRestart(device)); }
        public void Pause() { if(device==IntPtr.Zero) return; Check(waveOutPause(device)); playing=false; }
        void Fill() {
            if(clip==null || device==IntPtr.Zero) return;
            foreach(Block block in blocks) {
                if(block.Queued) {
                    Header queued=(Header)Marshal.PtrToStructure(block.Header,typeof(Header));
                    if((queued.Flags&1)==0) continue;
                    Check(waveOutUnprepareHeader(device,block.Header,HeaderSize)); block.Prepared=false; block.Queued=false;
                }
                if(readFrame>=clip.Frames) continue;
                int count=(int)Math.Min(2048,Math.Ceiling((clip.Frames-readFrame)/speed));
                int align=clip.Channels*2;
                long first=(long)readFrame;
                int sourceCount=(int)Math.Min(clip.Frames-first,Math.Ceiling(count*speed)+2);
                byte[] source=new byte[sourceCount*align];
                pcm.Position=first*align; int read=0;
                while(read<source.Length) { int n=pcm.Read(source,read,source.Length-read); if(n==0) throw new EndOfStreamException(); read+=n; }
                byte[] output=new byte[count*align];
                for(int j=0;j<count;j++) {
                    double location=readFrame-first+j*speed;
                    int frame=Math.Min(sourceCount-1,(int)location), next=Math.Min(sourceCount-1,frame+1);
                    double blend=location-frame;
                    for(int c=0;c<clip.Channels;c++) {
                        short a=BitConverter.ToInt16(source,frame*align+c*2), b=BitConverter.ToInt16(source,next*align+c*2);
                        short value=(short)((a+(b-a)*blend)*Math.Max(0,Math.Min(1,Volume)));
                        output[j*align+c*2]=(byte)(value&255); output[j*align+c*2+1]=(byte)(value>>8);
                    }
                }
                Marshal.Copy(output,0,block.Data,output.Length);
                Header header=new Header {Data=block.Data,Length=(uint)output.Length};
                Marshal.StructureToPtr(header,block.Header,false);
                Check(waveOutPrepareHeader(device,block.Header,HeaderSize)); block.Prepared=true;
                Check(waveOutWrite(device,block.Header,HeaderSize)); block.Queued=true;
                readFrame+=count*speed;
            }
        }
        void Pump() {
            if(!playing) return;
            try {
                Fill();
                bool queued=false; foreach(Block block in blocks) if(block.Queued) queued=true;
                if(!queued && clip!=null && readFrame>=clip.Frames) {
                    playing=false;
                    if(MediaEnded!=null) MediaEnded(this,EventArgs.Empty);
                }
            } catch(Exception e) { playing=false; if(device!=IntPtr.Zero) waveOutReset(device); Fail(e); }
        }
        public void Close() {
            if(loading!=null) { loading.Cancel(); loading=null; }
            timer.Stop(); playing=false;
            if(device!=IntPtr.Zero) {
                waveOutReset(device);
                foreach(Block block in blocks) {
                    if(block.Prepared) waveOutUnprepareHeader(device,block.Header,HeaderSize);
                    Marshal.FreeHGlobal(block.Header); Marshal.FreeHGlobal(block.Data);
                }
                blocks.Clear(); waveOutClose(device); device=IntPtr.Zero;
            }
            if(pcm!=null) { pcm.Dispose(); pcm=null; }
            if(clip!=null) { try { File.Delete(clip.PcmPath); } catch(IOException) {} clip=null; }
            origin=0; readFrame=0;
        }
    }
}
