from pathlib import Path
import json
import sys
import tempfile
import numpy as np
from transcribe import tempo_and_beats, multipitch, separate_guitar, mono_for_analysis


def pluck(midis, seconds=1.6, rate=22050):
    t=np.arange(int(seconds*rate))/rate
    signal=np.zeros_like(t)
    for midi in midis:
        f0=440*2**((midi-69)/12)
        for harmonic in range(1,12):
            signal += np.sin(2*np.pi*f0*harmonic*t)/(harmonic**.8*len(midis))
    return signal*.12*np.minimum(1,t/.01)*np.exp(-t*.5)


def test():
    checks=[]
    for bpm in (80,100,120,150,180):
        rate=11025; samples=np.zeros(rate*16)
        rng=np.random.default_rng(42)
        for t in np.arange(.25,15.8,60/bpm):
            first=int(t*rate); count=min(700,len(samples)-first)
            samples[first:first+count]+=rng.normal(size=count)*np.exp(-np.arange(count)/80)
        result=tempo_and_beats(samples,rate)
        assert abs(result['bpm']-bpm)<2,(bpm,result)
        actual=np.array(result['beats']); expected=.25+np.arange(50)*60/bpm
        errors=np.min(abs(actual[:,None]-expected[None,:]),axis=1)
        assert np.median(errors)<.04,(bpm,np.median(errors))
        checks.append(f'{bpm} BPM with beat phase within 40ms')
    assert tempo_and_beats(np.zeros(11025*8),11025)['bpm']==0
    checks.append('Silence has no invented BPM')
    for midis in ([40],[64],[69],[40,47,52],[52,55,59],[64,67,71]):
        notes=multipitch(pluck(midis))
        prominent={n['midi'] for n in notes if n['duration']>.6}
        assert prominent==set(midis),(midis,notes)
        checks.append('Harmonic guitar '+str(midis))
    quiet=multipitch(pluck([64])*.025)
    assert any(n['midi']==64 for n in quiet),'Quiet guitar'
    checks.append('Quiet isolated guitar')
    assert multipitch(np.zeros(22050*2))==[]
    checks.append('Silent stem produces no notes')
    distorted=multipitch(np.tanh(pluck([52,55,59])*8))
    assert {n['midi'] for n in distorted if n['duration']>.6}=={52,55,59},distorted
    checks.append('Distorted harmonic chord')
    repeated=multipitch(np.tile(pluck([64],.4),4))
    assert len([n for n in repeated if n['midi']==64])==4,repeated
    checks.append('Repeated same-pitch picks are split')
    signal=pluck([64])
    assert np.max(np.abs(mono_for_analysis(np.stack([signal,-signal]))))>.01
    checks.append('Phase-inverted stereo guitar is not cancelled')
    print(json.dumps(dict(passed=checks),ensure_ascii=False,indent=2))


if __name__=='__main__':
    test()
    if len(sys.argv)>1:
        rate=44100
        guitar=np.tile(pluck([52,55,59],2,rate),4)
        t=np.arange(len(guitar))/rate
        mixture=guitar+.08*np.sin(2*np.pi*55*t)
        stereo=np.stack([mixture,mixture*.92])
        result=separate_guitar(stereo,Path(sys.argv[1]))
        assert result.shape==stereo.shape and np.isfinite(result).all()
        assert np.sqrt(np.mean(result**2))>1e-7
        print(json.dumps(dict(real_demucs_inference=True,samples=result.shape[1],
                              note_accuracy_claim=False,notes=multipitch(result.mean(axis=0)[::2])),indent=2))
