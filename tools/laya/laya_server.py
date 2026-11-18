"""
Laya intent server for OmniWin's router (stage B). Runs as a child process of OmniWin.Server; talks JSON lines on stdin/stdout.

  in : {"id": 1, "text": "turn it up a bit"}
  out: {"id": 1, "intent": "volume_up", "confidence": 0.93, "ms": 41.2}      (or {"id": 1, "error": "..."})
First line out, once the model is loaded: {"ready": true, "device": "cpu", "load_ms": 5321.0}

It only names an INTENT from a fixed list. It never supplies a number or a name: OmniWin reads those out of the text with rules and
matches names against its own window, app and workflow lists. It sends no input and touches nothing on the desktop.

Needs (inside tools/laya/.venv): torch, transformers, safetensors, numpy. Files: typed-decisions/ (Apache-2.0, convaiinnovations/laya).
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

# Keep this list in step with PhraseRouter.Intents in src/OmniWin.Server/Router/PhraseRouter.cs.
INTENTS = [
    "media_play", "media_pause", "media_toggle", "media_next", "media_previous",
    "volume_set", "volume_up", "volume_down", "mute", "unmute",
    "window_focus", "window_minimize", "window_maximize", "window_restore", "window_close",
    "app_launch", "workflow_run", "read_time", "read_battery", "read_volume", "none",
]

QUESTIONS = {
    "intent": {
        "type": "choice",
        "instructions": "Which quick desktop command does the user's request ask for? Choose none for anything else.",
        "criteria": INTENTS,
    }
}


def load_agent(device):
    from rl_agent_api import RLAgent  # needs torch + transformers
    return RLAgent(os.path.join(HERE, "typed-decisions"), device=device)


def main():
    device = os.environ.get("LAYA_DEVICE") or None
    t0 = time.perf_counter()
    agent = load_agent(device)
    # one warm-up pass so the first real request is not slower than the rest
    agent.system_one("warm up", QUESTIONS)
    load_ms = (time.perf_counter() - t0) * 1000
    sys.stdout.write(json.dumps({"ready": True, "device": str(agent.device), "load_ms": round(load_ms, 1)}) + "\n")
    sys.stdout.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
            rid = request.get("id")
            started = time.perf_counter()
            out = agent.system_one(str(request.get("text", "")), QUESTIONS)
            answer = out["answers"]["intent"]
            reply = {
                "id": rid,
                "intent": answer["choice"],
                "confidence": answer["confidence"],
                "ms": round((time.perf_counter() - started) * 1000, 2),
            }
            if request.get("probabilities"):
                reply["probabilities"] = answer["probabilities"]
        except Exception as exc:  # report, never crash the loop
            reply = {"id": request.get("id") if isinstance(request, dict) else None, "error": type(exc).__name__ + ": " + str(exc)[:200]}
        sys.stdout.write(json.dumps(reply) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
