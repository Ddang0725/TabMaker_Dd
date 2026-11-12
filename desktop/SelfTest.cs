using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Reflection;

namespace TabMaker {
    internal static class SelfTest {
        static List<string> passed = new List<string>();
        static void Assert(bool condition, string name) { if(!condition) throw new Exception(name); passed.Add(name); }
        static void Pump(double seconds) {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer {Interval=TimeSpan.FromSeconds(seconds)};
            timer.Tick += delegate { timer.Stop(); frame.Continue=false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        public static int Run(string report,bool withAi=false) {
            string demo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"desktop-test-demo.wav");
            try {
                foreach(int midi in new[]{40,45,52,64,69,76,88}) {
                    float[] signal = new float[1000];
                    for(int i=0;i<signal.Length;i++) signal[i]=(float)(.4*Math.Sin(2*Math.PI*440*Math.Pow(2,(midi-69)/12.0)*i/8000));
                    Assert(Analysis.Pitch(signal,0)==midi,"Pitch MIDI "+midi);
                }
                Assert(Analysis.Pitch(new float[1000],0)==-1,"Silence");
                AudioDecoder.WriteDemo(demo);
                float[] decoded = AudioDecoder.Decode(demo,CancellationToken.None,delegate{});
                Assert(Math.Abs(decoded.Length/8000.0-5)<.01,"Native Media Foundation WAV decode and 8kHz conversion");
                List<Note> notes = Analysis.Run(decoded,0,5,Analysis.Tunings[0],24,CancellationToken.None,delegate{});
                Assert(string.Join(",",notes.Select(n=>n.Midi))=="64,67,69,71,69,67,64,62,64,67,71,76","Twelve demo notes in correct order");
                Assert(notes.All(n=>n.String>=0 && n.Fret>=0 && n.Fret<=24 && Analysis.Tunings[0][n.String]+n.Fret==n.Midi),"Playable positions");
                List<Note> extremes = new List<Note>{new Note{Midi=40},new Note{Midi=90},new Note{Midi=88}};
                Analysis.Finger(extremes,Analysis.Tunings[0],24);
                Assert(extremes[0].String==5 && extremes[0].Fret==0 && extremes[1].Fret==-1 && extremes[2].String==0 && extremes[2].Fret==24,"Fret range and out-of-range notes");
                List<Note> sub = Analysis.Run(decoded,1.6,1.2,Analysis.Tunings[0],24,CancellationToken.None,delegate{});
                Assert(sub.Count>0 && sub.All(n=>n.Start>=1.6 && n.End<=2.9),"Analysis offset uses original song timestamps");
                Assert(TabView.ActiveIndex(notes,notes[3].Start+.03)==3,"Forward playback highlight");
                Assert(TabView.ActiveIndex(notes,notes[0].Start+.03)==0,"Reverse seek highlight");
                Assert(TabView.ActiveIndex(notes,0)==-1 && TabView.ActiveIndex(notes,5)==-1,"No highlight outside notes");
                Assert(TabView.ActiveIndex(notes,notes[0].End)==-1,"Rest clears highlight");
                CancellationTokenSource cancel=new CancellationTokenSource(); cancel.Cancel();
                bool canceled=false;
                try { Analysis.Run(decoded,0,5,Analysis.Tunings[0],24,cancel.Token,delegate{}); } catch(OperationCanceledException) { canceled=true; }
                Assert(canceled,"Analysis cancellation"); cancel.Dispose();
                Application application = new Application();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Studio studio = new Studio();
                studio.Window.ShowInTaskbar=false;
                studio.Window.Opacity=0;
                studio.Window.Show();
                WavePlayer player = (WavePlayer)typeof(Studio).GetField("player",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(studio);
                player.Volume=0;
                studio.Open(demo);
                studio.Find<ComboBox>("InputMode").SelectedIndex=1;
                for(int i=0;i<40 && !studio.Find<Button>("PlayButton").IsEnabled;i++) Pump(.1);
                Assert(studio.Find<Button>("PlayButton").IsEnabled,"Native PCM player opens WAV: "+studio.Find<TextBlock>("Status").Text);
                System.Threading.Tasks.Task analysis=studio.Analyze();
                for(int i=0;i<300 && !analysis.IsCompleted;i++) Pump(.1);
                Assert(analysis.Status==System.Threading.Tasks.TaskStatus.RanToCompletion && studio.Tab.Notes.Count==12,"Desktop analyze button pipeline"+(analysis.Exception==null ? "" : analysis.Exception.ToString()));
                Assert(studio.Tab.Rhythm.Available && Math.Abs(studio.Tab.Rhythm.Bpm-150)<5,"Desktop BPM analysis");
                BeatMap map=new BeatMap {Bpm=120,Origin=.25,Beats=new List<double>{.25,.75,1.25,1.76,2.27},Meter=4,UseDetectedBeats=true};
                Assert(Math.Abs(map.TimeAt(map.BeatAt(1.55))-1.55)<.001,"Beat/time mapping round trip with tempo drift");
                Assert(map.BarAt(.25)==1 && map.BarAt(2.27)==2,"Beat-based measure boundaries");
                map.UseDetectedBeats=false;
                Assert(Math.Abs(map.TimeAt(4)-2.25)<1e-8,"Constant BPM ignores jitter in detected beats");
                map.Meter=6;
                Assert(Math.Abs(map.TimeAt(6)-1.75)<1e-8 && map.BarAt(1.75)==2,"6/8 has six eighth notes, not six quarter notes");
                Assert(Math.Abs(map.TimeAt(map.BeatAt(1.52))-1.52)<1e-8,"6/8 time conversion round trip");
                studio.Find<ComboBox>("Part").SelectedIndex=0;
                Assert(studio.Tab.Notes.Count>0,"Cached chord candidates selectable without inference");
                studio.Find<ComboBox>("Part").SelectedIndex=1;
                Assert(studio.Tab.Notes.Count>0 && studio.Tab.Notes.Zip(studio.Tab.Notes.Skip(1),(a,b)=>a.End<=b.Start+.00002).All(x=>x),"Cached lead notes are monophonic without inference");
                List<Note> chord=new List<Note>{new Note{Midi=52,Start=0,Duration=1},new Note{Midi=55,Start=0,Duration=1},new Note{Midi=59,Start=0,Duration=1}};
                Analysis.FingerPolyphonic(chord,Analysis.Tunings[0],24);
                Assert(chord.All(n=>n.String>=0) && chord.Select(n=>n.String).Distinct().Count()==3,"Chord fingering uses distinct strings");
                Assert(TabView.ActiveIndices(chord,.5).Count==3,"Simultaneous chord highlighting");
                var jsonMethod=typeof(Studio).GetMethod("JsonNotes",BindingFlags.Instance|BindingFlags.NonPublic);
                var exported=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)jsonMethod.Invoke(studio,null));
                Assert(exported.ContainsKey("beats") && exported.ContainsKey("firstBarSeconds") && Convert.ToDouble(exported["bpm"])>0,"JSON includes detected rhythm and first-bar basis");
                studio.Find<TextBox>("Bpm").Text="120";
                studio.Find<TextBox>("FirstBar").Text="0.25";
                studio.Find<Button>("ApplyRhythm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(studio.Tab.Rhythm.ManualTempo && studio.Tab.Rhythm.BarAt(2.26)==2,"Manual BPM and first measure correction");
                studio.Find<TextBox>("Bpm").Text="NaN";
                studio.Find<Button>("ApplyRhythm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(studio.Tab.Rhythm.Bpm==120,"Invalid manual BPM does not corrupt beat grid");
                studio.Find<TextBox>("Bpm").Text="120";
                studio.Seek(1.25);
                studio.Find<Button>("AlignFirstBar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(Math.Abs(studio.Tab.Rhythm.Origin-1.25)<.01 && studio.Tab.Rhythm.BarAt(1.26)==1,"Current playback position sets first measure");
                studio.Find<ComboBox>("Meter").SelectedIndex=2;
                studio.Find<Button>("ApplyRhythm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var sixExport=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)jsonMethod.Invoke(studio,null));
                var grid=((System.Collections.IEnumerable)sixExport["beats"]).Cast<object>().Select(x=>Convert.ToDouble(x)).ToArray();
                Assert(Convert.ToInt32(sixExport["beatUnit"])==8 && grid.Length>1 && Math.Abs(grid[1]-grid[0]-.25)<1e-8,"JSON exports effective eighth-note grid in 6/8");
                studio.Find<ComboBox>("Meter").SelectedIndex=0;
                studio.Find<TextBox>("Bpm").Text="130";
                Pump(.35);
                Assert(Math.Abs(studio.Tab.Rhythm.Bpm-130)<1e-9 && !studio.Tab.Rhythm.UseDetectedBeats,"Editing BPM updates GUI tempo without Apply button");
                BeatMap guiRhythm=new BeatMap {Bpm=130,Origin=0,Meter=4};
                double beatSeconds=60.0/130;
                Assert(guiRhythm.BeatInBar(beatSeconds-.0001)==1 && guiRhythm.BeatInBar(beatSeconds+.0001)==2 && guiRhythm.BarAt(4*beatSeconds+.0001)==2,"130 BPM GUI beat changes every 60/130 seconds and bar every four beats");
                TabView guiView=new TabView {Rhythm=guiRhythm,RangeStart=0,RangeEnd=200,BarsVisible=2,Follow=true};
                guiView.Update(2*beatSeconds); double xBefore=guiView.ScreenFraction(2*beatSeconds);
                guiView.Update(3*beatSeconds); double xAfter=guiView.ScreenFraction(3*beatSeconds);
                Assert(Math.Abs(xAfter-xBefore-.125)<1e-8,"GUI playhead advances one beat on a stationary two-bar page");
                guiView.Update(8*beatSeconds+.0001);
                Assert(guiView.ScreenFraction(8*beatSeconds+.0001)<.001,"GUI follows at the next measure-aligned page");
                studio.Find<TextBox>("Bpm").Text="120";
                studio.Find<TextBox>("FirstBar").Text="0.25";
                studio.Find<Button>("ApplyRhythm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(withAi) {
                    Transcription ai=AiEngine.Run(demo,0,5,true,true,CancellationToken.None,delegate{});
                    Assert((ai.Source=="demucs_6s_guitar" || ai.Source=="mel_band_roformer_guitar") && File.Exists(ai.StemPath),"Real separation inference via EXE backend, not note-accuracy validation");
                }
                studio.Seek(notes[3].Start+.03);
                studio.Find<Button>("PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double before=studio.Tab.Position;
                Pump(.25);
                Assert(studio.Tab.Position>before+.05,"Playback clock drives moving tab");
                studio.Find<Button>("PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double paused=studio.Tab.Position; Pump(.15);
                Assert(Math.Abs(studio.Tab.Position-paused)<.06,"Pause freezes playhead");
                studio.Find<System.Windows.Controls.ComboBox>("Speed").SelectedIndex=0;
                studio.Seek(.5);
                studio.Find<Button>("PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(.4);
                double slow=studio.Tab.Position-.5;
                Assert(slow>.08 && slow<.36,"Half-speed playback uses source-song clock");
                studio.Find<Button>("PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                studio.Find<System.Windows.Controls.ComboBox>("Speed").SelectedIndex=2;
                studio.Seek(notes[0].Start+.03); Pump(.05);
                Assert(TabView.ActiveIndex(studio.Tab.Notes,studio.Tab.Position)==0,"UI seek updates highlighted note");
                studio.Find<CheckBox>("Loop").IsChecked=true;
                studio.Seek(4.97);
                studio.Find<Button>("PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(.3);
                Assert(studio.Tab.Position<1,"Analyzed segment loops with song");
                studio.Find<Button>("PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                studio.Find<TextBox>("Start").Text="1.6";
                System.Threading.Tasks.Task offset=studio.Analyze();
                for(int i=0;i<300 && !offset.IsCompleted;i++) Pump(.1);
                Assert(offset.Status==System.Threading.Tasks.TaskStatus.RanToCompletion && studio.Tab.Notes.Count>0 && studio.Tab.Notes.All(n=>n.Start>=1.6),"AI backend segment offset remains in original song time");
                studio.Tab.Notes=chord; studio.Tab.RangeStart=0; studio.Tab.RangeEnd=5;
                string chordText=(string)typeof(Studio).GetMethod("TextTab",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(studio,null);
                Assert(chordText.Contains("BPM") && chordText.Contains("마디") && !chordText.Contains("undefined"),"TXT includes measure context and chord columns");
                studio.Tab.Notes=notes;
                studio.Tab.Update(notes[3].Start+.1);
                studio.Find<System.Windows.Controls.TextBlock>("FileLabel").Text="테스트 멜로디 · 5초";
                studio.Find<System.Windows.Controls.TextBlock>("Summary").Text="12개 노트 · 재생 위치에 맞춰 현재 음 강조";
                studio.Find<System.Windows.Controls.TextBlock>("CurrentNote").Text="B4 · 현재 프렛 강조";
                studio.Find<System.Windows.Controls.TextBlock>("Clock").Text="00:01.36 / 00:05.00";
                ((System.Windows.Controls.Grid)studio.Window.Content).Background=new SolidColorBrush(Color.FromRgb(246,245,241));
                FrameworkElement content=(FrameworkElement)studio.Window.Content;
                studio.Window.Close();
                studio.Window.Content=null;
                content.Measure(new Size(1220,850)); content.Arrange(new Rect(0,0,1220,850)); content.UpdateLayout();
                studio.Tab.InvalidateVisual(); content.UpdateLayout();
                RenderTargetBitmap bitmap=new RenderTargetBitmap(1220,850,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
                string screenshot=Path.ChangeExtension(report,"png");
                using(FileStream file=File.Create(screenshot)) { PngBitmapEncoder encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file); }
                Assert(File.Exists(screenshot),"WPF window and synchronized tab render");
                File.WriteAllText(report,"PASS\r\n"+string.Join("\r\n",passed),Encoding.UTF8); return 0;
            } catch(Exception e) {
                File.WriteAllText(report,"FAIL: "+e+"\r\nPassed:\r\n"+string.Join("\r\n",passed),Encoding.UTF8); return 1;
            } finally { if(File.Exists(demo)) File.Delete(demo); }
        }
    }
}
