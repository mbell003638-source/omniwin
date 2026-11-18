"""
Builds eval_set.jsonl: labelled spoken/typed commands for the OmniWin router.

Each line: {"text": ..., "intent": <one of the router intents or "none">, "expect": {...optional slot values...}}
The set is used two ways:
  1. tests/OmniWin.Router.Tests runs it through the strict phrase table. Rule: whatever the table matches must be the labelled
     intent (a wrong match is a failure); "none" items must never match. Coverage (how many it matches) is only reported.
  2. eval_laya.py runs it through the Laya model offline (no input is sent) and reports accuracy, the wrong-action rate at the
     chosen confidence threshold, and latency.
Deterministic: the same file every time. Plain text only; nothing here touches the desktop.
"""
import json
import itertools
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "eval_set.jsonl")

PREFIXES = ["", "", "", "please ", "hey ", "ok ", "could you ", "can you please ", "just "]
SUFFIXES = ["", "", "", " please", " now", " for me"]

APPS = ["notepad", "calculator", "paint", "edge", "chrome", "spotify", "word", "excel", "settings", "file explorer", "terminal", "vlc"]
WINDOWS = ["chrome", "edge", "notepad", "spotify", "explorer", "code", "word", "terminal", "calculator"]
WORKFLOWS = ["morning setup", "save draft", "open report", "daily notes", "backup note"]

items = []


def add(text, intent, **expect):
    items.append({"text": text, "intent": intent, "expect": expect} if expect else {"text": text, "intent": intent})


def variants(core):
    for p, s in itertools.product(PREFIXES, SUFFIXES):
        yield (p + core + s).strip()


def pick(core, intent, limit=6, **expect):
    seen = []
    for v in variants(core):
        if v not in seen:
            seen.append(v)
    # deterministic spread over the variants
    step = max(1, len(seen) // limit)
    for v in seen[::step][:limit]:
        add(v, intent, **expect)


# ---- media
for c in ["play", "resume", "unpause", "play the music", "resume the song", "continue playing"]:
    pick(c, "media_play", limit=3)
for c in ["pause", "pause the music", "stop the music", "pause the video", "hold on pause that", "stop playback"]:
    pick(c, "media_pause", limit=3)
for c in ["play pause", "toggle playback", "toggle media"]:
    pick(c, "media_toggle", limit=3)
for c in ["next", "next track", "skip", "skip this song", "next song", "skip the track"]:
    pick(c, "media_next", limit=3)
for c in ["previous", "previous track", "go back a song", "last song", "previous song"]:
    pick(c, "media_previous", limit=3)

# ---- volume and mute
for n in [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 60, 70, 75, 80, 90, 100]:
    for c in [f"volume {n}", f"set the volume to {n}", f"volume to {n} percent", f"turn the volume to {n}", f"set volume at {n}%"]:
        pick(c, "volume_set", limit=1, percent=n)
for c in ["volume up", "turn the volume up", "louder", "turn it up", "raise the volume", "volume up by 20", "turn the volume up by 15"]:
    pick(c, "volume_up", limit=3)
for c in ["volume down", "turn the volume down", "quieter", "turn it down", "lower the volume", "volume down by 20", "softer"]:
    pick(c, "volume_down", limit=3)
for c in ["mute", "mute the sound", "mute the volume", "mute the computer", "mute audio"]:
    pick(c, "mute", limit=3)
for c in ["unmute", "unmute the sound", "turn the sound on", "unmute the volume"]:
    pick(c, "unmute", limit=3)

# ---- windows and apps
for w in WINDOWS:
    for c in [f"switch to {w}", f"go to {w}", f"focus {w}", f"bring up {w}"]:
        pick(c, "window_focus", limit=2, target=w)
for w in WINDOWS[:6]:
    for c in [f"minimize {w}", f"minimise the {w} window"]:
        pick(c, "window_minimize", limit=1, target=w)
    pick(f"maximize {w}", "window_maximize", limit=1, target=w)
    pick(f"restore {w}", "window_restore", limit=1, target=w)
    pick(f"close {w}", "window_close", limit=1, target=w)
for c in ["minimize", "minimize this window", "minimize the current window"]:
    pick(c, "window_minimize", limit=2)
for c in ["maximize", "maximize this window"]:
    pick(c, "window_maximize", limit=2)
for a in APPS:
    for c in [f"open {a}", f"launch {a}", f"start {a}"]:
        pick(c, "app_launch", limit=2, app=a)
for w in WORKFLOWS:
    for c in [f"run workflow {w}", f"run the workflow {w}", f"do workflow {w}"]:
        pick(c, "workflow_run", limit=2, name=w)

# ---- reads
for c in ["what time is it", "whats the time", "time", "what is the time"]:
    pick(c, "read_time", limit=3)
for c in ["battery", "battery level", "how much battery do i have", "battery status"]:
    pick(c, "read_battery", limit=3)
for c in ["what's the volume", "volume level", "how loud is it", "volume"]:
    pick(c, "read_volume", limit=3)

# ---- none: open-ended, risky, or not a quick command. The router must never run any of these.
NONE = [
    "find me a good thriller on netflix and play it", "what's the weather in tokyo", "summarize this page", "book a flight to seattle next friday",
    "write an email to bob about the meeting", "delete my downloads folder", "delete everything on the desktop", "install chrome", "restart the pc",
    "shut down the computer", "send this to anna", "what is on my screen", "explain this code", "play despacito", "play some jazz",
    "open the second link", "click the blue button", "click save", "scroll down", "scroll to the bottom", "type hello world", "turn off the lights",
    "set a timer for five minutes", "remind me to call mom", "what's the capital of france", "tell me a joke", "search for cheap flights",
    "open youtube and search for cats", "take a screenshot and email it", "log me out", "lock the computer", "empty the recycle bin",
    "uninstall spotify", "remove the old files", "format the usb drive", "pay the electricity bill", "buy milk online", "download the report",
    "copy this to the clipboard", "paste it here", "open the file named budget dot xlsx", "go to the downloads folder and sort by date",
    "pause the music and then lower the volume and open notepad", "volume up and next track", "switch to chrome and open a new tab",
    "make it a bit louder than before but not too loud", "mute everything except the call", "close all windows", "close everything",
    "what's playing", "who sings this", "turn on dark mode", "change the wallpaper", "connect to wifi", "enable bluetooth", "increase the brightness",
    "what did i do yesterday", "read my notifications", "open settings and change the display scale", "run the script", "run powershell",
    "start a backup", "run workflow", "open", "switch to", "volume to loud", "set the volume to one hundred and fifty", "volume 250",
    "play it again", "go back", "forward", "refresh the page", "new tab", "save the file", "undo that", "undo", "redo",
    "hello", "thanks", "how are you", "nevermind", "stop it", "wait", "yes", "no", "do it", "open the thing", "close the thing i mean the other thing",
    "switch to the window with the red icon", "focus on the second monitor", "bring everything to the front", "minimize all", "show the desktop",
]
for t in NONE:
    add(t, "none")
# every none item also with a polite prefix, to make sure politeness alone never turns a request into a command
for t in NONE[:40]:
    add("please " + t, "none")

with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    for item in items:
        f.write(json.dumps(item, ensure_ascii=False) + "\n")
print(len(items), "items written to", OUT)
counts = {}
for i in items:
    counts[i["intent"]] = counts.get(i["intent"], 0) + 1
print(counts)
