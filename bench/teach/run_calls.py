"""Runs a JSON list of [tool, args] calls in one OmniWin process and prints each result (exploration)."""
import json, sys
from omni import Omni
exe, data, calls, limit = sys.argv[1], sys.argv[2], json.load(open(sys.argv[3], encoding="utf-8")), int(sys.argv[4]) if len(sys.argv) > 4 else 2500
o = Omni(exe, data)
try:
    for name, args in calls:
        r = o.call(name, args)
        print(f">>> {name} {json.dumps(args)[:120]}\n{r[:limit]}\n")
finally:
    o.close()
