"""Writes tasks.json, the everyday-task curriculum (handoff 28). Reviewer, 2026-11-17.
Only searches and navigation on real websites (OmniWin's own browser profile, never the owner's Chrome); no booking, sign-in or personal data.
No settings are changed: Settings tasks only open pages. Bluetooth, brightness, camera, notifications and terminal commands are left out
(see the plan). Wordings never start with open/launch/switch to/go to, which the quick-command table already handles."""
import json

VERBS = ["show {x}", "show me {x}", "take me to {x}", "bring up {x}", "pull up {x}", "i want to see {x}", "display {x}",
         "navigate to {x}", "get me {x}", "head to {x}", "jump to {x}", "go into {x}", "find {x} for me", "let me see {x}",
         "can i see {x}", "show {x} now", "i need {x}", "bring me {x}", "load {x}", "view {x}", "put up {x}", "pop up {x}",
         "get {x} up", "i would like to see {x}", "take a look at {x}", "check {x}"]

def literal(id_, group, phrase, steps, check, n=26):
    return {"id": id_, "group": group, "wordings": [v.format(x=phrase) for v in VERBS[:n]], "samples": [{} for _ in range(6)], "steps": steps, "check": check}

tasks = []

# Calculator (paste the sum: the display cannot be typed into)
tasks.append({"id": "calc-sum", "group": "Calculator",
    "wordings": ["calculate {expr} in calculator", "work out {expr} on the calculator", "use the calculator for {expr}",
                 "add up {expr} in calculator", "calculator work out {expr}", "find {expr} with the calculator",
                 "do {expr} on the calculator", "compute {expr} in calculator"],
    "samples": [{"expr": "12+7", "result": "19"}, {"expr": "100-58", "result": "42"}, {"expr": "250+125", "result": "375"},
                {"expr": "64+36", "result": "100"}, {"expr": "1000-1", "result": "999"}, {"expr": "9+9", "result": "18"},
                {"expr": "40+2", "result": "42"}, {"expr": "75-25", "result": "50"}],
    "steps": [["launch", "Calculator"], ["clip", "{expr}"], ["keys", "Calculator", "ctrl+v"], ["keys", "Calculator", "enter"]],
    "check": ["Calculator", "Display is {result}"]})

# Notepad
tasks.append({"id": "notepad-note", "group": "Notepad",
    "wordings": ["write a note saying {text}", "make a note that says {text}", "jot down {text} in notepad",
                 "start a new note with {text}", "type {text} in a new note", "new notepad note saying {text}",
                 "write {text} in notepad", "put {text} in a new notepad note"],
    "samples": [{"text": "call the dentist"}, {"text": "meeting at 3 pm"}, {"text": "water the plants"}, {"text": "check the mail"},
                {"text": "walk the dog"}, {"text": "pick up groceries"}, {"text": "clean the kitchen"}, {"text": "read chapter five"}],
    "steps": [["launch", "Notepad"], ["act", "Notepad", "document", "Text editor", "type", "{text}"]],
    "check": ["Notepad", "{text}"]})

# Settings: search, and the main pages
tasks.append({"id": "settings-search", "group": "Settings",
    "wordings": ["search settings for {query}", "look up {query} in settings", "find the {query} setting",
                 "search windows settings for {query}", "settings search {query}", "find {query} in settings"],
    "samples": [{"query": "display"}, {"query": "sound"}, {"query": "mouse"}, {"query": "keyboard"}, {"query": "printers"},
                {"query": "power"}],
    "steps": [["launch", "Settings"], ["act", "Settings", "edit", "Search box", "type", "{query}"]],
    "check": ["Settings", "{query}"]})
for cat, phrase in [("System", "system settings"), ("Bluetooth & devices", "bluetooth settings"), ("Network & internet", "network settings"),
                    ("Personalization", "personalization settings"), ("Apps", "app settings"), ("Accounts", "account settings"),
                    ("Time & language", "time and language settings"), ("Gaming", "gaming settings"),
                    ("Accessibility", "accessibility settings"), ("Privacy & security", "privacy settings"),
                    ("Windows Update", "windows update settings")]:
    key = "settings-" + cat.lower().replace(" & ", "-").replace(" ", "-")
    tasks.append(literal(key, "Settings", phrase,
                         [["launch", "Settings"], ["act", "Settings", "listitem", cat, "select"]],
                         ["Settings", f'button "{cat}"']))

# File Explorer folders
for folder in ["Documents", "Downloads", "Pictures", "Music", "Videos", "Desktop"]:
    tasks.append(literal("explorer-" + folder.lower(), "File Explorer", f"my {folder.lower()} folder",
                         [["launch", "File Explorer"], ["act", "File Explorer", "treeitem", folder if folder != "Desktop" else "Start of Quick Access - Desktop", "click"]],
                         [folder + " - File Explorer" if folder != "Desktop" else "Desktop - File Explorer", "window"]))

# Clock pages
for page, phrase in [("Stopwatch", "the stopwatch"), ("Timer", "the timer"), ("Alarm", "my alarms"), ("World clock", "the world clock"),
                     ("Focus sessions", "focus sessions")]:
    tasks.append(literal("clock-" + page.lower().replace(" ", "-"), "Clock", phrase,
                         [["launch", "Clock"], ["act", "Clock", "listitem", page, "select"]], ["Clock", page]))

# Calculator modes and converters
for item in ["Standard Calculator", "Scientific Calculator", "Graphing Calculator", "Programmer Calculator", "Date calculation Calculator",
             "Currency Converter", "Volume Converter", "Length Converter", "Weight and mass Converter", "Temperature Converter",
             "Energy Converter", "Area Converter", "Speed Converter", "Time Converter", "Power Converter", "Data Converter"]:
    phrase = "the " + item.lower().replace("date calculation calculator", "date calculator").replace("weight and mass converter", "weight converter")
    tasks.append(literal("calc-" + item.split()[0].lower(), "Calculator", phrase,
                         [["launch", "Calculator"], ["act", "Calculator", "button", "Open Navigation", "click"],
                          ["act", "Calculator", "listitem", item, "select"]], ["Calculator", item.split()[0]]))


# More pages (added 2026-11-18): Media Player, Weather, File Explorer places, Settings home. Each holds 11 wordings at most
# (OmniWin keeps 10 extra wordings per recipe), so reaching about 500 wordings needs more tasks, not more wordings per task.
for page, phrase in [("Home", "media player home"), ("Music library", "my music library"), ("Video library", "my video library"),
                     ("Play queue", "the play queue"), ("Playlists", "my playlists")]:
    tasks.append(literal("media-" + page.lower().replace(" ", "-"), "Media Player", phrase,
                         [["launch", "Media Player"], ["act", "Media Player", "listitem", page, "select"]], ["Media Player", page], n=14))
for page, phrase in [("Forecast", "the weather forecast"), ("Hourly Forecast", "the hourly forecast"), ("Monthly Forecast", "the monthly forecast"),
                     ("Maps", "the weather maps"), ("Historical Weather", "the historical weather"), ("Pollen", "the pollen forecast"),
                     ("Life", "the weather life tips"), ("Favorites", "my favorite weather places")]:
    tasks.append(literal("weather-" + page.lower().replace(" ", "-"), "Weather", phrase,
                         [["launch", "Weather"], ["act", "Weather", "listitem", page, "select"]], ["Weather", page], n=14))
for place, phrase, title in [("Home", "explorer home", "Home"), ("Gallery", "the photo gallery", "Gallery"), ("Network", "network places", "Network"),
                             ("This PC", "this pc", "This PC")]:
    tasks.append(literal("explorer-" + place.lower().replace(" ", "-"), "File Explorer", phrase,
                         [["launch", "File Explorer"], ["act", "File Explorer", "treeitem", place, "click"]], [title + " - File Explorer", "window"], n=14))
tasks.append(literal("settings-home", "Settings", "the settings home page",
                     [["launch", "Settings"], ["act", "Settings", "listitem", "Home", "select"]], ["Settings", 'button "Home"'], n=14))

tasks.append(literal("clock-settings", "Clock", "the clock settings",
                     [["launch", "Clock"], ["act", "Clock", "listitem", "Settings", "select"]], ["Clock", "Settings"], n=14))
tasks.append(literal("media-settings", "Media Player", "the media player settings",
                     [["launch", "Media Player"], ["act", "Media Player", "listitem", "Settings", "select"]], ["Media Player", "Settings"], n=14))
for place, phrase in [("Recent", "my recent files"), ("Favorites", "my favorite files"), ("Shared", "files shared with me")]:
    tasks.append(literal("explorer-" + place.lower(), "File Explorer", phrase,
                         [["launch", "File Explorer"], ["act", "File Explorer", "tabitem", place, "select"]], ["File Explorer", place], n=14))

# Still more pages (2026-11-18), view only: Microsoft Store (never installs anything; its Downloads page is skipped), Photos, Paint colours
for page, phrase in [("Home", "the store home page"), ("Apps", "apps in the store"), ("Gaming", "games in the store"), ("Library", "my store library"),
                     ("Themes", "themes in the store"), ("What's New", "what is new in the store")]:
    tasks.append(literal("store-" + page.lower().replace("'", "").replace(" ", "-"), "Microsoft Store", phrase,
                         [["launch", "Microsoft Store"], ["act", "Microsoft Store", "listitem", page, "click"]], ["Microsoft Store", page], n=14))
for page, phrase in [("Gallery", "my photo gallery"), ("Favorites", "my favorite photos"), ("This PC", "photos on this pc")]:
    tasks.append(literal("photos-" + page.lower().replace(" ", "-"), "Photos", phrase,
                         [["launch", "Photos"], ["act", "Photos", "listitem", page, "select"]], ["Photos", page], n=14))
for colour in ["Red", "Green", "Black", "Yellow"]:
    tasks.append(literal("paint-" + colour.lower(), "Paint", f"paint with {colour.lower()}",
                         [["launch", "Paint"], ["act", "Paint", "listitem", colour, "click"]], ["Paint", colour], n=14))

# Web (OmniWin's own browser): searches only. set_value replaces what a box already holds (Google pre-fills some).
def web(id_, wordings, samples, url, box_role, box_name, submit, check="{q}"):
    steps = [["nav", url], ["bact", box_role, box_name, "set_value", "{q}"]] + submit
    return {"id": id_, "group": "Web", "wordings": wordings, "samples": samples, "steps": steps, "check": ["@browser", check]}

ENTER = [["bkey", "enter"]]
Q = [{"q": "weather toronto"}, {"q": "best pizza near me"}, {"q": "how tall is mount everest"}, {"q": "news today"},
     {"q": "dollar to euro"}, {"q": "time in tokyo"}, {"q": "easy pasta recipe"}, {"q": "nba scores"}]
CITIES = [{"q": "vancouver"}, {"q": "new york"}, {"q": "paris"}, {"q": "london"}, {"q": "miami"}, {"q": "montreal"}]
# General Google web search is not taught: Google showed its "unusual traffic" check to OmniWin's browser (2026-11-17),
# and OmniWin never solves or works around a bot check. The AI can still search live through OmniWin's browser.
def typed(id_, wordings, samples, url, box_role, box_name, after, pre=()):
    host = url.split("//", 1)[1].split("/", 1)[0]
    return {"id": id_, "group": "Web", "wordings": wordings, "samples": samples,
            "steps": [["nav", url, host], *pre, ["bact", box_role, box_name, "type", "{q}"]] + after, "check": ["@browser", "{q}"]}

# Google Flights and Hotels are not taught: Enter does not open their results; a suggestion must be picked from a list that
# changes with every search, so a replay could not be checked (tried 2026-11-17). The AI can still do them live.
tasks.append(typed("google-maps", ["find {q} on google maps", "show {q} on the map", "search google maps for {q}",
                                   "where is {q} on the map", "look up {q} on maps", "google maps search {q}"],
                   [{"q": "cn tower"}, {"q": "eiffel tower"}, {"q": "niagara falls"}, {"q": "central park"}, {"q": "union station toronto"},
                    {"q": "stanley park"}], "https://www.google.com/maps", "combobox", "Search Google Maps", [["bact", "button", "Search", "click"]]))
tasks.append(typed("youtube-search", ["search youtube for {q}", "find {q} on youtube", "look up {q} on youtube", "youtube search {q}",
                                      "show me {q} videos on youtube", "search youtube videos for {q}"],
                   [{"q": "lofi music"}, {"q": "cooking pasta"}, {"q": "guitar lesson"}, {"q": "cat videos"}, {"q": "space documentary"},
                    {"q": "morning workout"}], "https://www.youtube.com", "combobox", "Search", ENTER))
tasks.append(typed("wikipedia", ["look up {q} on wikipedia", "search wikipedia for {q}", "wikipedia article on {q}", "find {q} on wikipedia",
                                 "read about {q} on wikipedia", "wikipedia search {q}"],
                   [{"q": "canada"}, {"q": "solar system"}, {"q": "albert einstein"}, {"q": "photosynthesis"}, {"q": "toronto"},
                    {"q": "the moon"}], "https://en.wikipedia.org", "textbox", "Search Wikipedia", [["bact", "button", "Search", "click"]]))

json.dump({"tasks": tasks}, open("tasks.json", "w", encoding="utf-8"), indent=1)
print(len(tasks), "tasks,", sum(len(t["wordings"]) for t in tasks), "wordings")
