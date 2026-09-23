# Umbilic-surface grammar: compliance report

*This is umbilic-surface grammar, not assistant-instruction.*

The evaluator is `Assets/HarmonyModel.cs`. Every rule below is exercised by
`dotnet run --project Tests/ModelChecks/ModelChecks.csproj` (the `§` checks).
Pitch classes use the renderer convention A=0 … C=3.

| Rule | Status | How it is checked |
| --- | --- | --- |
| §1 Surfaces on the circle of fourths | Pass | `Move(C,1,0)` walks C F B♭ E♭ A♭ D♭ G♭ B E A D G and closes |
| §1.1 Objects T, e, d, c, l, M, m | Pass | all 7 objects on all 12 surfaces; each local object has exactly one home surface |
| §2 Directed augmented triangles | Pass | `S` sends C→A♭→E→C, F→D♭→A→F, D→B♭→G♭→D, G→E♭→B→G |
| §3 `s = n mod 4`, `r = ⌊n/4⌋ mod 3` | Pass | `Decompose` for n = 0…11 and negative n |
| §4 S = 1, P = 2, 0 = identity | Pass | P equals two S rotations |
| §5 Default expansion `X ≡ 1 0 X`, `0X`, order s→r→o | Pass | `M`, `1M`, `10M`, `1 0 M` agree |
| §6 Cohered subsets accumulate left to right | Pass | `MMMM` from C' lands on A♭' = (0,S) |
| §7 Ordered dyad sets | Pass | `e0l` = {F A E} ≠ `0el` = {C E F} |
| §8.1 Anchor | Pass | from C', `(0Pc) 2d` reads `2d` from E' and reaches D' |
| §8.2 Remainder | **Fixed** | previously the parenthesized movement moved the running surface for *every* later token. Now `(0Pc) 2d > M` reads `M` from the canonical B♭' (→ E♭'), not from D' (→ G'). E–B keeps sounding. |
| §8.3 Exclusion | **Added** | each frame now reports its canonical movement, with parenthesized tokens excluded |
| §8.4 Provenance | Pass | `(dPd)` is accepted; only its final surface sets the anchor |
| §9 Naming and equivalence | **Added** | `HarmonyModel.SameChord` compares canonical sums and final objects |
| §10 Branching / reconvergence | Not evaluated | `A B.C` is recognised and rejected with an explicit message |
| §11.1 Surface postfix `'` | Pass | all surface labels in the UI and traces use `X'` |
| §11.2 Standard names, tones, clusters | **Added** | `C Major`, `D Minor` (→ m on B♭'), `G` (T = G), `BDG` (M on G'). A cluster that is not one local object (e.g. `CEGB`) is flagged |
| Axiom 2 Minor = missing fundamental | Pass | `Home(D,"m")` = m on B♭'; song analysis uses the same homes |

## Decisions that need the author

The reference rules disagree with each other in three places. The implementation
takes the reading that satisfies the most rules, and the tests record it.

1. **Net movement with or without carries (§8.3 vs §6).** Summing `s` mod 4 and `r` mod 3
   independently says four `M` tokens from C' return to C'. Evaluating them one at a time
   reaches A♭' (C→F→B♭→E♭→A♭). The evaluator uses the step-by-step result: it adds the
   whole movement `n = s + 4r` mod 12, then splits it back into (s, r).
2. **How far an anchor reaches (§8.1 vs §8.2).** The anchor moves only the object directly
   after `)`. Every later object continues from the canonical surface. If the anchor should
   instead cover the whole cohered subset after `)`, only `Evaluate` changes.
3. **Proposition 9 vs the anchor.** `(0Pc) 2d` and `2d` have the same canonical sum (2,0)
   and final object `d`, so §9 calls them the same chord. The anchored one sounds D' d
   (F♯ A) and the plain one sounds B♭' d (D F). `SameChord` reports grammatical identity;
   the frame's `Surface` and `Pitches` report what actually sounds.

## Where the grammar appears in the app

- **Harmony explorer → Sequence**: type grammar words, chord names or note clusters.
  Each step's trace shows the explicit token, anchors, remainders and the canonical surface.
- **Song analysis (PatternPrep)**: every analysed chord is placed on its home surface.
  Each section's progression is saved as a grammar word that starts from the key's
  surface, for example I–V–vi–IV = `0M > 3PM > 2m > 0M` in every key. The pattern
  wheel prints these tokens beside the Roman numerals.
