"""Check a directed tour with TypeSafe's Jev: are its facts supported, and are its cues on the right bars?

Two checks, both judgments from a System One model (POST https://api.typesafe.ai/v1/systemone), with the
decisions kept in code:

* ``claims``: every spoken sentence of every narrated cue, against the evidence for the cue's sourceIds. Two
  questions per sentence, asked together over the same state: a Noul "is this a factual statement?" (a direction
  to the listener or an interpretation needs no source) and a Choice "how does the evidence relate to it?"
  (supports / contradicts / says nothing). A factual sentence is ``verified`` when the evidence supports it,
  ``contradicted`` or ``unsupported`` otherwise; anything decided below the confidence threshold goes to review.
* ``beats``: the director's own words for each beat of the story ("the second swell, with dissonant tension")
  against candidate passages of the score, each described from the score itself. One Choice per beat picks the
  passage; the report compares it with the bars the director's cue actually uses.

The evidence file (JSON) holds the source passages and score facts this tour relies on; keep copyrighted
excerpts short and out of git (SongLibraryData is not committed):

    {"sources": {"<sourceId>": ["passage", ...]},
     "beats": [{"beat": "<director's words>", "cue": "<from>-<to>"}],
     "windows": [{"id": "<id>", "bars": "<from>-<to>", "description": "<what the score does there>"}]}

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/tour_check.py claims <director.json> <evidence.json>
    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/tour_check.py beats <director.json> <evidence.json>

The API key comes from SongLibraryData/settings.json (typesafeKeyFile) or TYPESAFE_API_KEY and is only ever sent
in the Authorization header. Answers are cached by request (SongLibraryData/typesafe-cache.json), so a rerun with
unchanged text costs nothing and gives the same report.
"""
import argparse
import hashlib
import json
import os
import re
import time
import urllib.error
import urllib.request
from pathlib import Path

from common import DATA, atomic_json, read_json

ENDPOINT = 'https://api.typesafe.ai/v1/systemone'
MODEL = 'jev-latest'
ACCEPT = 0.8            # below this confidence a verdict goes to a person
FACTUAL = 0.5           # Noul threshold for "this sentence states a fact"
CACHE = DATA/'typesafe-cache.json'

RELATION = {
    'supports': 'The evidence states the sentence or directly implies that it is true',
    'contradicts': 'The evidence states the opposite of the sentence or implies that it is false',
    'says_nothing': 'The evidence does not address what the sentence asserts, either way',
}


def api_key():
    settings = read_json(DATA/'settings.json') or {}
    path = settings.get('typesafeKeyFile')
    if path and Path(path).exists():
        return Path(path).read_text(encoding='utf-8-sig').strip()
    if os.environ.get('TYPESAFE_API_KEY'):
        return os.environ['TYPESAFE_API_KEY'].strip()
    raise ValueError('Configure typesafeKeyFile in SongLibraryData/settings.json or set TYPESAFE_API_KEY')


class Jev:
    """A small System One client: cached by request, retried on 429/529/5xx with Retry-After (capped)."""

    def __init__(self, model=MODEL):
        self.model = model
        self.cache = read_json(CACHE) or {}
        self.key = None
        self.calls = 0

    def ask(self, state, questions):
        body = dict(model=self.model, state=state, questions=questions)
        identity = hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        if identity in self.cache:
            return self.cache[identity]
        self.key = self.key or api_key()
        request = urllib.request.Request(ENDPOINT, data=json.dumps(body).encode('utf-8'), method='POST',
                                         headers={'Authorization': 'Bearer ' + self.key, 'Content-Type': 'application/json'})
        delay = 1.0
        for attempt in range(6):
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    answer = json.loads(response.read().decode('utf-8'))
                break
            except urllib.error.HTTPError as error:
                if error.code not in (429, 529) and error.code < 500 or attempt == 5:
                    detail = error.read().decode('utf-8', 'replace')[:300]
                    raise RuntimeError(f'TypeSafe answered {error.code}: {detail}') from None
                wait = error.headers.get('Retry-After')
                time.sleep(min(30.0, float(wait)) if wait and wait.replace('.', '', 1).isdigit() else delay)
                delay = min(30.0, delay * 2)
        self.calls += 1
        result = dict(model=answer.get('model'), answers=answer['answers'])
        self.cache[identity] = result
        atomic_json(CACHE, self.cache)
        return result


def sentences(text):
    return [s.strip() for s in re.split(r'(?:(?<=[.!?])|(?<=[.!?][”"’\']))\s+(?=[A-Z“"\'])', text.strip()) if s.strip()]


def check_claims(director, evidence, jev):
    report = []
    for index, cue in enumerate(director['cues']):
        if cue.get('narrate', True) is False:
            continue
        speech = cue.get('speech') or cue['text']
        passages = {sid: evidence['sources'].get(sid, []) for sid in cue.get('sourceIds', [])}
        for sentence in sentences(speech):
            state = dict(sentence=sentence, context=speech, evidence=passages)
            result = jev.ask(state, {
                'factual': dict(type='noul', instructions=(
                    'Does `sentence` state something about the music, its history or its people that could be true or '
                    'false? Answer no for a direction to the listener ("listen", "notice"), a transition, or a feeling '
                    'or interpretation such as calling the music bittersweet.')),
                'relation': dict(type='choice', instructions=(
                    'How does `evidence` relate to the factual content of `sentence` (read it in the light of '
                    '`context`, the cue it belongs to)?'), criteria=RELATION),
            })
            answers = result['answers']
            factual = answers['factual']['noul']
            relation = answers['relation']
            if factual < FACTUAL:
                verdict, confidence = 'interpretation', 1 - factual
            else:
                verdict = {'supports': 'verified', 'contradicts': 'contradicted', 'says_nothing': 'unsupported'}[relation['choice']]
                confidence = relation.get('confidence') or max(relation['probabilities'].values())
            report.append(dict(cue=index, sentence=sentence, verdict=verdict, factual=round(factual, 3),
                               relation=relation['choice'], probabilities=relation['probabilities'],
                               confidence=round(confidence, 3), review=verdict != 'verified' and verdict != 'interpretation' or confidence < ACCEPT,
                               model=result.get('model')))
    return report


def check_beats(director, evidence, jev):
    windows = evidence['windows']
    criteria = {w['id']: f"bars {w['bars']}: {w['description']}" for w in windows}
    cues = {f"{c.get('from', c.get('start'))}-{c.get('to', c.get('end'))}": c for c in director['cues']}
    report = []
    for beat in evidence['beats']:
        result = jev.ask(dict(piece=director['title'], listener_description=beat['beat']), {
            'passage': dict(type='choice', instructions=(
                'A listener describes one moment of this piece in `listener_description`. Which passage of the score is '
                'that moment? Each option is described from the score.'), criteria=criteria),
        })
        answer = result['answers']['passage']
        chosen = next(w for w in windows if w['id'] == answer['choice'])
        cue = cues.get(beat['cue'])
        report.append(dict(beat=beat['beat'], cue=beat['cue'], chosen=chosen['id'], chosenBars=chosen['bars'],
                           agrees=chosen.get('cue') == beat['cue'] or chosen['bars'] == beat['cue'],
                           confidence=round(answer.get('confidence') or max(answer['probabilities'].values()), 3),
                           probabilities=answer['probabilities'], cueFound=cue is not None, model=result.get('model')))
    return report


def main():
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('check', choices=['claims', 'beats'])
    p.add_argument('director')
    p.add_argument('evidence')
    p.add_argument('--model', default=MODEL)
    p.add_argument('--out', help='write the report here as JSON')
    a = p.parse_args()
    director = json.loads(Path(a.director).read_text(encoding='utf-8'))
    evidence = json.loads(Path(a.evidence).read_text(encoding='utf-8'))
    jev = Jev(a.model)
    report = check_claims(director, evidence, jev) if a.check == 'claims' else check_beats(director, evidence, jev)
    if a.out:
        Path(a.out).write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding='utf-8')
    for row in report:
        if a.check == 'claims':
            flag = '  <-- review' if row['review'] else ''
            print(f"[{row['cue']:2d}] {row['verdict']:<14} {row['confidence']:.2f}  {row['sentence']}{flag}")
        else:
            flag = 'agrees' if row['agrees'] else 'DIFFERS'
            print(f"{flag:<8} {row['confidence']:.2f}  cue {row['cue']:<10} Jev: {row['chosen']} (bars {row['chosenBars']})  | {row['beat']}")
    print(f'{jev.calls} new request(s); model {next((r["model"] for r in report), a.model)}')


if __name__ == '__main__':
    main()
