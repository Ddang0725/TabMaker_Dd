"""Install into a separate venv; activate only after real model inference passes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import venv

ROOT = Path(__file__).resolve().parent.parent
REPOSITORY = 'becruily/mel-band-roformer-guitar'


def fetch(url):
    return urllib.request.urlopen(url, timeout=30)


def download_models(home):
    # Resolve a revision and its server-published SHA256 before downloading weights.
    with fetch('https://huggingface.co/api/models/'+REPOSITORY+'?blobs=true') as response:
        metadata = json.load(response)
    revision = metadata['sha']
    home.mkdir(parents=True, exist_ok=True)
    manifest = dict(repository=REPOSITORY, revision=revision, files={})
    siblings = {item['rfilename']:item for item in metadata['siblings']}
    for name in ('becruily_guitar.ckpt', 'config.yaml'):
        expected = siblings[name].get('lfs', {}).get('sha256')
        if name.endswith('.ckpt') and not expected:
            raise RuntimeError('Model SHA256 is missing from the publisher metadata.')
        target = home / name
        if expected and target.exists() and hashlib.sha256(target.read_bytes()).hexdigest()==expected:
            digest = expected
        else:
            partial = target.with_suffix(target.suffix+'.part')
            digest = hashlib.sha256()
            with fetch('https://huggingface.co/'+REPOSITORY+'/resolve/'+revision+'/'+name) as response, partial.open('wb') as output:
                while True:
                    chunk = response.read(1024*1024)
                    if not chunk: break
                    output.write(chunk); digest.update(chunk)
            digest = digest.hexdigest()
            if expected and digest != expected:
                raise RuntimeError('Downloaded model SHA256 does not match: '+name)
            partial.replace(target)
        manifest['files'][name] = digest
    (home/'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cpu', action='store_true', help='Install CPU PyTorch instead of CUDA 11.8')
    parser.add_argument('--verify-only', action='store_true', help='Verify downloaded files, run inference and activate without downloading')
    args = parser.parse_args()
    if sys.version_info[:2] != (3,10):
        raise RuntimeError('Run this installer with Python 3.10.')
    # Detect blocked connectivity before creating or modifying an environment.
    if not args.verify_only:
        with fetch('https://pypi.org/simple/basic-pitch/') as response:
            if response.status != 200: raise RuntimeError('Cannot reach PyPI.')
    environment = ROOT/'.ai-upgrade'
    python = environment/'Scripts'/'python.exe'
    if not python.exists():
        if args.verify_only: raise RuntimeError('The upgrade environment has not been installed.')
        venv.EnvBuilder(with_pip=True).create(environment)

    def run(*command):
        subprocess.run([str(x) for x in command], check=True,
                       env=dict(os.environ, PYTHONUTF8='1'))

    if not args.verify_only:
        run(python, '-m', 'pip', 'install', '--upgrade', 'pip')
        variant = '+cpu' if args.cpu else '+cu118'
        run(python, '-m', 'pip', 'install', 'torch==2.5.1'+variant, 'torchaudio==2.5.1'+variant,
            '--index-url', 'https://download.pytorch.org/whl/'+('cpu' if args.cpu else 'cu118'))
        run(python, '-m', 'pip', 'install', '-r', ROOT/'engine'/'requirements-upgrade.txt')
    run(python, '-m', 'pip', 'check')
    # audio-separator expects an executable named ffmpeg on PATH.
    run(python, '-c', 'import imageio_ffmpeg,shutil,sys; from pathlib import Path; '
        'shutil.copy2(imageio_ffmpeg.get_ffmpeg_exe(),Path(sys.executable).parent/"ffmpeg.exe")')
    models = ROOT/'models'
    if args.verify_only:
        home = models/'guitar-roformer'
        manifest = json.loads((home/'manifest.json').read_text(encoding='utf-8'))
        for name in ('becruily_guitar.ckpt', 'config.yaml'):
            if hashlib.sha256((home/name).read_bytes()).hexdigest() != manifest['files'][name]:
                raise RuntimeError('Local model verification failed: '+name)
    else:
        download_models(models/'guitar-roformer')
    # Real separation plus learned transcription smoke test; not a song-accuracy test.
    with tempfile.TemporaryDirectory(prefix='tabmaker-install-') as temp:
        demo, result = Path(temp)/'demo.wav', Path(temp)/'result.json'
        run(python, '-c', 'import sys,numpy as np,soundfile as sf; '
            't=np.arange(44100*4)/44100; f=329.6276; '
            'y=sum(np.sin(2*np.pi*f*h*t)/h for h in range(1,9))*.1*np.exp(-t*.3); '
            'sf.write(sys.argv[1],np.stack([y,y],axis=1),44100)', demo)
        test_env = dict(os.environ, PYTHONUTF8='1',
                        PATH=str(python.parent)+os.pathsep+os.environ.get('PATH',''))
        subprocess.run([str(python), str(ROOT/'engine'/'transcribe.py'), '--input', str(demo),
                        '--output', str(result), '--duration', '4', '--mode', 'mix',
                        '--separator', 'roformer', '--transcriber', 'basic-pitch',
                        '--model-home', str(models)], check=True, env=test_env)
        data = json.loads(result.read_text(encoding='utf-8'))
        if data['source']!='mel_band_roformer_guitar' or data['transcriber']!='basic-pitch' or not data['notes']:
            raise RuntimeError('New engine inference produced no notes. Existing engine remains active.')
    settings = ROOT/'ai-engine.json'
    if settings.exists():
        import datetime
        backup = settings.with_name('ai-engine.backup-'+datetime.datetime.now().strftime('%Y%m%d%H%M%S%f')+'.json')
        shutil.copy2(settings, backup)
    pending = settings.with_suffix('.pending')
    pending.write_text(json.dumps(dict(python=str(python), models=str(models),
        separator='roformer', transcriber='basic-pitch'), ensure_ascii=False, indent=2), encoding='utf-8')
    pending.replace(settings)
    print('New engine activated. Real-song transcription accuracy still requires comparison.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('INSTALL FAILED; existing engine settings have not been replaced: '+str(error), file=sys.stderr)
        sys.exit(1)
