"""Writes learned/README.md for the GitHub copy from the curriculum results and the recipe cards. Reviewer, 2026-11-17.
Usage: python learned_readme.py <curriculum json> <training data dir> <out file>"""
import json, os, statistics, sys

cur, data, out = sys.argv[1:4]
res = json.load(open(cur, encoding="utf-8"))["tasks"]
cards = {}
rdir = os.path.join(data, "recipes")
for f in os.listdir(rdir) if os.path.isdir(rdir) else []:
    if f.endswith(".recipe.json"):
        c = json.load(open(os.path.join(rdir, f), encoding="utf-8"))
        cards[c.get("workflow")] = c

learned = [t for t in res if t.get("result") == "learned"]
inst = [t["instant"]["ms"] for t in learned if t.get("instant") and t["instant"].get("routed")]
first = [t["first_run_ms"] for t in learned if t.get("first_run_ms")]
aliases = sum(sum(1 for a in t.get("aliases", []) if a.get("learned")) for t in learned)
lines = ["# Learned tasks (starter recipes)", "",
         "Taught on the reference PC on 2026-11-17 (Windows 11, English (Canada), OmniWin 2.6) by a scripted teacher driving OmniWin's own tools",
         "(`bench/teach/teach.py`, task list `bench/teach/make_tasks.py`). Each task was done once, saved with `task_done`, replayed three",
         "times with other values, then run from new words; every other wording was then run once so OmniWin learned it.", "",
         f"- Tasks learned: **{len(learned)}** of {len(res)} tried; wordings learned in addition to each task's first one: **{aliases}**.",
         f"- First run (scripted, so no AI thinking time is included): median {statistics.median(first):.0f} ms." if first else "",
         f"- Instant run from words (`omniwin_do`, one call): median {statistics.median(inst):.0f} ms, {len(inst)} tasks." if inst else "",
         "- Web tasks (Google, Flights, Hotels, Maps, YouTube, Wikipedia) never run by themselves (owner rule: no browser step at speed 1);",
         "  the AI runs them in one call with `workflow_run`. They search only: nothing is booked, bought, signed in to or sent.",
         "- Left out on purpose: Bluetooth on/off, brightness, camera, notifications (system settings, privacy, or different every time),",
         "  terminal commands (a learned paste into a terminal would skip OmniWin's typed-delete check), Word and Excel (not installed).", "",
         "| Task | Group | Result | Wordings learned | Instant run | Recipe |", "|---|---|---|---|---|---|"]
for t in res:
    a = sum(1 for x in t.get("aliases", []) if x.get("learned"))
    i = t.get("instant")
    inst_txt = f"{i['ms']:.0f} ms" if i and i.get("routed") else ("AI only" if t.get("result") == "learned" else "")
    lines.append(f"| {t['id']} | {t['group']} | {t.get('result')} | {1 + a if t.get('result') == 'learned' else 0} of {t.get('wordings', '?')} | {inst_txt} | `{t.get('recipe', '')}` |")
lines += ["", "Failures are listed as they happened (`artifacts/benchmarks/curriculum-*.json` has each step's status).", ""]
open(out, "w", encoding="utf-8").write("\n".join(x for x in lines if x is not None))
print(len(learned), "learned;", aliases, "aliases")
