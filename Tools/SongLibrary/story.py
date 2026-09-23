"""Generate a portable, validated director script from completed offline analysis.

Only a compact musical summary is sent. Audio, MIDI, paths and credentials are not
included in the model input. Never log HTTP headers or unsanitized API errors.
"""
import bisect
import collections
import json
import math
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urlparse
from common import atomic_json, read_json, sha, validate_bundle

VIEWS = ('Overview', 'Torus', 'Timeline', 'Drums')


def load_context(bundle):
    path = Path(bundle)/'story.context.json'
    if not path.exists():
        return dict(version=1, interpretiveLens='', sources=[], facts=[])
    if path.stat().st_size > 65536:
        raise ValueError('Story context exceeds 64 KB')
    context = json.loads(path.read_text(encoding='utf-8'))
    if context.get('version') != 1 or not isinstance(context.get('interpretiveLens',''), str):
        raise ValueError('Invalid story context')
    sources = context.get('sources', [])
    if not isinstance(sources,list) or len(sources)>20:
        raise ValueError('Invalid story sources')
    ids = set()
    for source in sources:
        if not all(isinstance(source.get(k),str) and source[k] for k in ('id','title','url')):
            raise ValueError('Incomplete story source')
        if source['id'] in ids or urlparse(source['url']).scheme != 'https':
            raise ValueError('Invalid or duplicate story source')
        ids.add(source['id'])
    facts = context.get('facts', [])
    if not isinstance(facts,list) or len(facts)>40:
        raise ValueError('Invalid story facts')
    for fact in facts:
        if not isinstance(fact.get('text'),str) or not fact['text'] or not fact.get('sourceIds') or any(s not in ids for s in fact['sourceIds']):
            raise ValueError('Historical facts need verified source references')
    if 'scenePlan' in context:
        if not isinstance(context['scenePlan'],list) or not 1<=len(context['scenePlan'])<=100:
            raise ValueError('Invalid editorial scene plan')
    return context


def seconds_at(data, beat):
    tempos = data['Tempos']
    t = tempos[max(0, bisect.bisect_right([t['Beat'] for t in tempos], beat)-1)]
    return t['Seconds'] + (beat-t['Beat'])*t['Microseconds']/1e6


def summarize(data, manifest):
    names = ('A', 'Bb', 'B', 'C', 'C#', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab')
    sections = []
    for section in data['Sections']:
        chords = [c for c in data.get('RegionPhases', data['Chords']) if not c['Rest'] and section['Start'] <= c['Start'] < section['End']]
        progression = []
        for c in chords:
            name = names[c['Root'] % 12] + c['Quality']
            if not progression or progression[-1] != name:
                progression.append(name)
        start, end = seconds_at(data, section['Start']), seconds_at(data, section['End'])
        strands = [s for s in data['MelodyStrands'] if s['Track'] == data['LeadVocalTrack']]
        onsets = sorted({n['Start'] for s in strands for n in s['Notes'] if start <= n['Start'] < end})
        intervals = collections.Counter()
        for t in onsets:
            pitches = sorted({n['Pitch'] for s in strands for n in s['Notes'] if n['Start'] <= t+1e-6 < n['End']})
            if len(pitches) == 2:
                intervals[pitches[1]-pitches[0]] += 1
        interval_names={0:'unison',1:'minor second',2:'major second',3:'minor third',4:'major third',5:'perfect fourth',6:'tritone',7:'perfect fifth',8:'minor sixth',9:'major sixth',10:'minor seventh',11:'major seventh',12:'octave'}
        sections.append(dict(name=section['Name'], label=section.get('Label') or section['Name'], variation=section.get('Variation', ''),
                             start=round(start, 3), end=round(min(end, manifest['audioDuration']), 3),
                             estimatedStableProgression=progression[:24], vocalIntervals=[dict(semitones=k, interval=interval_names.get(k,'compound interval'), onsetCount=v) for k,v in intervals.most_common()]))
    return dict(title=data.get('Title') or 'Prepared song', duration=manifest['audioDuration'],
                key=names[data['Key'] % 12], minor=data['Minor'], sectionEvidence=data.get('Provenance'),
                formGrammar=data.get('FormGrammar', ''),
                warning='MIDI approximates the recording; section labels may be estimates. Intervals are score evidence, not verified vocal identity.',
                stems=[dict(id=s['id'], name=s['name']) for s in manifest.get('stems', [])], sections=sections)


def validate_cues(cues, duration, stems, source_ids=()):
    if not isinstance(cues, list) or not 1 <= len(cues) <= 100:
        raise ValueError('Story must contain 1–100 cues')
    last = 0
    for cue in cues:
        start, end = cue['start'], cue['end']
        if not all(isinstance(t, (int, float)) and not isinstance(t, bool) and math.isfinite(t) for t in (start, end)):
            raise ValueError('Invalid cue clock')
        if start < last or end <= start or end > duration+.001:
            raise ValueError('Overlapping or out-of-range story cues')
        if cue['view'] not in VIEWS or cue['stem'] not in {'', *stems}:
            raise ValueError('Unavailable view or stem')
        if not isinstance(cue['uncoil'], bool) or (cue['uncoil'] and cue['view'] != 'Torus'):
            raise ValueError('Uncoil requires the Torus view')
        if not isinstance(cue['text'], str) or not 1 <= len(cue['text']) <= 450:
            raise ValueError('Invalid story caption')
        refs=cue.get('sourceIds',[])
        if not isinstance(cue.get('narrate',True),bool):raise ValueError('Invalid narration flag')
        if cue.get('annotationTarget','none') not in ('none','melody','drums','patterns') or not isinstance(cue.get('annotationLabel',''),str) or len(cue.get('annotationLabel',''))>70:
            raise ValueError('Invalid story annotation')
        if not isinstance(refs,list) or any(s not in source_ids for s in refs):
            raise ValueError('Unknown story source reference')
        last = end
    return cues


def settle_transitions(cues):
    # A narration change can occur inside one continuous uncoiled view. Do not
    # start a six-second transition for a passage too short to settle and read.
    first = 0
    while first < len(cues):
        if not cues[first]['uncoil']:
            first += 1
            continue
        last = first
        while last+1 < len(cues) and cues[last+1]['uncoil'] and abs(cues[last]['end']-cues[last+1]['start']) < .001:
            last += 1
        if cues[last]['end']-cues[first]['start'] < 14:
            for cue in cues[first:last+1]:
                cue['uncoil'] = False
        first = last+1
    return cues


def generate(bundle, key_file, model='gpt-4.1'):
    bundle = Path(bundle).resolve()
    data = validate_bundle(bundle)
    manifest = read_json(bundle/'aligned.mid.prepared.json')
    summary = summarize(data, manifest)
    context = load_context(bundle)
    plan=context.get('scenePlan')
    properties = dict(start={'type':'number'}, end={'type':'number'}, text={'type':'string'},
                      view={'type':'string','enum':list(VIEWS)}, uncoil={'type':'boolean'},
                      stem={'type':'string','enum':['']+[s['id'] for s in manifest.get('stems', [])]},
                      sourceIds={'type':'array','items':{'type':'string'}},
                      annotationTarget={'type':'string','enum':['none','melody','drums','patterns']},annotationLabel={'type':'string'},narrate={'type':'boolean'})
    schema = dict(type='object', properties={'cues':dict(type='array', items=dict(type='object', properties=properties,
                  required=list(properties), additionalProperties=False))}, required=['cues'], additionalProperties=False)
    if plan:
        validate_cues([dict(c,text=c['focus']) for c in plan],manifest['audioDuration'],[s['id'] for s in manifest.get('stems',[])])
        fields=dict(scene={'type':'integer'},text={'type':'string'},sourceIds={'type':'array','items':{'type':'string'}})
        schema=dict(type='object',properties={'captions':dict(type='array',items=dict(type='object',properties=fields,required=list(fields),additionalProperties=False))},required=['captions'],additionalProperties=False)
    prompt = (
        'Write a researched music-history listening story with a feeling-led narrative, not a list of abstract metaphors. '
        'Choose one emotional thread and develop it across the song: an opening feeling, a complication, a moment of connection, '
        'and an ending that changes how the beginning feels. Let a recurring image evolve rather than repeat the same metaphor. '
        'Start with the audible opening instrument and its verified performer, then establish the home key and the broad pop form early. Distinguish intro riffs from later solos when assigning credits. '
        'Use specific recording history, credited contributions and documented influences throughout, tied to the passage being heard. Let music theory explain the emotional effect in plain language; avoid chord catalogs. '
        'Use music history to give the people and their creative choices presence, based ONLY on supplied sourced facts. '
        'Differentiate history from a poetic listening interpretation. A duet may suggest mutual support; do not claim it proves '
        'the musicians felt a particular emotion or describes their entire relationship. Avoid invented studio dialogue or motives. '
        'Do not turn disputed authorship, firsts or inventions into settled facts. Credit collaborative contributions fairly. '
        'Let silence, texture, rhythmic pull and the act of one part supporting another carry the story. '
        'Return timed cues, '
        'chronological and nonoverlapping, seconds from the start of the recording. Honor the supplied exact section boundaries. Cover the whole song with '
        'roughly 12–20 cues, no more than two short sentences/280 characters each. Use supplied evidence only. '
        'Aim for two thirds narration and one third uninterrupted music. Allow at least 0.45 seconds per spoken word. '
        'Reserve whole listening scenes with narrate=false, especially after explaining an audible detail; keep captions brief there. '
        'Do not fill every scene with speech. Protect instrumental entrances and give harmonies time to be heard. '
        'Use occasional annotationTarget melody, drums or patterns to point at the featured visual, with a short annotationLabel; otherwise none and an empty label. '
        'No lyric quotations. No biographical claims outside the sourced context. Treat section labels as a listening map. '
        'onsetCount is a count of occurrences, NEVER an interval size. '
        'Use interval names provided; an octave is 12 semitones, a major third is 4. Distinguish scored notes from measured singing. '
        'Chord estimates are fallible: favor broad stable tonal relationships, not assertions about every passing chord. '
        'Singer credits may be used if sourced, but do not label a particular visual tracer as a named singer without evidence. '
        'Stem empty string means full mix; mostly full mix, with a few purposeful solos of at least 7 seconds. '
        'Make solos and view changes reveal the narrative point rather than just decorate it. If there are repeated bridges with two vocal voices, '
        'hold a vocal solo through the first bridge in uncoiled view and return to that image in coiled view at the next bridge. '
        'Audio AND visuals solo together, but chord colors retain full-song context. '
        'Overview shows all; Timeline features nested pattern wheels; Drums shows overhead percussion; Torus shows '
        'harmony. Use Torus uncoil=true for one sustained passage of at least 14 seconds, as transition takes 6 seconds. '
        'Leave ample time between view changes. Begin with Overview full mix and finish full mix. '
        'For captions containing historical facts, put the supporting context source ids in sourceIds; '
        'pure listening interpretations and score observations use an empty array. Do not print citation codes in caption text. '
        'The score uses A-based pitch classes. '
        + ('An editor has supplied scenePlan. Return exactly one caption for every scene in order, numbered from 0. '
           'Its focus is the required point for that scene: include the named historical people and supported creative contribution when requested. '
           'The scene plan already controls timing, solos and views; write captions only. Do not omit history or drum/bass moments in favor of abstract emotion. ' if plan else '')
        + 'Musical analysis and context follow:\n' + json.dumps(dict(analysis=summary, editorialContext=context)))
    body = dict(model=model, store=False, input=prompt, max_output_tokens=6500,
                text={'format':dict(type='json_schema', name='song_director', strict=True, schema=schema)})
    key = Path(key_file).read_text(encoding='utf-8-sig').strip()
    if not key or '\n' in key or '\r' in key:
        raise ValueError('Key file must contain one API key')
    request = urllib.request.Request('https://api.openai.com/v1/responses', data=json.dumps(body).encode(),
        headers={'Authorization':'Bearer '+key, 'Content-Type':'application/json'}, method='POST')
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            result = json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f'OpenAI request failed (HTTP {error.code}); no response body or credentials logged') from None
    except urllib.error.URLError:
        raise RuntimeError('OpenAI request could not connect; credentials were not logged') from None
    finally:
        key = None
    if result.get('status') != 'completed':
        raise ValueError('OpenAI did not complete the story; existing story retained')
    output = ''.join(c.get('text','') for item in result.get('output',[]) for c in item.get('content',[]) if c.get('type')=='output_text')
    parsed=json.loads(output)
    if plan:
        captions=parsed['captions']
        if len(captions)!=len(plan) or [c['scene'] for c in captions]!=list(range(len(plan))):
            raise ValueError('Story omitted or reordered an editorial scene')
        generated=[dict(start=c['start'],end=c['end'],view=c['view'],uncoil=c['uncoil'],stem=c['stem'],text=caption['text'],sourceIds=caption['sourceIds']) for c,caption in zip(plan,captions)]
        for cue,scene in zip(generated,plan):
            cue.update(annotationTarget=scene.get('annotationTarget','none'),annotationLabel=scene.get('annotationLabel',''))
            for key in ('narrate','narrationStart','narrationEnd','releaseSoloAfterNarration'):
                if key in scene:cue[key]=scene[key]
    else:generated=parsed['cues']
    cues = settle_transitions(validate_cues(generated, manifest['audioDuration'], [s['id'] for s in manifest.get('stems',[])], [s['id'] for s in context.get('sources',[])]))
    story = dict(version=1, title=summary['title'], midiSha256=manifest['midiSha256'], audioSha256=manifest['audioSha256'],
                 patternsSha256=sha(bundle/'aligned.mid.patterns.json'), duration=manifest['audioDuration'],
                 model=result.get('model',model), narrativeStyle='feeling-led / sourced context',narrationTargetFraction=2/3,
                 contextSha256=sha(bundle/'story.context.json') if (bundle/'story.context.json').exists() else None,
                 sources=context.get('sources',[]), cues=cues)
    atomic_json(bundle/'story.json', story)
    return dict(path=str(bundle/'story.json'), cues=len(cues), model=story['model'])
