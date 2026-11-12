using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Web.Script.Serialization;

namespace TabMaker {
    public class Studio {
        public Window Window;
        public TabView Tab = new TabView();
        WavePlayer player = new WavePlayer();
        DispatcherTimer timer = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(16)};
        DispatcherTimer rhythmInputTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        CancellationTokenSource cancellation;
        string path = "";
        bool ready, playing, updatingSlider, dragging, closed;
        double duration, analysisStart, analysisEnd;
        int resultTuning, resultFrets;
        string stemPath="", resultSource="";
        double detectedBpm;
        List<Note> candidateNotes=new List<Note>(), leadNotes=new List<Note>();
        public T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        public Studio() {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            Find<Grid>("TabHost").Children.Add(Tab);
            Button("OpenButton").Click += delegate {
                OpenFileDialog dialog = new OpenFileDialog {Filter="음원 / Audio|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.ogg|모든 파일|*.*"};
                if (dialog.ShowDialog(Window) == true) Open(dialog.FileName);
            };
            Button("DemoButton").Click += delegate {
                try { string demo = Path.Combine(Path.GetTempPath(), "TabMakerDd-demo.wav"); AudioDecoder.WriteDemo(demo); Open(demo); }
                catch (Exception e) { Status("샘플 파일을 만들지 못했어요: " + e.Message); }
            };
            Button("AnalyzeButton").Click += async delegate { await Analyze(); };
            Button("CancelButton").Click += delegate { if(cancellation != null) cancellation.Cancel(); };
            Button("PlayButton").Click += delegate { TogglePlayback(); };
            Button("RestartButton").Click += delegate { Seek(analysisStart); };
            Button("TextExport").Click += delegate { Export(false); };
            Button("JsonExport").Click += delegate { Export(true); };
            Button("StemExport").Click += delegate { ExportStem(); };
            Button("ApplyRhythm").Click += delegate { ApplyRhythm(); };
            Button("TempoHalf").Click += delegate { ScaleTempo(.5); };
            Button("TempoDouble").Click += delegate { ScaleTempo(2); };
            rhythmInputTimer.Tick+=delegate { rhythmInputTimer.Stop(); if(cancellation==null && ready) ApplyRhythm(); };
            Find<TextBox>("Bpm").TextChanged+=delegate {
                if(cancellation!=null || !ready || string.IsNullOrWhiteSpace(Find<TextBox>("Bpm").Text)) return;
                // The visible BPM field controls the GUI tempo, including tiny edits.
                Combo("GridMode").SelectedIndex=0;
                rhythmInputTimer.Stop(); rhythmInputTimer.Start();
            };
            Combo("Meter").SelectionChanged+=delegate { if(cancellation==null && ready && Tab.Rhythm.Available) ApplyRhythm(); };
            Combo("GridMode").SelectionChanged+=delegate { if(cancellation==null && ready && Tab.Rhythm.Available) ApplyRhythm(); };
            Button("AlignFirstBar").Click += delegate {
                if(!ready) return;
                player.Pause(); playing=false; Button("PlayButton").Content="▶ 재생";
                Find<TextBox>("FirstBar").Text=player.Position.TotalSeconds.ToString("0.000000",CultureInfo.InvariantCulture);
                ApplyRhythm();
            };
            Combo("Part").SelectionChanged += delegate {
                if(candidateNotes.Count==0 || cancellation!=null) return;
                Tab.Notes=Combo("Part").SelectedIndex==0 ? candidateNotes : leadNotes;
                if(Combo("Part").SelectedIndex==0) Analysis.FingerPolyphonic(Tab.Notes,Analysis.Tunings[resultTuning],resultFrets);
                else Analysis.Finger(Tab.Notes,Analysis.Tunings[resultTuning],resultFrets);
                Find<TextBlock>("Summary").Text=Tab.Notes.Count+"개 추정 노트 · "+(Combo("Part").SelectedIndex==0 ? "코드 후보" : "단음 멜로디 후보")+" · 실제 기타 파트인지 확인해주세요.";
                Tick();
            };
            Combo("Speed").SelectionChanged += delegate { if(ready) player.SpeedRatio = Selected(Combo("Speed")); };
            Combo("Speed").ToolTip="재생 속도를 바꾸면 음 높이도 함께 달라집니다.";
            Combo("Zoom").SelectionChanged += delegate { Tab.BarsVisible=(int)Selected(Combo("Zoom")); Tab.SecondsVisible=Tab.BarsVisible*5; Tab.Update(player.Position.TotalSeconds); };
            Find<CheckBox>("Follow").Checked += delegate { Tab.Follow = true; Tab.Update(player.Position.TotalSeconds); };
            Find<CheckBox>("Follow").Unchecked += delegate { Tab.Follow = false; };
            Tab.PreviewMouseWheel += delegate { Find<CheckBox>("Follow").IsChecked = false; };
            Tab.Seek += Seek;
            Slider slider = Find<Slider>("Timeline");
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate { dragging = true; }));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate { dragging = false; Seek(slider.Value); }));
            slider.ValueChanged += delegate { if (!updatingSlider && !dragging && ready) Seek(slider.Value); };
            player.MediaOpened += delegate {
                if (closed) return;
                ready = true; duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
                slider.Maximum = Math.Max(1,duration); slider.IsEnabled = duration > 0;
                Button("PlayButton").IsEnabled = true; Button("AnalyzeButton").IsEnabled = true;
                player.SpeedRatio = Selected(Combo("Speed"));
                Find<TextBlock>("FileLabel").Text = Path.GetFileName(path) + "\n" + Format(duration);
                Status("준비됐어요. 타브를 만든 뒤 재생해보세요."); Tick();
            };
            player.MediaFailed += delegate(object sender, AudioFailureEventArgs e) {
                ready = false; playing = false; Button("PlayButton").Content = "▶ 재생";
                Button("PlayButton").IsEnabled = false; Button("AnalyzeButton").IsEnabled = false;
                slider.IsEnabled = false;
                Status("음원을 열 수 없어요. WAV 또는 MP3로 다시 시도해주세요. " + e.ErrorException.Message);
            };
            player.MediaEnded += delegate {
                if (Find<CheckBox>("Loop").IsChecked == true && Tab.Notes.Count > 0) { Seek(analysisStart); player.Play(); }
                else { playing = false; player.Pause(); Button("PlayButton").Content = "▶ 재생"; }
                Tick();
            };
            Window.Drop += delegate(object sender, DragEventArgs e) {
                if (cancellation != null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) Open(files[0]);
            };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Space && !(Keyboard.FocusedElement is TextBox) && !(Keyboard.FocusedElement is ComboBox)) { TogglePlayback(); e.Handled = true; }
            };
            Window.Closed += delegate { closed = true; if(cancellation != null) cancellation.Cancel(); timer.Stop(); rhythmInputTimer.Stop(); player.Close(); };
            timer.Tick += delegate { Tick(); }; timer.Start();
        }
        Button Button(string name) { return Find<Button>(name); }
        ComboBox Combo(string name) { return Find<ComboBox>(name); }
        static double Selected(ComboBox combo) { return double.Parse(((ComboBoxItem)combo.SelectedItem).Tag.ToString(),CultureInfo.InvariantCulture); }
        void Status(string text) { if(!closed) Find<TextBlock>("Status").Text = text; }
        public static string Format(double seconds) { return string.Format(CultureInfo.InvariantCulture,"{0:00}:{1:00.00}",(int)seconds/60,seconds%60); }
        public void Open(string file) {
            if (cancellation != null) return;
            try {
                if (!File.Exists(file)) throw new FileNotFoundException("파일을 찾을 수 없습니다.");
                if (new FileInfo(file).Length > 200L*1024*1024) throw new IOException("200MB 이하의 음원을 선택해주세요.");
                player.Close(); ready = false; playing = false; duration = 0; path = Path.GetFullPath(file);
                ClearResult(); Find<TextBox>("Start").Text = "0";
                Button("PlayButton").Content = "▶ 재생"; Button("PlayButton").IsEnabled = false; Button("AnalyzeButton").IsEnabled = false;
                Find<Slider>("Timeline").IsEnabled = false;
                Find<TextBlock>("FileLabel").Text = Path.GetFileName(path);
                Status("음원을 열고 있어요…"); player.Open(new Uri(path));
            } catch (Exception e) { Status(e.Message); }
        }
        void ClearResult() {
            rhythmInputTimer.Stop();
            Tab.Rhythm=new BeatMap(); detectedBpm=0; stemPath=""; resultSource="";
            candidateNotes=new List<Note>(); leadNotes=new List<Note>();
            Find<TextBox>("Bpm").Text=""; Find<TextBox>("FirstBar").Text="0";
            Find<TextBlock>("RhythmInfo").Text="BPM·박 위치는 자동 추정합니다. 박자표와 첫 마디는 확인 후 보정해주세요.";
            Tab.Notes = new List<Note>(); Tab.RangeStart = 0; Tab.RangeEnd = 0; Tab.ResetView();
            Find<CheckBox>("Loop").IsChecked = false;
            foreach(string name in new[]{"TextExport","JsonExport","RestartButton","StemExport"}) Button(name).IsEnabled = false;
            Find<TextBlock>("Summary").Text = "타브를 만들면 원곡 재생 시간에 맞춰 현재 프렛을 보여드려요.";
        }
        void Busy(bool busy) {
            foreach(string name in new[]{"OpenButton","DemoButton"}) Button(name).IsEnabled = !busy;
            Button("AnalyzeButton").IsEnabled = !busy && ready;
            foreach(string name in new[]{"Tuning","Frets","Length","InputMode","Part"}) Combo(name).IsEnabled = !busy;
            foreach(string name in new[]{"ApplyRhythm","TempoHalf","TempoDouble","AlignFirstBar"}) Button(name).IsEnabled=!busy;
            Combo("GridMode").IsEnabled=!busy;
            Find<TextBox>("Start").IsEnabled = !busy;
            Button("CancelButton").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Find<ProgressBar>("Progress").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        public async Task Analyze() {
            if (!ready || cancellation != null) return;
            double start;
            if (!double.TryParse(Find<TextBox>("Start").Text,NumberStyles.Float,CultureInfo.InvariantCulture,out start) || double.IsNaN(start) || double.IsInfinity(start) || start < 0 || start >= duration - .15) {
                Status("분석 시작 시간을 음원 길이 안으로 입력해주세요."); return;
            }
            double length = Math.Min(Selected(Combo("Length")), duration - start);
            int tuning = Combo("Tuning").SelectedIndex, frets = (int)Selected(Combo("Frets"));
            bool mixture=Combo("InputMode").SelectedIndex==0, polyphonic=Combo("Part").SelectedIndex==0;
            CancellationTokenSource operation = new CancellationTokenSource(); cancellation = operation;
            Busy(true); Find<ProgressBar>("Progress").Value = 0;
            Status(mixture ? "BPM 분석 → AI 기타 분리 → 기타 채보 순서로 진행합니다…" : "기타 음원의 BPM과 음을 분석하고 있어요…");
            try {
                Transcription transcription = await Task.Run(delegate {
                    Transcription parsed=AiEngine.Run(path,start,length,mixture,polyphonic,operation.Token,delegate(string stage,int percent) {
                        Window.Dispatcher.BeginInvoke(new Action(delegate { if(!closed && cancellation==operation) { Status(stage); Find<ProgressBar>("Progress").Value=percent; } }));
                    });
                    return parsed;
                });
                operation.Token.ThrowIfCancellationRequested(); if(closed) return;
                candidateNotes=transcription.CandidateNotes; leadNotes=transcription.LeadNotes;
                List<Note> result=polyphonic ? candidateNotes : leadNotes;
                if(polyphonic) Analysis.FingerPolyphonic(result,Analysis.Tunings[tuning],frets);
                else Analysis.Finger(result,Analysis.Tunings[tuning],frets);
                analysisStart = start; analysisEnd = Math.Min(start + length,duration);
                resultTuning = tuning; resultFrets = frets;
                stemPath=transcription.StemPath; resultSource=transcription.Source;
                Tab.Rhythm=transcription.Rhythm; detectedBpm=Tab.Rhythm.Bpm;
                Tab.Rhythm.Meter=(int)Selected(Combo("Meter"));
                Tab.Rhythm.UseDetectedBeats=Combo("GridMode").SelectedIndex==1;
                Find<TextBox>("Bpm").Text=Tab.Rhythm.Available ? Tab.Rhythm.Bpm.ToString("0.####",CultureInfo.InvariantCulture) : "";
                Find<TextBox>("FirstBar").Text=Tab.Rhythm.Origin.ToString("0.000000",CultureInfo.InvariantCulture);
                RhythmInfo(); Button("StemExport").IsEnabled=File.Exists(stemPath);
                Tab.Notes = result; Tab.Tuning = Analysis.Tunings[tuning]; Tab.RangeStart = analysisStart; Tab.RangeEnd = analysisEnd;
                Tab.ResetView();
                bool hasNotes = result.Count > 0;
                foreach (string name in new[]{"TextExport","JsonExport","RestartButton"}) Button(name).IsEnabled = hasNotes;
                int outside = result.Count(n => n.Fret < 0);
                Find<TextBlock>("Summary").Text = result.Count + "개 추정 노트 · "+(mixture ? "AI 분리 기타 채보" : "기타 음원 직접 채보")+" · 주황색 재생선 · 초록색 현재 음/코드";
                Seek(start);
                Status(hasNotes ? "기타 타브가 준비됐어요. 첫 마디를 확인한 뒤 재생해주세요." + (outside>0 ? " 음역 밖/운지 충돌 "+outside+"개는 ?로 표시됩니다." : "") : "선명한 기타 음을 찾지 못했어요. 분리된 기타 음원이나 다른 구간으로 확인해주세요.");
            } catch(OperationCanceledException) { Status("분석을 취소했어요."); }
            catch(Exception e) { Status("분석 오류: " + e.Message); }
            finally { if(cancellation == operation) cancellation = null; operation.Dispose(); if(!closed) Busy(false); }
        }
        void RhythmInfo() {
            BeatMap rhythm=Tab.Rhythm;
            Find<TextBlock>("RhythmInfo").Text=rhythm.Available ?
                (rhythm.UseDetectedBeats && !rhythm.ManualTempo ? "검출 박 추적" : "고정 BPM 격자")+" · BPM은 4분음표 기준 · "+rhythm.Meter+"/"+rhythm.BeatUnit+" · 첫 강박에서 일시정지 후 ‘현재 위치를 첫 마디로’를 누르세요." :
                "규칙적인 박을 찾지 못했어요. BPM과 첫 마디를 입력하면 마디선을 표시합니다.";
        }
        void ScaleTempo(double scale) {
            double bpm;
            if(double.TryParse(Find<TextBox>("Bpm").Text,NumberStyles.Float,CultureInfo.InvariantCulture,out bpm)) {
                Find<TextBox>("Bpm").Text=(bpm*scale).ToString("0.##",CultureInfo.InvariantCulture); ApplyRhythm();
            }
        }
        void ApplyRhythm() {
            double bpm,first;
            if(!double.TryParse(Find<TextBox>("Bpm").Text,NumberStyles.Float,CultureInfo.InvariantCulture,out bpm) || double.IsNaN(bpm) || bpm<30 || bpm>300 ||
               !double.TryParse(Find<TextBox>("FirstBar").Text,NumberStyles.Float,CultureInfo.InvariantCulture,out first) || double.IsNaN(first) || first< -60 || first>duration) {
                Status("BPM은 30~300, 첫 마디는 원곡 안의 초 단위 시간으로 입력해주세요."); return;
            }
            Tab.Rhythm.Bpm=bpm; Tab.Rhythm.Origin=first;
            Tab.Rhythm.Meter=(int)Selected(Combo("Meter"));
            Tab.Rhythm.UseDetectedBeats=Combo("GridMode").SelectedIndex==1;
            Tab.Rhythm.ManualTempo=detectedBpm==0 || Math.Abs(bpm-detectedBpm)>.000001;
            Tab.ResetView(); RhythmInfo(); Tick();
            Status("박자선을 적용했어요. 실제 음 시작 시점은 유지하고 마디 기준만 바꿨습니다.");
        }
        void ExportStem() {
            if(!File.Exists(stemPath)) return;
            SaveFileDialog dialog=new SaveFileDialog {FileName=Path.GetFileNameWithoutExtension(path)+".guitar.wav",Filter="기타 WAV|*.wav"};
            if(dialog.ShowDialog(Window)!=true) return;
            try {
                if(string.Equals(Path.GetFullPath(dialog.FileName),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)) throw new IOException("원곡과 다른 이름으로 저장해주세요.");
                File.Copy(stemPath,dialog.FileName,true);
                Status("분석에 사용한 기타 음원을 저장했어요. 원곡 "+Format(analysisStart)+"부터의 구간입니다.");
            } catch(Exception e) { Status("저장 오류: "+e.Message); }
        }
        void TogglePlayback() {
            if (!ready) return;
            if (playing) { player.Pause(); playing = false; }
            else {
                if (player.Position.TotalSeconds >= duration - .03) Seek(Tab.Notes.Count > 0 ? analysisStart : 0);
                if (Find<CheckBox>("Loop").IsChecked == true && Tab.Notes.Count > 0 && (player.Position.TotalSeconds < analysisStart || player.Position.TotalSeconds >= analysisEnd)) Seek(analysisStart);
                player.Play(); playing = true;
            }
            Button("PlayButton").Content = playing ? "❚❚ 일시정지" : "▶ 재생"; Tick();
        }
        public void Seek(double seconds) {
            if(!ready) return;
            player.Position = TimeSpan.FromSeconds(Math.Max(0,Math.Min(duration,seconds))); Tick();
        }
        void Tick() {
            if(closed) return;
            double time = ready ? player.Position.TotalSeconds : 0;
            if(playing && Find<CheckBox>("Loop").IsChecked == true && Tab.Notes.Count>0 && time >= analysisEnd) { player.Position = TimeSpan.FromSeconds(analysisStart); time = analysisStart; }
            Find<TextBlock>("Clock").Text = Format(time) + " / " + Format(duration);
            if(!dragging) { updatingSlider = true; Find<Slider>("Timeline").Value = time; updatingSlider = false; }
            Tab.Update(time);
            Find<TextBlock>("GuiBeatClock").Text=Tab.Rhythm.Available ? "적용 "+Tab.Rhythm.Bpm.ToString("0.####",CultureInfo.InvariantCulture)+" BPM   "+string.Join("   ",Enumerable.Range(1,Tab.Rhythm.Meter).Select(i=>(i==Tab.Rhythm.BeatInBar(time) ? "● " : "○ ")+i)) : "BPM을 입력하면 박 표시가 재생 시간에 맞춰 움직입니다.";
            Find<TextBlock>("BarPosition").Text=Tab.Rhythm.Available ? (Tab.Rhythm.BarAt(time)>0 ? Tab.Rhythm.BarAt(time)+"마디 · "+Tab.Rhythm.BeatInBar(time)+"박" : "첫 마디 전") : "BPM 분석 전 / 수동 입력";
            List<int> active=TabView.ActiveIndices(Tab.Notes,time);
            Find<TextBlock>("CurrentNote").Text = active.Count>0 ? string.Join("  /  ",active.Select(i=>Tab.Notes[i].Name+(Tab.Notes[i].Fret>=0 ? " · "+(Tab.Notes[i].String+1)+"번줄 "+Tab.Notes[i].Fret+"프렛" : " · 배치 불가"))) : Tab.Notes.Count>0 ? (time < analysisStart || time >= analysisEnd ? "분석 구간 밖" : "쉼 / 다음 음 대기") : "대기 중";
        }
        string TextTab() {
            StringBuilder text = new StringBuilder("타브만든댕 — " + Path.GetFileName(path) + "\r\n튜닝: " + ((ComboBoxItem)Combo("Tuning").Items[resultTuning]).Content + " · 최대 " + resultFrets + "프렛\r\n시간은 원곡 기준 초 · 텍스트 칸 간격은 박자와 무관 · ? = 음역 밖/운지 배치 불가\r\n");
            if(Tab.Rhythm.Available) text.AppendLine("4분음표 BPM "+Tab.Rhythm.Bpm.ToString("0.####",CultureInfo.InvariantCulture)+" · "+Tab.Rhythm.Meter+"/"+Tab.Rhythm.BeatUnit+" · 첫 마디 "+Tab.Rhythm.Origin.ToString("0.000000",CultureInfo.InvariantCulture)+"초 (수동 확인 필요)\r\n");
            string[] labels = {"e","B","G","D","A",resultTuning == 1 ? "D" : "E"};
            if(resultTuning == 2) labels = new[]{"eb","Bb","Gb","Db","Ab","Eb"};
            foreach(var measure in Tab.Notes.GroupBy(n=>Tab.Rhythm.Available ? Tab.Rhythm.BarAt(n.Start) : (int)(n.Start/10)+1)) {
                text.AppendLine(Tab.Rhythm.Available ? measure.Key+"마디" : "구간 "+measure.Key);
                List<List<Note>> events=new List<List<Note>>();
                foreach(Note note in measure) {
                    if(events.Count==0 || note.Start-events[events.Count-1][0].Start>.025) events.Add(new List<Note>());
                    events[events.Count-1].Add(note);
                }
                for(int offset=0;offset<events.Count;offset+=12) {
                    var columns=events.Skip(offset).Take(12).ToList();
                    text.Append("    "); foreach(var chord in columns) text.Append(chord[0].Start.ToString("0.00",CultureInfo.InvariantCulture).PadRight(8)); text.AppendLine();
                    for(int s=0;s<6;s++) {
                        text.Append(labels[s].PadRight(2)+" |");
                        foreach(var chord in columns) { Note note=chord.FirstOrDefault(n=>n.String==s); text.Append((note==null ? "" : note.Fret.ToString()).PadLeft(2,'-').PadRight(8,'-')); }
                        text.AppendLine("|");
                    }
                    text.Append("    "); foreach(var chord in columns) text.Append((chord.Any(n=>n.Fret<0) ? "?" : "").PadRight(8)); text.AppendLine("\r\n");
                }
            }
            return text.ToString();
        }
        string JsonNotes() {
            int firstBeat=Tab.Rhythm.Available ? (int)Math.Ceiling(Tab.Rhythm.BeatAt(analysisStart)) : 0;
            int lastBeat=Tab.Rhythm.Available ? (int)Math.Floor(Tab.Rhythm.BeatAt(analysisEnd)) : -1;
            double[] effectiveBeats=Enumerable.Range(firstBeat,Math.Max(0,lastBeat-firstBeat+1)).Select(i=>Tab.Rhythm.TimeAt(i)).ToArray();
            var data=new Dictionary<string,object> {
                {"file",Path.GetFileName(path)},{"source",resultSource},{"stringNumbering","1 = highest string"},
                {"tuning",Analysis.Tunings[resultTuning]},{"maxFret",resultFrets},
                {"bpm",Tab.Rhythm.Available ? (object)Tab.Rhythm.Bpm : null},{"beatConfidence",Tab.Rhythm.Confidence},
                {"beats",effectiveBeats},{"detectedQuarterBeats",Tab.Rhythm.Beats},{"beatsPerBar",Tab.Rhythm.Meter},{"firstBarSeconds",Tab.Rhythm.Origin},
                {"manualTempo",Tab.Rhythm.ManualTempo},{"downbeatDetected",false},
                {"beatUnit",Tab.Rhythm.BeatUnit},{"bpmUnit","quarter"},{"useDetectedBeats",Tab.Rhythm.UseDetectedBeats},
                {"effectiveGrid",Tab.Rhythm.UseDetectedBeats && !Tab.Rhythm.ManualTempo ? "detected" : "constant"},
                {"part",Combo("Part").SelectedIndex==0 ? "poly" : "lead"},
                {"notes",Tab.Notes.Select(n=>new Dictionary<string,object> {
                    {"start",n.Start},{"duration",n.Duration},{"midi",n.Midi},{"note",n.Name},
                    {"string",n.String<0 ? null : (object)(n.String+1)},{"fret",n.Fret<0 ? null : (object)n.Fret},
                    {"relativeStrength",n.Confidence},{"bar",Tab.Rhythm.Available ? (object)Tab.Rhythm.BarAt(n.Start) : null},
                    {"beat",Tab.Rhythm.Available ? (object)Tab.Rhythm.BeatAt(n.Start) : null}
                }).ToArray()}
            };
            return new JavaScriptSerializer {MaxJsonLength=32*1024*1024}.Serialize(data);
        }
        void Export(bool json) {
            SaveFileDialog dialog = new SaveFileDialog {FileName=Path.GetFileNameWithoutExtension(path)+(json ? ".notes.json" : ".tab.txt"),Filter=json ? "JSON|*.json" : "텍스트 타브|*.txt"};
            if(dialog.ShowDialog(Window)!=true) return;
            try { File.WriteAllText(dialog.FileName,json ? JsonNotes() : TextTab(),new UTF8Encoding(!json)); Status("저장했어요: "+dialog.FileName); }
            catch(Exception e) { Status("저장하지 못했어요: "+e.Message); }
        }
    }
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            try {
                if(args.Length>0 && args[0]=="--self-test") return SelfTest.Run(args.Length>1 ? args[1] : "desktop-test.txt",args.Length>2 && args[2]=="--with-ai");
                Application app = new Application();
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { MessageBox.Show("오류가 발생했어요: "+e.Exception.Message,"타브만든댕"); e.Handled=true; };
                Studio studio = new Studio();
                if(args.Length>0) studio.Window.Loaded += delegate { studio.Open(args[0]); };
                return app.Run(studio.Window);
            } catch(Exception e) { MessageBox.Show(e.ToString(),"타브만든댕 실행 오류"); return 1; }
        }
    }
}
