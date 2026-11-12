const $ = id => document.getElementById(id);
const tunings = {standard:[64,59,55,50,45,40], dropD:[64,59,55,50,45,38], half:[63,58,54,49,44,39]};
const names = ['C','C#','D','D#','E','F','F#','G','G#','A','A#','B'];
let buffer = null, audioURL = null, worker = null, notes = [], tabText = '', fileName = '', loadVersion = 0;
let resultSettings = null;
function status(message, error = false) { $('status').textContent = message; $('status').classList.toggle('error', error); }
function busy(value) {
  $('generate').disabled = value || !buffer;
  for (const id of ['demo','file','tuning','frets','start','length']) $(id).disabled = value;
  $('cancel').hidden = !value;
  $('progress').hidden = !value;
}
function clearResult() {
  notes = []; tabText = ''; resultSettings = null;
  $('empty').hidden = false; $('output').hidden = true;
}
function drawWave() {
  const canvas = $('wave'); canvas.width = 700;
  const ctx = canvas.getContext('2d');
  const data = buffer.getChannelData(0), step = Math.max(1,Math.floor(data.length / 700));
  ctx.clearRect(0,0,700,64); ctx.fillStyle = '#8ba47a';
  for (let x = 0; x < 700; x++) {
    let peak = 0;
    for (let j = x * step; j < Math.min((x + 1) * step, data.length); j += Math.max(1,Math.floor(step / 30))) peak = Math.max(peak,Math.abs(data[j]));
    ctx.fillRect(x,32 - Math.max(1,peak * 29),1,Math.max(2,peak * 58));
  }
}
function setAudio(decoded, blob, name) {
  buffer = decoded; fileName = name;
  if (audioURL) URL.revokeObjectURL(audioURL);
  audioURL = URL.createObjectURL(blob); $('player').src = audioURL;
  $('fileLabel').textContent = name;
  $('fileMeta').textContent = `${decoded.duration.toFixed(1)}초 · ${decoded.numberOfChannels}채널`;
  $('playerBox').hidden = false; $('start').value = '0';
  clearResult(); drawWave(); busy(false); status('준비됐어요. 분석 구간을 고르고 타브를 만들어보세요.');
}
async function loadFile(file) {
  if (!file || worker) return;
  const version = ++loadVersion;
  if (file.size > 100 * 1024 * 1024) { status('100MB 이하 음원을 선택해주세요.',true); return; }
  busy(true); $('cancel').hidden = true; status('음원을 읽고 있어요…');
  let context;
  try {
    context = new AudioContext();
    const decoded = await context.decodeAudioData(await file.arrayBuffer());
    if (version === loadVersion) setAudio(decoded,file,file.name);
  } catch (error) { status('음원을 열 수 없어요. WAV 또는 MP3 파일로 다시 시도해주세요.',true); }
  finally { if (context) await context.close(); if (version === loadVersion) busy(false); }
}
$('file').addEventListener('change', e => loadFile(e.target.files[0]));
$('drop').addEventListener('dragover', e => { e.preventDefault(); if (!worker) $('drop').classList.add('drag'); });
$('drop').addEventListener('dragleave', () => $('drop').classList.remove('drag'));
$('drop').addEventListener('drop', e => { e.preventDefault(); $('drop').classList.remove('drag'); if (!$('file').disabled) loadFile(e.dataTransfer.files[0]); });
function makeDemo() {
  const rate = 44100, melody = [64,67,69,71,69,67,64,62,64,67,71,76];
  const samples = new Float32Array(Math.ceil((melody.length * 0.4 + 0.2) * rate));
  melody.forEach((midi, i) => {
    const frequency = 440 * 2 ** ((midi - 69) / 12);
    for (let j = 0; j < 0.34 * rate; j++) {
      const t = j / rate, envelope = Math.min(1,t / 0.008) * Math.min(1,(0.34 - t) / 0.035) * Math.exp(-t * 2);
      samples[Math.floor((i * 0.4 + 0.05) * rate) + j] = envelope * (Math.sin(2 * Math.PI * frequency * t) * 0.45 + Math.sin(4 * Math.PI * frequency * t) * 0.14);
    }
  });
  const decoded = new AudioBuffer({length:samples.length, sampleRate:rate,numberOfChannels:1}); decoded.copyToChannel(samples,0);
  const wav = new ArrayBuffer(44 + samples.length * 2), view = new DataView(wav);
  const str = (offset,text) => [...text].forEach((c,i) => view.setUint8(offset+i,c.charCodeAt(0)));
  str(0,'RIFF'); view.setUint32(4,wav.byteLength-8,true); str(8,'WAVE'); str(12,'fmt '); view.setUint32(16,16,true); view.setUint16(20,1,true); view.setUint16(22,1,true); view.setUint32(24,rate,true); view.setUint32(28,rate*2,true); view.setUint16(32,2,true); view.setUint16(34,16,true); str(36,'data'); view.setUint32(40,samples.length*2,true);
  samples.forEach((value,i) => view.setInt16(44+i*2,Math.round(value*32767),true));
  setAudio(decoded,new Blob([wav],{type:'audio/wav'}),'작은 산책 · 샘플 멜로디.wav');
}
$('demo').addEventListener('click',makeDemo);
function finishWorker() { if(worker) worker.terminate(); worker = null; busy(false); }
$('cancel').addEventListener('click', () => { finishWorker(); status('분석을 취소했어요. 다시 시작할 수 있습니다.'); });
$('generate').addEventListener('click',async () => {
  if (!buffer || worker) return;
  const start = Number($('start').value), duration = Number($('length').value);
  if (!Number.isFinite(start) || start < 0 || start >= buffer.duration) { status('시작 시간을 음원 길이 안으로 입력해주세요.',true); return; }
  const source = buffer;
  const settings = {tuning:$('tuning').value, maxFret:Number($('frets').value), start, duration:Math.min(duration,source.duration-start)};
  busy(true); $('cancel').hidden = true; clearResult(); $('progress').value = 0; status('분석용 오디오를 준비하고 있어요…');
  try {
    const rate = 8000, length = Math.floor(settings.duration * rate);
    if (length < 1200) throw new Error('분석 구간을 0.15초 이상으로 설정해주세요.');
    const offline = new OfflineAudioContext(1,length,rate);
    const input = offline.createBufferSource(); input.buffer = source; input.connect(offline.destination); input.start(0,start,settings.duration);
    const rendered = await offline.startRendering();
    const samples = rendered.getChannelData(0);
    worker = new Worker('pitch-worker.js'); $('cancel').hidden = false;
    status('음 높이를 찾고 연주하기 좋은 운지를 고르고 있어요…');
    worker.onmessage = ({data}) => {
      if (data.progress !== undefined) $('progress').value = data.progress;
      if (data.error) { finishWorker(); status(`분석 오류: ${data.error}`,true); }
      if (data.result) {
        notes = data.result; resultSettings = settings; finishWorker();
        if (!notes.length) { status('선명한 멜로디를 찾지 못했어요. 다른 구간이나 기타 독주 음원으로 시도해주세요.',true); return; }
        renderResult();
        const outside = notes.filter(n => n.fret === null).length;
        status(`${notes.length}개 노트를 찾았어요.${outside ? ` ${outside}개는 선택한 기타 음역 밖이에요.` : ' 원곡을 들으며 운지를 확인해보세요.'}`);
      }
    };
    worker.onerror = () => { finishWorker(); status('분석기를 시작하지 못했어요. 실행.bat으로 앱을 열었는지 확인해주세요.',true); };
    worker.postMessage({samples,rate,start,tuning:tunings[settings.tuning],maxFret:settings.maxFret},[samples.buffer]);
  } catch (error) { finishWorker(); status(error.message,true); }
});
function noteName(midi) { return names[midi%12] + (Math.floor(midi/12)-1); }
function renderResult() {
  $('empty').hidden = true; $('output').hidden = false;
  $('stats').replaceChildren();
  for (const text of [`${notes.length} NOTES`, `${resultSettings.start.toFixed(1)}–${(resultSettings.start+resultSettings.duration).toFixed(1)}s`, resultSettings.tuning.toUpperCase()]) {
    const span = document.createElement('span'); span.textContent = text; $('stats').append(span);
  }
  const tuning = tunings[resultSettings.tuning];
  const labels = tuning.map(midi => names[midi%12].padEnd(2,' '));
  const blocks = [];
  for (let offset = 0; offset < notes.length; offset += 16) {
    const slice = notes.slice(offset,offset+16);
    const lines = labels.map(label => label + '|');
    let times = '   ', marker = '   ';
    for (const note of slice) {
      const time = note.start.toFixed(2), width = Math.max(5,time.length + 1);
      times += time.padEnd(width,' ');
      marker += (note.fret === null ? '?' : '').padEnd(width,' ');
      lines.forEach((_,i) => { lines[i] += (note.string === i ? String(note.fret) : '').padStart(2,'-').padEnd(width,'-'); });
    }
    blocks.push(`${times}\n${lines.map(line=>line+'|').join('\n')}${slice.some(n=>n.fret===null) ? '\n'+marker : ''}`);
  }
  tabText = `타브만든댕 — ${fileName}\n튜닝: ${resultSettings.tuning} · 최대 ${resultSettings.maxFret}프렛\n시간: 원곡 기준 초 · 각 칸은 노트 순서이며 박자 악보가 아닙니다.\n? = 선택한 음역 밖\n\n${blocks.join('\n\n')}\n\n자동 추정 멜로디입니다. 코드·벤딩·기타 주법은 인식하지 않습니다.\n`;
  $('tab').replaceChildren();
  blocks.forEach(text => { const pre = document.createElement('pre'); pre.className = 'tab-block'; pre.textContent = text; $('tab').append(pre); });
  $('notes').replaceChildren();
  notes.forEach(note => {
    const row = document.createElement('tr');
    [note.start.toFixed(2)+'초',note.duration.toFixed(2)+'초',noteName(note.midi),note.string === null ? '음역 밖' : note.string+1,note.fret ?? '—'].forEach(value=>{const td=document.createElement('td');td.textContent=value;row.append(td);});
    $('notes').append(row);
  });
  const legend = document.querySelector('.legend');
  legend.textContent = '위쪽이 1번 줄 · 숫자는 프렛 · 시간은 원곡 기준(초) · 칸 간격은 박자와 무관';
}
function download(content,type,extension) {
  const url = URL.createObjectURL(new Blob([content],{type}));
  const link = document.createElement('a'); link.href = url; link.download = fileName.replace(/\.[^.]+$/,'') + extension; link.click();
  setTimeout(()=>URL.revokeObjectURL(url),1000);
}
$('download').addEventListener('click',()=>download('\uFEFF'+tabText,'text/plain;charset=utf-8','.tab.txt'));
$('json').addEventListener('click',()=>download(JSON.stringify({file:fileName,settings:resultSettings,stringNumbering:'1 = highest string',notes:notes.map(n=>({...n,string:n.string===null?null:n.string+1,note:noteName(n.midi)}))},null,2),'application/json','.notes.json'));
$('print').addEventListener('click',()=>window.print());
