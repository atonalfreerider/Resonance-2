"""Copy fully prepared songs from PreparedSongs/Library to a Meta Quest over adb.

The headset app (see Docs/QUEST.md) reads its library from its own external files folder,
/sdcard/Android/data/<package>/files/Library/<song folder>/, with the same layout as the
desktop library but only what playback needs: aligned.mid and its patterns, prepared and
stripes sidecars, the recording the prepared manifest names, and story.json for the title.
Stems, narration and analysis files stay on the computer.

  python Tools/SongLibrary/deploy_quest.py                 # every prepared song
  python Tools/SongLibrary/deploy_quest.py --only fire     # folders containing "fire"
  python Tools/SongLibrary/deploy_quest.py --list          # what is on the headset
  python Tools/SongLibrary/deploy_quest.py --remove Drank  # delete a song from the headset
  python Tools/SongLibrary/deploy_quest.py --command "passthrough on" --command dump
  python Tools/SongLibrary/deploy_quest.py --command sweep  # measure what the picture costs
  python Tools/SongLibrary/deploy_quest.py --perf           # print the last sweep's result

Commands run in the headset app (VrCommands) while it is running and worn; see Docs/QUEST.md.

Files already on the headset with the same size are skipped, so re-running is cheap.
Install and run the app once first so the headset has made its data folder.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT / "PreparedSongs" / "Library"
PACKAGE = "com.primitive.resonance"
BUNDLE_VERSION = 5  # PreparedPatternSong.CurrentVersion
ADB_CANDIDATES = [
    r"C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe",
]


def find_adb():
    found = shutil.which("adb")
    if found:
        return found
    for candidate in ADB_CANDIDATES:
        if Path(candidate).exists():
            return candidate
    hub = Path(r"C:\Program Files\Unity\Hub\Editor")
    for adb in sorted(hub.glob("*/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"), reverse=True):
        return str(adb)
    sys.exit("adb not found: install Android platform-tools or Unity's Android support.")


class Device:
    def __init__(self, adb, serial):
        self.adb, self.serial = adb, serial

    def run(self, *args, check=True):
        command = [self.adb] + (["-s", self.serial] if self.serial else []) + list(args)
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if check and result.returncode != 0:
            raise RuntimeError(f"{' '.join(args[:2])}: {result.stderr.strip() or result.stdout.strip()}")
        return result.stdout

    def shell(self, command, check=True):
        return self.run("shell", command, check=check)

    def sizes(self, folder):
        """File name -> size for the files in a headset folder (empty if missing)."""
        out = self.shell(f"ls -l '{folder}' 2>/dev/null", check=False)
        sizes = {}
        for line in out.splitlines():
            parts = line.split()
            if len(parts) >= 8 and parts[0].startswith("-"):
                sizes[" ".join(parts[7:])] = int(parts[4])
        return sizes


def bundle_current(bundle):
    try:
        with open(bundle, "r", encoding="utf-8", errors="replace") as handle:
            head = handle.read(65536)
        match = re.search(r'"Version"\s*:\s*(\d+)', head)
        return bool(match) and int(match.group(1)) >= BUNDLE_VERSION
    except OSError:
        return False


def prepared_songs(only=None, include_fixtures=False):
    """The same rule as the app's song list: aligned.mid with current patterns and a prepared manifest."""
    songs = []
    for folder in sorted(LIBRARY.iterdir(), key=lambda p: p.name.lower()):
        if not folder.is_dir():
            continue
        if not include_fixtures and folder.name.startswith("lyric-fixture"):
            continue
        if only and not any(o.lower() in folder.name.lower() for o in only):
            continue
        score = folder / "aligned.mid"
        bundle = Path(str(score) + ".patterns.json")
        prepared = Path(str(score) + ".prepared.json")
        if not (score.exists() and bundle.exists() and prepared.exists() and bundle_current(bundle)):
            continue
        songs.append(folder)
    return songs


def payload(folder):
    """Files the headset needs for one song, as (local path, name on the headset)."""
    score = folder / "aligned.mid"
    files = [score] + [Path(str(score) + suffix) for suffix in (".patterns.json", ".prepared.json", ".stripes.json")]
    manifest = json.loads(Path(str(score) + ".prepared.json").read_text(encoding="utf-8"))
    audio = (folder / manifest["audioPath"]).resolve()
    if audio.parent != folder.resolve():
        raise RuntimeError(f"{folder.name}: recording {manifest['audioPath']} is outside the song folder")
    files.append(audio)
    files.append(folder / "story.json")
    return [(f, f.name) for f in files if f.exists()]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--serial", help="adb serial of the headset (default: the only device)")
    parser.add_argument("--package", default=PACKAGE)
    parser.add_argument("--only", nargs="+", help="deploy only song folders containing any of these")
    parser.add_argument("--fixtures", action="store_true", help="include the lyric test fixtures")
    parser.add_argument("--list", action="store_true", help="list the songs on the headset and stop")
    parser.add_argument("--remove", nargs="+", help="remove song folders containing any of these from the headset")
    parser.add_argument("--command", action="append", help="send a command line to the running app (repeatable)")
    parser.add_argument("--perf", action="store_true", help="print the last performance sweep from the headset")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    device = Device(find_adb(), args.serial)
    devices = [l.split()[0] for l in device.run("devices").splitlines()[1:] if l.strip().endswith("device")]
    if not devices:
        sys.exit("No headset on adb: plug in the Quest and allow USB debugging in the headset.")
    if not args.serial and len(devices) > 1:
        sys.exit(f"Several devices ({', '.join(devices)}): pick one with --serial.")
    remote_root = f"/sdcard/Android/data/{args.package}/files/Library"
    remote_files = f"/sdcard/Android/data/{args.package}/files"

    if args.command or args.perf:
        if args.command:
            import tempfile
            with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8", newline="\n") as handle:
                handle.write("\n".join(args.command) + "\n")
            # Pushed as the shell user: opened so the app can read and delete it, then renamed
            # into place so the app never reads it half written.
            staging = f"{remote_files}/vr-commands.txt.tmp"
            device.run("push", handle.name, staging)
            os.unlink(handle.name)
            device.shell(f"chmod 666 {staging} && mv {staging} {remote_files}/vr-commands.txt")
            print(f"Sent {len(args.command)} command(s); the app runs them within half a second while it is running.")
        if args.perf:
            print(device.shell(f"cat {remote_files}/vr-perf.txt 2>/dev/null", check=False) or "No sweep result on the headset yet.")
        return

    if args.list or args.remove:
        folders = [l.strip() for l in device.shell(f"ls '{remote_root}' 2>/dev/null", check=False).splitlines() if l.strip()]
        if args.remove:
            for name in folders:
                if any(r.lower() in name.lower() for r in args.remove):
                    print(f"removing {name}")
                    if not args.dry_run:
                        device.shell(f"rm -rf '{remote_root}/{name}'")
            return
        for name in folders:
            total = sum(device.sizes(f"{remote_root}/{name}").values())
            print(f"{total / 1048576:8.1f} MB  {name}")
        return

    songs = prepared_songs(args.only, args.fixtures)
    if not songs:
        sys.exit("No fully prepared songs matched.")
    pushed = skipped = 0
    for folder in songs:
        remote = f"{remote_root}/{folder.name}"
        files = payload(folder)
        have = device.sizes(remote)
        todo = [(f, n) for f, n in files if have.get(n) != f.stat().st_size]
        size = sum(f.stat().st_size for f, _ in todo)
        print(f"{folder.name}: {len(todo)} of {len(files)} files, {size / 1048576:.1f} MB")
        if args.dry_run or not todo:
            skipped += len(files) - len(todo)
            continue
        device.shell(f"mkdir -p '{remote}'")
        for local, name in todo:
            device.run("push", str(local), f"{remote}/{name}")
            pushed += 1
        skipped += len(files) - len(todo)
    # adb creates folders and files as the shell user, closed to others: open them so the app
    # (its own user) can read them.
    if not args.dry_run:
        device.shell(f"chmod -R a+rwX '{remote_root}'")
    print(f"Done: {pushed} files pushed, {skipped} already there, {len(songs)} songs in {remote_root}.")


if __name__ == "__main__":
    main()
