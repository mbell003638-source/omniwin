"""Teaches OmniWin a list of everyday tasks (handoff 28). Reviewer, 2026-11-17.

For each task in tasks.json, in one OmniWin.Server process:
  1. omniwin_do(first wording)            -> needs_brain opens an episode
  2. the task's steps through OmniWin's own tools (every step through the normal checks and the safety gate)
  3. the end-result check                  -> task_done(success=true, template, params) saves a recipe
  4. three replays with other values (workflow_run) -> trusted
  5. one instant run from words (omniwin_do) -> must answer "routed rule=recipe."
  6. every other wording once (omniwin_do -> needs_brain -> workflow_run -> task_done) -> learned as a wording
Results: artifacts/benchmarks/curriculum-<tag>.json. Usage: python teach.py <server.exe> <data dir> <tasks.json> <tag> [task ids...]
Only real input through OmniWin; nothing is deleted; windows the task opened are closed (unsaved text is never saved).
"""
import json, re, sys, time
from omni import Omni, status

ROLE_LINE = re.compile(r'^\s*(e\d+|b\d+) (\S+) "((?:[^"\\]|\\.)*)"(.*)$')


def fill(text, values):
    return re.sub(r"\{(\w+)\}", lambda m: values[m.group(1)], text)


class BotCheck(Exception):
    """A site showed a bot check (CAPTCHA, 'unusual traffic'): never solved or worked around; the task stops."""


class Teacher:
    def __init__(self, o):
        self.o = o
        self.log = []

    def call(self, name, args=None):
        t = time.perf_counter()
        r = self.o.call(name, args or {})
        self.log.append({"tool": name, "status": status(r), "ms": round((time.perf_counter() - t) * 1000, 1)})
        return r

    # ---- looking
    def observe(self, window, browser=False):
        if browser:
            r = self.call("browser_observe", {"max_elements": 400})
        else:
            r = self.call("desktop_observe", {"window": window, "max_elements": 400})
        gen = re.search(r"gen=(\d+)", r)
        return r, int(gen.group(1)) if gen else 0

    def find(self, text, role, name, exact=False):
        for line in text.splitlines():
            m = ROLE_LINE.match(line)
            if not m:
                continue
            ref, r, n = m.group(1), m.group(2), m.group(3)
            if role and r != role:
                continue
            if not role and "click" not in line and "select" not in line and "invoke" not in line:
                continue   # no role given: only something that can be clicked or selected
            if (n == name) if exact else n.lower().startswith(name.lower()):
                return ref
        return None

    # ---- steps
    def step(self, s, values):
        kind = s[0]
        if kind == "launch":
            return self.call("app_launch", {"app": s[1]})
        if kind == "keys":
            return self.call("desktop_keys", {"keys": s[2], **({"window": s[1]} if s[1] else {})})
        if kind == "clip":
            return self.call("clipboard_set", {"text": fill(s[1], values)})
        if kind == "wait":
            time.sleep(s[1] / 1000)
            return "status=confirmed waited"
        if kind == "bkey":
            edge = [w for w, v in self.windows().items() if v[0] == "msedge.exe"]
            if not edge:
                return "status=blocked reason=not_found teacher: no browser window"
            return self.call("desktop_keys", {"keys": s[1], "window": edge[0]})
        if kind == "nav":
            # an expectation lets the navigation be confirmed, so it is recorded as a step of the recipe
            args = {"action": "navigate", "url": s[1]}
            if len(s) > 2:
                args["expect"] = {"condition": {"kind": "url_contains", "value": s[2]}, "timeout_ms": 8000}
            return self.call("browser_nav", args)
        if kind in ("act", "bact"):
            browser = kind == "bact"
            window, role, name, action = (None, s[1], s[2], s[3]) if browser else (s[1], s[2], s[3], s[4])
            value = s[4] if browser and len(s) > 4 else (s[5] if not browser and len(s) > 5 else None)
            for attempt in range(6):
                text, gen = self.observe(window, browser)
                ref = self.find(text, role, name)
                if browser and ("/sorry/" in text or "unusual traffic" in text.lower()):
                    raise BotCheck()
                if ref:
                    args = {"ref": ref, "gen": gen, "action": action}
                    if value is not None:
                        args["value"] = fill(value, values)
                    if browser and len(s) > 5 and s[5]:
                        # the page this step must lead to: OmniWin confirms it, records it, and checks it again on replay
                        args["expect"] = {"condition": {"kind": "url_contains", "value": s[5]}, "timeout_ms": 10000}
                    r = self.call("browser_act" if browser else "desktop_act", args)
                    if status(r) == "stale" and attempt < 5:
                        time.sleep(0.5)
                        continue
                    return r
                time.sleep(0.5)
            return f"status=blocked reason=not_found teacher: no {role} named {name!r}"
        raise ValueError(kind)

    def run_steps(self, steps, values):
        for s in steps:
            r = self.step(s, values)
            st = status(r)
            if st not in ("confirmed", "unverified"):
                return False, f"step {s[:4]} -> {r.splitlines()[0][:200]}"
        return True, None

    def check(self, chk, values):
        if not chk:
            return True
        window, needle = chk[0], fill(chk[1], values)
        for _ in range(40 if window == "@browser" else 16):   # web pages load slowly
            # web pages: the tab list (title and address) is enough, and a very large page can time out a full read
            text = (self.o.call("browser_tabs") if window == "@browser" else
                    self.o.call("windows_list") if window == "@windows" else self.observe(window)[0])
            if window == "@browser" and ("/sorry/" in text or "unusual traffic" in text.lower()):
                raise BotCheck()
            if needle.lower() in text.lower():
                return True
            time.sleep(0.5)
        return False

    def windows(self):
        out = {}
        for line in self.o.call("windows_list").splitlines():
            m = re.match(r"^(w\d+) (\S+) pid=\d+ \"(.*?)\"(.*)$", line)
            if m:
                out[m.group(1)] = (m.group(2).lower(), m.group(3), m.group(4))
        return out

    def snapshot(self):
        self.baseline = set(self.windows())

    def cleanup(self, task=None):
        """Closes only windows that appeared since the snapshot (never the owner's). Unsaved text is never saved."""
        for _ in range(3):
            new = {w: v for w, v in self.windows().items() if w not in self.baseline and v[0] != "msedge.exe" and "inaccessible" not in v[2]}
            if not new:
                return
            for wid in new:
                self.o.call("window_manage", {"window": wid, "action": "close"})
                time.sleep(0.5)
            for wid in list(self.windows()):
                if wid in self.baseline:
                    continue
                text = self.o.call("desktop_observe", {"window": wid, "max_elements": 120})
                for line in text.splitlines():
                    m = ROLE_LINE.match(line)
                    if m and m.group(2) == "button" and m.group(3).lower().replace("’", "'") in ("don't save", "dont save"):
                        gen = re.search(r"gen=(\d+)", text)
                        self.o.call("desktop_act", {"ref": m.group(1), "gen": int(gen.group(1)) if gen else 0, "action": "click"})
                        time.sleep(0.4)
                        break

def teach(t, task):
    res = {"id": task["id"], "group": task["group"], "wordings": len(task["wordings"]), "phases": {}}
    samples, wordings = task["samples"], task["wordings"]
    t.snapshot()
    t0 = time.perf_counter()
    first = t.call("omniwin_do", {"text": fill(wordings[0], samples[0])})
    if not first.startswith("needs_brain"):
        res["result"] = "already_instant_or_refused"
        res["detail"] = first.splitlines()[0][:200]
        t.cleanup(task)
        return res
    ok, why = t.run_steps(task["steps"], samples[0])
    ok = ok and t.check(task.get("check"), samples[0])
    if not ok:
        t.call("task_done", {"success": False})
        res["result"] = "teach_failed"
        res["detail"] = why or "end-result check failed"
        t.cleanup(task)
        return res
    slots = re.findall(r"\{(\w+)\}", wordings[0])
    params = {f"p{i + 1}": s for i, s in enumerate(slots)}
    done = t.call("task_done", {"success": True, "template": wordings[0], **({"params": params} if params else {})})
    ph = re.search(r"Placeholders: ([^.]+)\.", done)
    if "template_mismatch" in done and ph and slots:
        # placeholder names count up across tasks in one server process: map the slots to the ones OmniWin lists, in order
        names = [x.strip() for x in ph.group(1).split(",")]
        if len(names) == len(slots):
            done = t.call("task_done", {"success": True, "template": wordings[0], "params": dict(zip(names, slots))})
    res["first_run_ms"] = round((time.perf_counter() - t0) * 1000, 1)
    res["task_done"] = done.splitlines()[0][:220]
    m = re.search(r"recipe=(\S+)", done)
    if status(done) != "confirmed" or "learned=true" not in done or not m:
        res["result"] = "not_learned"
        res["detail"] = done[:300]
        t.cleanup(task)
        return res
    name = res["recipe"] = m.group(1)
    t.cleanup(task)

    replays = []
    for v in samples[1:4]:
        t1 = time.perf_counter()
        r = t.call("workflow_run", {"name": name, "params": {k: v[k] for k in slots}} if slots else {"name": name})
        good = status(r) in ("confirmed", "unverified") and t.check(task.get("check"), v)
        replays.append({"ok": good, "ms": round((time.perf_counter() - t1) * 1000, 1), "line": r.splitlines()[0][:120]})
        t.cleanup(task)
    res["replays"] = replays

    res["auto_run"] = "auto_run=after_" in done
    instant = None
    if res["auto_run"] and all(x["ok"] for x in replays):
        v = samples[4 % len(samples)]
        t1 = time.perf_counter()
        r = t.call("omniwin_do", {"text": fill(wordings[0], v)})
        ms = round((time.perf_counter() - t1) * 1000, 1)
        instant = {"routed": "routed rule=recipe." in r, "ms": ms, "check": t.check(task.get("check"), v), "line": r.splitlines()[0][:120]}
        t.cleanup(task)
    res["instant"] = instant

    aliases = []
    for k, w in enumerate(wordings[1:], start=1):
        v = samples[k % len(samples)]
        r = t.call("omniwin_do", {"text": fill(w, v)})
        if r.startswith("needs_brain"):
            run = t.call("workflow_run", {"name": name, "params": {k: v[k] for k in re.findall(r"\{(\w+)\}", w)}} if slots else {"name": name})
            fine = status(run) in ("confirmed", "unverified") and t.check(task.get("check"), v)
            d = t.call("task_done", {"success": fine})
            aliases.append({"wording": k, "learned": "wording was added" in d, "ran": fine})
        else:
            aliases.append({"wording": k, "learned": False, "already": r.splitlines()[0][:100]})
        t.cleanup(task)
    res["aliases"] = aliases
    replays_ok = all(x["ok"] for x in replays) and len(replays) == 3
    instant_ok = instant is not None and instant["routed"] and instant["check"]
    res["result"] = ("learned" if replays_ok and (instant_ok or not res["auto_run"]) else "saved_but_replays_failed")
    return res


def more_wordings(t, task, name, start):
    out = []
    samples, wordings = task["samples"], task["wordings"]
    for k in range(start, len(wordings)):
        w, v = wordings[k], samples[k % len(samples)]
        t.snapshot()
        slots = re.findall(r"\{(\w+)\}", w)
        r = t.call("omniwin_do", {"text": fill(w, v)})
        if r.startswith("needs_brain"):
            run = t.call("workflow_run", {"name": name, "params": {s: v[s] for s in slots}} if slots else {"name": name})
            fine = status(run) in ("confirmed", "unverified") and t.check(task.get("check"), v)
            d = t.call("task_done", {"success": fine})
            out.append({"wording": k, "learned": "wording was added" in d, "ran": fine})
        else:
            out.append({"wording": k, "learned": False, "already": r.splitlines()[0][:100]})
        t.cleanup(task)
    return out


def main():
    exe, data, tasks_file, tag = sys.argv[1:5]
    only = set(sys.argv[5:])
    tasks = [x for x in json.load(open(tasks_file, encoding="utf-8"))["tasks"] if not only or x["id"] in only]
    out_path = f"../../artifacts/benchmarks/curriculum-{tag}.json"
    try:
        results = json.load(open(out_path, encoding="utf-8"))
    except (OSError, ValueError):
        results = {"tag": tag, "tasks": []}
    done_ids = {r["id"] for r in results["tasks"] if r.get("result") == "learned"}
    o = Omni(exe, data)
    t = Teacher(o)
    try:
        for task in tasks:
            if task["id"] in done_ids:
                prev = next(x for x in results["tasks"] if x["id"] == task["id"])
                tried = 1 + len(prev.get("aliases", []))
                if len(task["wordings"]) > tried and prev.get("recipe") and prev.get("auto_run", True):   # a noisy recipe never gets wordings
                    # more wordings for a task already learned: each is run once through omniwin_do -> workflow_run -> task_done
                    t.log = []
                    extra = more_wordings(t, task, prev["recipe"], tried)
                    prev["aliases"] = prev.get("aliases", []) + extra
                    json.dump(results, open(out_path, "w", encoding="utf-8"), indent=1)
                    print(task["id"], "wordings +", sum(1 for a in extra if a.get("learned")), "of", len(extra), flush=True)
                continue
            t.log = []
            try:
                r = teach(t, task)
            except BotCheck:
                r = {"id": task["id"], "group": task["group"], "result": "bot_check_stop",
                     "detail": "the site showed a bot check; OmniWin never solves or gets around one"}
                try:
                    t.call("task_done", {"success": False}); t.cleanup(task)
                except Exception:
                    pass
            except Exception as ex:  # one task's failure never stops the run
                r = {"id": task["id"], "group": task["group"], "result": "error", "detail": repr(ex)[:300]}
                try:
                    t.cleanup(task)
                except Exception:
                    pass
            results["tasks"] = [x for x in results["tasks"] if x["id"] != task["id"]] + [r]
            json.dump(results, open(out_path, "w", encoding="utf-8"), indent=1)
            print(task["id"], r.get("result"), r.get("detail", "")[:160], flush=True)
            if o.call("omniwin_status").find("kill") >= 0 and "tripped" in o.call("omniwin_status"):
                print("stop key tripped: ending the run"); break
    finally:
        o.close()


if __name__ == "__main__":
    main()
