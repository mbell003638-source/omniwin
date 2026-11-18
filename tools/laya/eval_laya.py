"""
Offline gate for Laya (plan P7). Sends NO input and touches nothing on the desktop: it only runs the model on text.

  tools\\laya\\.venv\\Scripts\\python.exe tools\\laya\\eval_laya.py [--device cpu|cuda|xpu] [--out artifacts/benchmarks/laya-eval-v1.json]

Reads eval_set.jsonl (build_eval_set.py, written next to the phrase rules, so it is the easy set) and eval_hard.jsonl (build_hard_set.py,
hand-written paraphrases and near-miss sentences, the set that matters for deciding whether Laya adds anything), asks the model for the intent of every text, and reports:
  - intent accuracy over everything and over the accepted answers,
  - at each confidence threshold: coverage (share of commands answered), WRONG-ACTION RATE (a command would be run, but it is the
    wrong one, or the text was "none"), accuracy of the accepted answers,
  - latency p50 / p95 / max (warm).
The gate (all must hold at one threshold): wrong-action rate <= 0.5%, accuracy of accepted answers >= 95%, median latency <= 50 ms.
The lowest threshold that meets the first two is the calibrated one; the latency is reported separately because it depends on the PC.
"""
import argparse
import json
import os
import statistics
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from laya_server import QUESTIONS, load_agent  # noqa: E402

THRESHOLDS = [0.5, 0.6, 0.7, 0.8, 0.85, 0.9, 0.93, 0.95, 0.97, 0.98, 0.99, 0.995]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", default=None)
    parser.add_argument("--out", default=os.path.join(HERE, "..", "..", "artifacts", "benchmarks", "laya-eval-v1.json"))
    parser.add_argument("--limit", type=int, default=0)
    args = parser.parse_args()

    items = []
    for name, tag in (("eval_set.jsonl", "easy"), ("eval_hard.jsonl", "hard")):
        for line in open(os.path.join(HERE, name), encoding="utf-8"):
            if line.strip():
                item = json.loads(line)
                item["set"] = tag
                items.append(item)
    if args.limit:
        items = items[: args.limit]
    agent = load_agent(args.device)
    agent.system_one("warm up", QUESTIONS)

    rows, times = [], []
    for item in items:
        started = time.perf_counter()
        answer = agent.system_one(item["text"], QUESTIONS)["answers"]["intent"]
        ms = (time.perf_counter() - started) * 1000
        times.append(ms)
        rows.append({"text": item["text"], "label": item["intent"], "pred": answer["choice"], "conf": answer["confidence"], "ms": ms, "set": item["set"]})

    total = len(rows)
    accuracy = sum(r["pred"] == r["label"] for r in rows) / total
    table = []
    for t in THRESHOLDS:
        accepted = [r for r in rows if r["conf"] >= t and r["pred"] != "none"]
        wrong = [r for r in accepted if r["pred"] != r["label"]]
        table.append({
            "threshold": t,
            "accepted": len(accepted),
            "coverage": round(len(accepted) / total, 4),
            "wrong_action_rate": round(len(wrong) / total, 4),
            "accepted_accuracy": round(1 - len(wrong) / len(accepted), 4) if accepted else None,
            "meets_accuracy_gate": bool(accepted) and len(wrong) / total <= 0.005 and 1 - len(wrong) / len(accepted) >= 0.95,
        })

    calibrated = next((row["threshold"] for row in table if row["meets_accuracy_gate"]), None)

    # The gate is judged on the HARD set at the calibrated threshold (the easy set flatters any classifier); both are reported.
    by_set = {}
    for tag in ("easy", "hard"):
        subset = [r for r in rows if r["set"] == tag]
        if not subset:
            continue
        t = calibrated if calibrated is not None else 0.9
        accepted = [r for r in subset if r["conf"] >= t and r["pred"] != "none"]
        wrong = [r for r in accepted if r["pred"] != r["label"]]
        by_set[tag] = {
            "items": len(subset),
            "intent_accuracy": round(sum(r["pred"] == r["label"] for r in subset) / len(subset), 4),
            "at_threshold": t,
            "coverage": round(len(accepted) / len(subset), 4),
            "wrong_action_rate": round(len(wrong) / len(subset), 4),
            "accepted_accuracy": round(1 - len(wrong) / len(accepted), 4) if accepted else None,
        }
    times_sorted = sorted(times)
    latency = {
        "p50_ms": round(statistics.median(times), 1),
        "p95_ms": round(times_sorted[int(0.95 * (len(times) - 1))], 1),
        "max_ms": round(max(times), 1),
    }
    gate = {
        "calibrated_threshold": calibrated,
        "accuracy_gate_met": calibrated is not None,
        "latency_gate_met": latency["p50_ms"] <= 50,
    }
    hard = by_set.get("hard")
    gate["hard_set_wrong_action_rate_ok"] = bool(hard) and hard["wrong_action_rate"] <= 0.005
    gate["integrate"] = gate["accuracy_gate_met"] and gate["latency_gate_met"] and gate["hard_set_wrong_action_rate_ok"]

    worst = sorted((r for r in rows if r["pred"] != r["label"]), key=lambda r: -r["conf"])[:15]
    report = {
        "what": "Laya typed-decisions, offline, intent only (no input sent)",
        "device": str(agent.device),
        "items": total,
        "intent_accuracy": round(accuracy, 4),
        "thresholds": table,
        "by_set": by_set,
        "latency_warm": latency,
        "gate": gate,
        "most_confident_mistakes": worst,
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    path = os.path.abspath(args.out)
    n = 2
    while os.path.exists(path):
        path = os.path.abspath(args.out).replace("-v1", f"-v{n}")
        n += 1
    with open(path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    print(json.dumps({"device": report["device"], "accuracy": report["intent_accuracy"], "latency": latency, "gate": gate}, indent=2))
    print("wrote", path)


if __name__ == "__main__":
    main()
