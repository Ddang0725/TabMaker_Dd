using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TabMaker {
    internal sealed class Transcription {
        public List<Note> Notes=new List<Note>();
        public BeatMap Rhythm=new BeatMap();
        public string StemPath, Source;
    }
    internal static class AiEngine {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer {MaxJsonLength=32*1024*1024}; }
        public static string[] Locate() {
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string settings=Path.Combine(root,"ai-engine.json");
            if(File.Exists(settings)) {
                var config=Serializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(settings,Encoding.UTF8));
                if(!config.ContainsKey("python") || !File.Exists(config["python"])) throw new IOException("ai-engine.json의 python 경로를 확인해주세요.");
                string separator=config.ContainsKey("separator") ? config["separator"] : "demucs";
                string transcriber=config.ContainsKey("transcriber") ? config["transcriber"] : "harmonic";
                if(separator!="demucs" && separator!="roformer") throw new IOException("지원하지 않는 기타 분리 모델입니다.");
                if(transcriber!="harmonic" && transcriber!="basic-pitch") throw new IOException("지원하지 않는 채보 모델입니다.");
                return new[]{config["python"],config.ContainsKey("models") ? config["models"] : Path.Combine(root,"models"),separator,transcriber};
            }
            foreach(string home in new[]{Path.Combine(root,".ai"),Path.Combine(Directory.GetParent(root.TrimEnd(Path.DirectorySeparatorChar)).FullName,"음원분리댕")}) {
                string python=Path.Combine(home,home.EndsWith(".ai") ? "Scripts\\python.exe" : ".venv\\Scripts\\python.exe");
                if(File.Exists(python)) return new[]{python,Path.Combine(home,"models"),"demucs","harmonic"};
            }
            throw new IOException("기타 분석 엔진을 찾지 못했어요. 음원분리댕의 AI 엔진을 설치하거나 ai-engine.json에 Python과 모델 경로를 지정해주세요.");
        }
        static string Quote(string value) { return "\""+value.Replace("\"","\\\"")+"\""; }
        public static Transcription Run(string input,double start,double duration,bool mixture,bool polyphonic,CancellationToken token,Action<string,int> progress) {
            string[] environment=Locate();
            string cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabMaker_Dd");
            Directory.CreateDirectory(cache);
            string script=Path.Combine(cache,"transcribe.py");
            using(Stream resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("transcribe.py"))
            using(StreamReader reader=new StreamReader(resource,Encoding.UTF8)) {
                string content=reader.ReadToEnd();
                if(!File.Exists(script) || File.ReadAllText(script,Encoding.UTF8)!=content) File.WriteAllText(script,content,new UTF8Encoding(false));
            }
            string outputFolder=Path.Combine(cache,"analyses",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputFolder);
            string output=Path.Combine(outputFolder,"result.json");
            ProcessStartInfo info=new ProcessStartInfo(environment[0]) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
                Arguments="-u "+Quote(script)+" --input "+Quote(input)+" --output "+Quote(output)+" --start "+start.ToString("R",CultureInfo.InvariantCulture)+" --duration "+duration.ToString("R",CultureInfo.InvariantCulture)+" --mode "+(mixture ? "mix" : "guitar")+" --part "+(polyphonic ? "poly" : "lead")+" --model-home "+Quote(environment[1])+" --separator "+environment[2]+" --transcriber "+environment[3]
            };
            info.EnvironmentVariables["PYTHONUTF8"]="1";
            info.EnvironmentVariables["PYTHONUNBUFFERED"]="1";
            info.EnvironmentVariables["TORCH_HOME"]=environment[1];
            info.EnvironmentVariables["PATH"]=Path.GetDirectoryName(environment[0])+Path.PathSeparator+Environment.GetEnvironmentVariable("PATH");
            string error="";
            object gate=new object();
            using(Process process=new Process {StartInfo=info}) {
                process.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e) {
                    if(string.IsNullOrEmpty(e.Data)) return;
                    try {
                        var message=Serializer().Deserialize<Dictionary<string,object>>(e.Data);
                        if(message.ContainsKey("error")) { lock(gate) error=Convert.ToString(message["error"]); }
                        else if(message.ContainsKey("stage")) progress(Convert.ToString(message["stage"]),Convert.ToInt32(message["progress"]));
                    } catch(ArgumentException) { }
                };
                process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) {
                    if(string.IsNullOrEmpty(e.Data)) return;
                    lock(gate) { error+="\n"+e.Data; if(error.Length>6000) error=error.Substring(error.Length-6000); }
                };
                token.ThrowIfCancellationRequested(); process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                while(!process.WaitForExit(100)) {
                    if(token.IsCancellationRequested) { process.Kill(); process.WaitForExit(); token.ThrowIfCancellationRequested(); }
                }
                process.WaitForExit(); token.ThrowIfCancellationRequested();
                if(process.ExitCode!=0 || !File.Exists(output)) throw new IOException("기타 분석 엔진 오류: "+error.Trim());
            }
            var data=Serializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(output,Encoding.UTF8));
            Transcription result=new Transcription {Source=Convert.ToString(data["source"]),StemPath=Convert.ToString(data["stem"])};
            result.Rhythm.Bpm=Convert.ToDouble(data["bpm"],CultureInfo.InvariantCulture);
            result.Rhythm.Confidence=Convert.ToDouble(data["confidence"],CultureInfo.InvariantCulture);
            foreach(object value in (IEnumerable)data["beats"]) result.Rhythm.Beats.Add(Convert.ToDouble(value,CultureInfo.InvariantCulture));
            if(result.Rhythm.Beats.Count>0) result.Rhythm.Origin=result.Rhythm.Beats[0];
            foreach(Dictionary<string,object> item in (IEnumerable)data["notes"]) {
                result.Notes.Add(new Note {Start=Convert.ToDouble(item["start"]),Duration=Convert.ToDouble(item["duration"]),Midi=Convert.ToInt32(item["midi"]),Confidence=Convert.ToDouble(item["confidence"])});
            }
            return result;
        }
    }
}
