"""Isolated local GPU transcription worker. Audio never leaves this machine."""
import argparse
from pathlib import Path
from common import DATA, sha

MODEL='mc13_256_g4_all_v7_mt3f_sqr_rms_moe_wf4_n8k2_silu_rope_rp_b36_nops'
HASH='ae38e415c79efd5592dcb9b658cdb99ddb11d4c4e1eaa364cab04a052473fc25'
CHECKPOINT=DATA/'models'/MODEL/'last.ckpt'
URL='https://huggingface.co/spaces/mimbres/YourMT3/resolve/main/amt/logs/2024/'+MODEL+'/checkpoints/last.ckpt'

def install():
    if CHECKPOINT.exists() and sha(CHECKPOINT)==HASH: return
    import requests
    CHECKPOINT.parent.mkdir(parents=True,exist_ok=True)
    temp=CHECKPOINT.with_suffix('.download')
    with requests.get(URL,stream=True,timeout=(20,120)) as response:
        response.raise_for_status()
        with temp.open('wb') as stream:
            for block in response.iter_content(1024*1024): stream.write(block)
    if sha(temp)!=HASH: raise ValueError('Model checkpoint checksum mismatch')
    temp.replace(CHECKPOINT)

def main():
    p=argparse.ArgumentParser();p.add_argument('--install',action='store_true');p.add_argument('--audio');p.add_argument('--output');p.add_argument('--device',default='auto');a=p.parse_args()
    if a.install: install();print(CHECKPOINT);return
    if not CHECKPOINT.exists() or sha(CHECKPOINT)!=HASH: raise ValueError('Run setup.ps1 to install the verified local model')
    import librosa
    from mt3_infer.adapters.yourmt3 import YourMT3Adapter
    adapter=YourMT3Adapter(model_key='yptf_moe_nops')
    adapter.load_model(checkpoint_path=str(CHECKPOINT),device=a.device)
    audio,_=librosa.load(a.audio,sr=16000,mono=True)
    midi=adapter.transcribe(audio,sr=16000)
    midi.save(a.output)
    print('Saved local multitrack transcription:',a.output)

if __name__=='__main__': main()
