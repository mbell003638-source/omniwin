"""
Builds eval_hard.jsonl: natural paraphrases that the strict phrase table is NOT expected to match (that is where the Laya stage could
help), plus near-miss sentences that look like commands but are not ("pause for dramatic effect", "close the deal"). Hand-written, so it
is not shaped by the rules in PhraseRouter.cs. Same format as eval_set.jsonl. Text only; nothing touches the desktop.
"""
import json
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "eval_hard.jsonl")

HARD = {
    "media_play": [
        "keep the music going", "start the song again", "turn the music back on", "let it play", "carry on with the track", "hit play",
        "start playing again", "get the video going", "continue the song", "unpause it",
    ],
    "media_pause": [
        "hold the music", "freeze the video", "stop the song for a second", "put the music on hold", "can you pause that for me",
        "wait pause it", "halt the playback", "pause that", "pause it", "stop the video for a moment",
    ],
    "media_next": [
        "play the next one", "go to the next song", "move on to the next track", "skip ahead to the next song", "i dont like this song skip it",
        "next one please", "forward to the next video", "play the following track",
    ],
    "media_previous": [
        "play the previous song", "go to the last track", "take me back one song", "the song before this one", "back to the previous track",
        "play the song from before",
    ],
    "media_toggle": ["press play pause", "flip between play and pause"],
    "volume_set": [
        "make the volume 40", "put the sound at 25 percent", "i want volume at 60", "set it to 35 percent volume", "volume should be 80 percent",
        "bring the sound to 20", "make the sound 55", "can you set the volume at 45",
    ],
    "volume_up": [
        "i cant hear anything turn it up", "make it a bit louder", "can you raise the volume a little", "crank it up", "increase the volume",
        "more volume please", "up the volume", "a little louder", "make the sound bigger",
    ],
    "volume_down": [
        "its too loud", "make it quieter please", "bring the volume down a bit", "decrease the volume", "lower the sound", "turn down the music",
        "less volume", "a little quieter", "that is way too loud",
    ],
    "mute": ["silence the computer", "shut off the sound", "make it silent", "kill the audio", "turn the sound off", "no sound please"],
    "unmute": ["bring the sound back", "turn the audio back on", "i want sound again", "enable the sound", "give me sound again"],
    "window_focus": [
        "show me chrome", "i want to see notepad", "take me to spotify", "bring chrome to the front", "go over to the terminal window",
        "pull up calculator", "i need the spotify window", "let me see the notepad window",
    ],
    "window_minimize": ["hide this window", "get this window out of the way", "shrink notepad to the taskbar", "minimize the chrome window please", "put this window away"],
    "window_maximize": ["make this window fullscreen", "make notepad bigger", "expand this window to fill the screen", "make chrome full screen"],
    "window_restore": ["put the window back to normal size", "restore the notepad window", "bring the window back down to its old size"],
    "window_close": ["shut the notepad window", "close that calculator", "shut this window", "close the spotify window"],
    "app_launch": [
        "can you open up notepad for me", "i need calculator", "fire up spotify", "bring up paint", "load excel", "launch the calculator app",
        "get me notepad", "i want to use word",
    ],
    "workflow_run": ["do my morning setup routine", "execute the save draft workflow", "kick off the daily notes workflow", "run my morning setup workflow"],
    "read_time": ["do you know what time it is", "tell me the time", "what is the current time", "how late is it", "what time is it right now"],
    "read_battery": ["how much power is left", "is my battery low", "whats my battery at", "check the battery", "how much charge do i have"],
    "read_volume": ["how loud is it right now", "what is the sound level at", "tell me the current volume", "what is the volume set to"],
    "none": [
        "play the next episode of the show i was watching", "turn up the heat", "the volume of the sphere", "pause for dramatic effect", "close the deal",
        "switch to a healthier diet", "open a bank account", "start a new project in the repo", "go to sleep", "mute the other participants in the call",
        "skip the intro of this video", "what time does the store close", "battery recycling near me", "how do i fix the volume knob",
        "play the song that was on yesterday", "minimize the risks of this plan", "maximize my productivity", "restore my deleted files",
        "launch a rocket into space", "run a marathon", "focus on the task", "switch the lights on", "next weeks weather", "previous owner of this house",
        "open the pod bay doors", "volume of a cylinder formula", "what is a good volume for a library", "how do i unmute someone on zoom",
        "remind me when the battery is low", "the time machine is broken", "set the volume of the sphere to 40 litres", "pause the download",
        "play the guitar", "stop the car", "next to the window", "back to the future", "go to the store and buy some milk", "open sesame",
        "start from the beginning and explain everything", "close the door", "launch the product next month", "restore the old design",
        "turn the page", "turn it around", "skip lunch", "mute my microphone in the meeting", "raise a ticket for this", "lower the price",
        "quiet please i am working", "i am cold", "please", "okay thanks", "do the thing again", "what was that", "undo what you just did",
    ],
}

items = []
for intent, texts in HARD.items():
    for text in texts:
        items.append({"text": text, "intent": intent, "set": "hard"})

with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    for item in items:
        f.write(json.dumps(item, ensure_ascii=False) + "\n")
print(len(items), "items written to", OUT)
