"""Local guitar separation, multi-pitch transcription and onset/beat tracking.

The Demucs weights and Python environment must already be installed. No automatic
network downloads and no inference on a mixture disguised as a guitar stem.
"""
from __future__ import annotations
import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import wave
import tempfile

os.environ.setdefault('OMP_NUM_THREADS', '4')
os.environ.setdefault('OPENBLAS_NUM_THREADS', '4')
import numpy as np


def emit(stage, percent):
    print(json.dumps(dict(stage=stage, progress=percent), ensure_ascii=False), flush=True)


def fit_beat_grid(times):
    """Refine constant tempo only when the tracked beats support a stable grid."""
    times = np.asarray(times, dtype=float)
    if len(times) < 8: return None
    indices = np.arange(len(times), dtype=float)
    keep = np.ones(len(times), dtype=bool)
    for _ in range(3):
        period, origin = np.polyfit(indices[keep], times[keep], 1)
        residual = times-(origin+period*indices)
        keep = abs(residual) <= max(.025, period*.12)
        if keep.sum() < len(times)*.85: return None
    if period <= 0 or np.percentile(abs(residual),90) > period*.08: return None
    return dict(bpm=round(60/period,4), gridOrigin=float(origin), gridStable=True)


def read_audio(path, start, duration):
    import imageio_ffmpeg
    command = [imageio_ffmpeg.get_ffmpeg_exe(), '-nostdin', '-v', 'error',
               '-ss', str(start), '-i', str(path), '-t', str(duration), '-vn',
               '-ar', '44100', '-ac', '2', '-f', 'f32le', '-']
    done = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if done.returncode:
        raise RuntimeError(done.stderr.decode('utf-8', errors='replace')[-1200:])
    data = np.frombuffer(done.stdout, dtype='<f4').reshape(-1, 2).T.copy()
    if data.shape[1] < 6615:
        raise ValueError('분석할 음원이 너무 짧습니다.')
    return data


def tempo_and_beats(mono, rate, start=0.0):
    """Spectral-flux onset, autocorrelation tempo and dynamic-programming beats."""
    hop = int(rate * .02)
    size = 1024 if rate <= 12000 else 2048
    signal = np.pad(mono, (size//2, size//2))
    frames = np.lib.stride_tricks.sliding_window_view(signal, size)[::hop]
    previous = np.zeros(size//2+1)
    onset_parts = []
    window = np.hanning(size)
    for begin in range(0, len(frames), 512):
        magnitude = np.log1p(np.abs(np.fft.rfft(frames[begin:begin+512]*window, axis=1))*30)
        spectral_flux = np.maximum(np.diff(np.vstack([previous, magnitude]), axis=0), 0)
        # Bass drum, snare and harmonic attacks all contribute; ignore DC rumble.
        frequencies = np.fft.rfftfreq(size, 1/rate)
        onset_parts.append(spectral_flux[:, (frequencies >= 40) & (frequencies <= 6000)].sum(axis=1))
        previous = magnitude[-1]
    onset = np.concatenate(onset_parts)
    local_mean = np.convolve(onset, np.ones(31)/31, mode='same')
    onset = np.maximum(onset-local_mean*.75, 0)
    if len(onset) < 150 or onset.max(initial=0) < 1e-6:
        return dict(bpm=0.0, confidence=0.0, beats=[])
    onset /= max(float(np.percentile(onset, 95)), 1e-9)
    onset = np.minimum(onset, 5)
    centered = onset-onset.mean()
    nfft = 1 << (2*len(centered)-1).bit_length()
    transform = np.fft.rfft(centered, nfft)
    correlation = np.fft.irfft(transform*transform.conj(), nfft)[:len(centered)]
    correlation /= np.arange(len(centered), 0, -1)
    correlation /= max(float(correlation[0]), 1e-9)
    fps = rate/hop
    low, high = int(60*fps/220), min(len(onset)//3, int(60*fps/45))
    candidates = []
    for lag in range(max(low,2), high):
        if correlation[lag] < correlation[lag-1] or correlation[lag] < correlation[lag+1]:
            continue
        prior = np.exp(-.5*(np.log2((60*fps/lag)/120)/.95)**2)
        score = correlation[lag]
        if lag*2 < len(correlation): score += .35*correlation[lag*2]
        if lag*3 < len(correlation): score += .15*correlation[lag*3]
        candidates.append((float(score*prior), lag))
    if not candidates:
        return dict(bpm=0.0, confidence=0.0, beats=[])
    score, lag = max(candidates)
    if correlation[lag] < .08:
        return dict(bpm=0.0, confidence=0.0, beats=[])
    a,b,c = correlation[lag-1:lag+2]
    delta = .5*(a-c)/(a-2*b+c) if abs(a-2*b+c)>1e-9 else 0
    period = lag+float(np.clip(delta,-.5,.5))
    bpm = float(60*fps/period)
    # Follow local changes instead of extending a single guessed BPM forever.
    costs = np.zeros(len(onset)); previous = np.full(len(onset), -1, dtype=int)
    min_lag, max_lag = max(1, int(period*.7)), int(period*1.35)
    lags = np.arange(min_lag, max_lag+1)
    penalties = 30*np.log(lags/period)**2
    for i in range(len(onset)):
        valid = lags <= i
        if valid.any():
            options = costs[i-lags[valid]]-penalties[valid]
            best = int(np.argmax(options))
            if options[best] > 0:
                previous[i] = i-lags[valid][best]
                costs[i] = options[best]
        costs[i] += onset[i]-.12
    end_region = max(0,len(onset)-int(period*2))
    index = end_region+int(np.argmax(costs[end_region:]))
    beat_frames = []
    while index >= 0:
        beat_frames.append(index)
        index = int(previous[index])
    beat_frames.reverse()
    # Extend to the leading/trailing silence so measures cover the entire song.
    if len(beat_frames) < 3:
        return dict(bpm=0.0, confidence=0.0, beats=[])
    times = np.asarray(beat_frames, dtype=float)/fps
    while times[0] > period/fps:
        times = np.insert(times,0,times[0]-period/fps)
    while times[-1] < len(mono)/rate-period/fps:
        times = np.append(times,times[-1]+period/fps)
    result = dict(bpm=round(bpm,4), confidence=round(float(np.clip(correlation[lag],0,1)),3),
                  beats=[round(start+float(t),5) for t in times], gridStable=False)
    fitted = fit_beat_grid(times+start)
    if fitted: result.update(fitted)
    return result


def separate_guitar(stereo, model_home):
    import torch
    os.environ['TORCH_HOME'] = str(model_home)
    # Loading a missing cache is an actionable error, never an implicit download.
    def no_download(*args, **kwargs):
        raise RuntimeError('htdemucs_6s 모델이 없습니다. 음원분리댕에서 모델 설치를 먼저 완료해주세요.')
    torch.hub.download_url_to_file = no_download
    from demucs.pretrained import get_model
    from demucs.apply import apply_model
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    model = get_model('htdemucs_6s')
    device = 'cuda' if torch.cuda.is_available() else 'cpu'
    model.to(device).eval()
    data = torch.from_numpy(np.asarray(stereo,dtype=np.float32))
    reference = data.mean(0)
    mean, deviation = reference.mean(), reference.std()
    if deviation < 1e-6:
        return np.zeros_like(stereo)
    data = (data-mean)/deviation
    emit('AI로 기타를 분리하고 있어요 ('+device.upper()+')', 15)
    with torch.inference_mode():
        sources = apply_model(model, data[None], device=device, shifts=0, split=True,
                              overlap=.25, segment=7, progress=False, num_workers=0)[0]
    # The common mean must not be added independently to every separated source.
    guitar = sources[model.sources.index('guitar')]*deviation
    result = guitar.cpu().numpy()
    del sources, model
    if device == 'cuda': torch.cuda.empty_cache()
    return result


def mono_for_analysis(stereo):
    mid=stereo.mean(axis=0)
    channel_power=np.mean(stereo*stereo,axis=1)
    # Preserve a phase-inverted stereo guitar instead of cancelling it to silence.
    if np.mean(mid*mid)<channel_power.max(initial=0)*.15:
        return stereo[int(np.argmax(channel_power))]
    return mid


def attack_frames(mono, rate, hop, count):
    square=np.r_[0,np.cumsum(np.asarray(mono,dtype=float)**2)]
    centers=np.minimum(np.arange(count)*hop,len(mono))
    lo=np.maximum(0,centers-256); hi=np.minimum(len(mono),centers+256)
    energy=(square[hi]-square[lo])/np.maximum(1,hi-lo)
    baseline=max(float(energy.max(initial=0))*.001,1e-12)
    log=np.log(energy+baseline)
    strength=np.maximum(log-np.r_[np.repeat(log[0],3),log[:-3]]-.05,0)
    threshold=max(.35,float(np.percentile(strength,98))*.2)
    peaks=np.flatnonzero((strength[1:-1]>=strength[:-2])&(strength[1:-1]>strength[2:])&(strength[1:-1]>threshold))+1
    chosen=[]
    for frame in peaks:
        if chosen and frame-chosen[-1]<int(.09*rate/hop):
            if strength[frame]>strength[chosen[-1]]: chosen[-1]=int(frame)
        else: chosen.append(int(frame))
    return chosen


def multipitch(mono, rate=22050, start=0.0, min_midi=38, max_midi=88, polyphonic=True, threshold=.32):
    """Sparse harmonic fitting on an isolated guitar; not an instrument classifier."""
    size, hop = 4096, 220
    padded = np.pad(mono, (size//2,size//2))
    frames = np.lib.stride_tricks.sliding_window_view(padded,size)[::hop]
    frequencies = np.fft.rfftfreq(size,1/rate)
    bins = np.where((frequencies>=45)&(frequencies<=6000))[0]
    midis = np.arange(min_midi,max_midi+1)
    atoms = np.zeros((len(bins),len(midis)))
    fundamental_bins = []
    for column,midi in enumerate(midis):
        f0 = 440*2**((midi-69)/12)
        fundamental_bins.append(int(np.argmin(abs(frequencies[bins]-f0))))
        for harmonic in range(1,min(20,int(6000/f0))+1):
            # Hann main-lobe width, with an allowance for guitar inharmonicity.
            width = rate/size * (0.8+harmonic*.015)
            atoms[:,column] += np.exp(-.5*((frequencies[bins]-f0*harmonic)/width)**2) / harmonic**.8
    atoms /= np.maximum(np.linalg.norm(atoms,axis=0,keepdims=True),1e-9)
    gram = atoms.T@atoms
    coefficients = []
    rms_parts = []
    supports = []
    window = np.hanning(size)
    for begin in range(0,len(frames),256):
        chunk = frames[begin:begin+256]
        spectrum = np.abs(np.fft.rfft(chunk*window,axis=1))[:,bins].T
        background=np.median(np.lib.stride_tricks.sliding_window_view(np.pad(spectrum,((4,4),(0,0)),mode='edge'),9,axis=0),axis=-1)
        spectrum=np.maximum(spectrum-background*.7,0)
        projection = atoms.T@spectrum
        activation = np.maximum(projection,1e-9)
        # Sparse NNLS; fitting multiple notes together avoids labeling every
        # upper harmonic as a separate octave note.
        penalty = np.maximum(projection.max(axis=0,keepdims=True)*.035,1e-7)
        for _ in range(65):
            activation *= np.maximum(projection-penalty,0)/(gram@activation+1e-9)
        coefficients.append(activation)
        rms_parts.append(np.sqrt(np.mean(chunk*chunk,axis=1)))
        local_support = np.zeros_like(activation)
        for column,center in enumerate(fundamental_bins):
            local_support[column] = spectrum[max(0,center-1):center+2].max(axis=0)
        supports.append(local_support)
    activation = np.concatenate(coefficients,axis=1)
    rms = np.concatenate(rms_parts)
    fundamental = np.concatenate(supports,axis=1)
    if activation.size == 0 or rms.max(initial=0)<1e-5:
        return []
    # A short median filter removes single-frame semitone jumps without forcing
    # the notes to the beat grid or changing their measured onset times.
    if activation.shape[1]>4:
        activation = np.median(np.stack([np.roll(activation,i,axis=1) for i in (-2,-1,0,1,2)]),axis=0)
    maximum = activation.max(axis=0)
    active = activation > np.maximum(maximum[None,:]*(threshold if polyphonic else .55),activation.max()*.025)
    active &= fundamental > np.maximum(fundamental.max(axis=0,keepdims=True)*.025,1e-5)
    active &= rms[None,:] > max(float(np.percentile(rms,95))*.035,1e-5)
    if not polyphonic:
        active &= np.arange(len(midis))[:,None] == activation.argmax(axis=0)[None,:]
    # Limit simultaneous hypotheses to the six strings of the instrument.
    if polyphonic:
        rank = np.argsort(-activation,axis=0)
        keep = np.zeros_like(active)
        np.put_along_axis(keep,rank[:6],True,axis=0)
        active &= keep
    notes = []
    attacks=attack_frames(mono,rate,hop,active.shape[1])
    newly_active=np.diff(np.pad(active.astype(int),((0,0),(1,0))),axis=1)==1
    for row,midi in enumerate(midis):
        flags = active[row]
        # Bridge a single short dropout, but preserve repeated picked attacks.
        flags[1:-1] |= flags[:-2]&flags[2:]
        edges = np.diff(np.r_[False,flags,False].astype(int))
        for first,last in zip(np.flatnonzero(edges==1),np.flatnonzero(edges==-1)):
            cuts=[int(first)]
            for frame in attacks:
                if first+8<frame<last-8:
                    # A new chord tone is not a repeated pick of every sustaining string.
                    others=newly_active[:,max(0,frame-6):min(active.shape[1],frame+4)].copy()
                    others[row]=False
                    if not others.any(): cuts.append(frame)
            cuts.append(int(last))
            for a,b in zip(cuts,cuts[1:]):
                t0,t1 = a*hop/rate,min(len(mono)/rate,b*hop/rate)
                if t1-t0 < .075:
                    continue
                confidence = float(np.median(activation[row,a:b]/np.maximum(maximum[a:b],1e-9)))
                notes.append(dict(start=round(start+t0,5),duration=round(t1-t0,5),midi=int(midi),confidence=round(min(1,confidence),3)))
    return sorted(notes,key=lambda n:(n['start'],n['midi']))


def write_wav(path, stereo):
    data = np.clip(stereo.T,-1,1)
    with wave.open(str(path),'wb') as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(44100)
        w.writeframes((data*32767).astype('<i2').tobytes())


def separate_roformer(stereo, model_home):
    """Use the locally installed guitar checkpoint, without model-list downloads."""
    import logging
    import soundfile as sf
    from audio_separator.separator import Separator
    from audio_separator.separator.uvr_lib_v5.roformer import mel_band_roformer as architecture
    import torch
    home = Path(model_home) / 'guitar-roformer'
    checkpoint, config = home / 'becruily_guitar.ckpt', home / 'config.yaml'
    if not checkpoint.is_file() or not config.is_file():
        raise RuntimeError('새 기타 모델이 설치되지 않았습니다. 고급엔진설치.bat을 실행해주세요.')

    class LocalSeparator(Separator):
        def download_model_files(self, model_filename):
            # The upstream loader accepts this tuple; never fetch a global catalog.
            self.model_is_uvr_vip = False
            self.model_friendly_name = 'becruily guitar Mel-Band RoFormer'
            return (checkpoint.name, 'MDXC', self.model_friendly_name,
                    str(checkpoint), str(config))

    with tempfile.TemporaryDirectory(prefix='tabmaker-roformer-') as temp:
        source = Path(temp) / 'input.wav'
        sf.write(str(source), stereo.T, 44100, subtype='FLOAT')
        separator = LocalSeparator(
            log_level=logging.WARNING, model_file_dir=str(home), output_dir=temp,
            output_format='WAV', output_single_stem='Guitar', use_soundfile=True,
            normalization_threshold=1.0,
            mdxc_params=dict(segment_size=256, override_model_segment_size=False,
                             batch_size=1, overlap=4, pitch_shift=0))
        # audio-separator 0.48.0 accepts mlp_expansion_factor but omits it when
        # constructing MaskEstimator. This checkpoint requires factor 1, not 4.
        original_class = architecture.MelBandRoformer
        class ConfiguredRoformer(original_class):
            def __init__(self, **kwargs):
                super().__init__(**kwargs)
                factor = kwargs.get('mlp_expansion_factor', 4)
                if factor != 4:
                    self.mask_estimators = torch.nn.ModuleList([
                        architecture.MaskEstimator(
                            dim=kwargs['dim'], dim_inputs=estimator.dim_inputs,
                            depth=kwargs.get('mask_estimator_depth', 2),
                            mlp_expansion_factor=factor)
                        for estimator in self.mask_estimators])
        architecture.MelBandRoformer = ConfiguredRoformer
        try:
            separator.load_model(checkpoint.name)
        finally:
            architecture.MelBandRoformer = original_class
        emit('기타 전용 RoFormer로 분리하고 있어요', 15)
        outputs = separator.separate(str(source))
        if len(outputs) != 1:
            raise RuntimeError('기타 전용 모델의 출력이 하나가 아닙니다. 모델 설정을 확인해주세요.')
        path = Path(outputs[0])
        if not path.is_absolute(): path = Path(temp) / path
        data, rate = sf.read(str(path), dtype='float32', always_2d=True)
        if rate != 44100 or data.shape != stereo.T.shape or not np.isfinite(data).all():
            raise RuntimeError('분리 결과의 길이/샘플레이트/채널이 원곡 구간과 일치하지 않습니다.')
        return data.T.copy()


def basic_pitch_notes(stereo, start=0.0, polyphonic=True):
    import soundfile as sf
    from basic_pitch import FilenameSuffix, build_icassp_2022_model_path
    from basic_pitch.inference import predict
    # Select the shipped ONNX model explicitly, independent of installed TF.
    model = build_icassp_2022_model_path(FilenameSuffix.onnx)
    if not model.is_file():
        raise RuntimeError('Basic Pitch ONNX 모델이 없습니다. 고급엔진설치.bat을 다시 실행해주세요.')
    with tempfile.TemporaryDirectory(prefix='tabmaker-pitch-') as temp:
        path = Path(temp) / 'guitar.wav'
        sf.write(str(path), mono_for_analysis(stereo), 44100, subtype='FLOAT')
        _, _, events = predict(path, model, minimum_note_length=80,
                               minimum_frequency=440*2**((38-69)/12),
                               maximum_frequency=440*2**((88-69)/12))
    return convert_pitch_events(events, stereo.shape[1]/44100, start, polyphonic)


def convert_pitch_events(events, duration, start=0.0, polyphonic=True):
    notes = []
    for first, last, midi, strength, *_ in events:
        if not all(math.isfinite(float(x)) for x in (first, last, midi, strength)):
            raise ValueError('채보 모델에서 유효하지 않은 노트가 반환되었습니다.')
        first, last = max(0.0, float(first)), min(duration, float(last))
        if last <= first or not 38 <= midi <= 88: continue
        notes.append(dict(start=start+first, duration=last-first, midi=int(midi),
                          confidence=float(np.clip(strength, 0, 1))))
    if not polyphonic:
        # Monophonic selection is post-processing, not lead-guitar separation.
        edges = sorted({t for n in notes for t in (n['start'], n['start']+n['duration'])})
        selected = []
        for first, last in zip(edges, edges[1:]):
            candidates = [n for n in notes if n['start'] <= (first+last)/2 < n['start']+n['duration']]
            if not candidates: continue
            best = max(candidates, key=lambda n: (n['confidence'], n['midi']))
            identity = (best['start'], best['midi'])
            if selected and selected[-1][0] == identity and abs(selected[-1][1]['start']+selected[-1][1]['duration']-first)<1e-7:
                selected[-1][1]['duration'] = last-selected[-1][1]['start']
            else:
                selected.append((identity, dict(best, start=first, duration=last-first)))
        notes = [n for _, n in selected if n['duration'] >= .075]
    return sorted([dict(n, start=round(n['start'],5), duration=round(n['duration'],5),
                        confidence=round(n['confidence'],3)) for n in notes],
                  key=lambda n:(n['start'],n['midi']))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--input',required=True)
    parser.add_argument('--output',required=True)
    parser.add_argument('--start',type=float,default=0)
    parser.add_argument('--duration',type=float,required=True)
    parser.add_argument('--mode',choices=['mix','guitar'],default='mix')
    parser.add_argument('--part',choices=['poly','lead'],default='poly')
    parser.add_argument('--model-home')
    parser.add_argument('--threshold',type=float,default=.32)
    parser.add_argument('--separator',choices=['demucs','roformer'],default='demucs')
    parser.add_argument('--transcriber',choices=['harmonic','basic-pitch'],default='harmonic')
    args = parser.parse_args()
    emit('원곡 BPM과 박 위치를 분석하고 있어요',5)
    original = read_audio(args.input,args.start,args.duration)
    rhythm = tempo_and_beats(mono_for_analysis(original)[::4],11025,args.start)
    if args.mode == 'mix':
        if not args.model_home:
            raise RuntimeError('기타 분리 모델 경로를 지정해주세요.')
        guitar = (separate_roformer if args.separator=='roformer' else separate_guitar)(original,Path(args.model_home))
    else:
        guitar = original
    emit('기타의 여러 음과 실제 시작 시점을 채보하고 있어요',75)
    if args.transcriber == 'basic-pitch':
        candidates = basic_pitch_notes(guitar,args.start,polyphonic=True)
    else:
        candidates = multipitch(mono_for_analysis(guitar)[::2],22050,args.start,polyphonic=True,threshold=args.threshold)
    events = [(n['start']-args.start,n['start']-args.start+n['duration'],n['midi'],n['confidence']) for n in candidates]
    lead_notes = convert_pitch_events(events,guitar.shape[1]/44100,args.start,False)
    notes = candidates if args.part=='poly' else lead_notes
    output = Path(args.output)
    output.parent.mkdir(parents=True,exist_ok=True)
    stem_path = output.with_suffix('.guitar.wav')
    write_wav(stem_path,guitar)
    source = ('mel_band_roformer_guitar' if args.separator=='roformer' else 'demucs_6s_guitar') if args.mode=='mix' else 'isolated_guitar'
    result = dict(**rhythm,notes=notes,candidateNotes=candidates,leadNotes=lead_notes,source=source,transcriber=args.transcriber,
                  inputPath=str(Path(args.input).resolve()),part=args.part,
                  stem=str(stem_path),stemStart=args.start,downbeatDetected=False)
    temporary = output.with_suffix('.tmp')
    temporary.write_text(json.dumps(result,ensure_ascii=False,allow_nan=False),encoding='utf-8')
    temporary.replace(output)
    emit('분석 완료',100)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps(dict(error=str(error)),ensure_ascii=False),flush=True)
        raise SystemExit(1)
