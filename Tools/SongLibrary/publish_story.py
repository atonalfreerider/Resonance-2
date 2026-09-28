"""Publish a hand-directed story: a bundle's story.draft.json becomes the story.json that Director
mode plays, stamped with the bundle's current hashes and validated like a generated story.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/publish_story.py PreparedSongs/Library/<song>

The draft holds what a director writes: the narrator (a Cartesia voice), the sources, the history
pictures and the timed cues. A cue names a picture by key; the picture's file (a freely licensed
image, usually from Wikimedia Commons) is copied into the bundle's story/pictures folder, no
larger than 720 px on its long edge, and its caption and licence credit go onto the cue.
Then narrate it:

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<song>
"""
import json
import sys
from pathlib import Path
from PIL import Image
from common import atomic_json, read_json, sha, validate_bundle
from story import settle_transitions, validate_cues

PICTURES = 'story/pictures'
LONG_EDGE = 720
CUE_DEFAULTS = dict(view='Overview', stem='', uncoil=False, annotationTarget='none', annotationLabel='', sourceIds=[])


def copy_picture(bundle, key, picture, draft_folder):
    source = Path(picture['file'])
    if not source.is_absolute():
        source = (draft_folder/source) if (draft_folder/source).exists() else (bundle/source)
    target = bundle/PICTURES/(key+'.jpg')
    target.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as image:
        image = image.convert('RGB')
        image.thumbnail((LONG_EDGE, LONG_EDGE), Image.LANCZOS)
        image.save(target, 'JPEG', quality=86, optimize=True, progressive=False)
    return target.relative_to(bundle).as_posix()


def publish(bundle, draft_path=None):
    bundle = Path(bundle).resolve()
    validate_bundle(bundle)
    draft_path = Path(draft_path) if draft_path else bundle/'story.draft.json'
    draft = json.loads(draft_path.read_text(encoding='utf-8'))
    manifest = read_json(bundle/'aligned.mid.prepared.json')
    pictures = draft.get('pictures', {})
    for key, picture in pictures.items():
        for field in ('file', 'caption', 'credit', 'commonsPage', 'license'):
            if not picture.get(field):
                raise ValueError(f'Picture {key} needs {field}')
    placed = {key: copy_picture(bundle, key, picture, draft_path.parent) for key, picture in pictures.items()}
    # A picture brought in from outside the bundle now lives in it; the draft points there.
    if any(picture['file'] != placed[key] for key, picture in pictures.items()):
        for key, picture in pictures.items():
            picture['file'] = placed[key]
        draft_path.write_text(json.dumps(draft, indent=1, ensure_ascii=False), encoding='utf-8')
    cues = []
    for cue in draft['cues']:
        cue = {**CUE_DEFAULTS, **cue}
        key = cue.pop('picture', None)
        if key:
            if key not in placed:
                raise ValueError(f'Unknown picture {key}')
            cue.update(image=placed[key], imageCaption=pictures[key]['caption'], imageCredit=pictures[key]['credit'])
            if pictures[key].get('placement'):
                cue.setdefault('imagePlacement', pictures[key]['placement'])
        cues.append(cue)
    sources = draft.get('sources', [])
    cues = settle_transitions(validate_cues(cues, manifest['audioDuration'], [s['id'] for s in manifest.get('stems', [])],
                                            [s['id'] for s in sources], bundle))
    used = {c.get('image') for c in cues}
    story = dict(version=1, title=draft['title'], midiSha256=manifest['midiSha256'], audioSha256=manifest['audioSha256'],
                 patternsSha256=sha(bundle/'aligned.mid.patterns.json'), duration=manifest['audioDuration'],
                 model='hand-directed', narrativeStyle=draft.get('narrativeStyle', 'directed tour: history and the Resonance views'),
                 narrationTargetFraction=draft.get('narrationTargetFraction', 2/3), narrator=draft['narrator'],
                 sources=sources,
                 pictures=[dict(image=placed[k], caption=p['caption'], credit=p['credit'], license=p['license'], commonsPage=p['commonsPage'])
                           for k, p in pictures.items() if placed[k] in used],
                 cues=cues)
    atomic_json(bundle/'story.json', story)
    spoken = [c for c in cues if c.get('narrate', True)]
    words = sum(len((c.get('speech') or c['text']).split()) for c in spoken)
    return dict(path=str(bundle/'story.json'), cues=len(cues), spoken=len(spoken), words=words, pictures=len(story['pictures']))


if __name__ == '__main__':
    print(json.dumps(publish(*sys.argv[1:3]), indent=2))
