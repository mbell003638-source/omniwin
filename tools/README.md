# OmniWin tools folder

Third-party programs that OmniWin can start, downloaded on 2026-11-13 with the owner's approval into this folder only (no system-wide
install, no PATH change, nothing registered to start at login). Everything here is data for the server: nothing in it is OmniWin's
own code except the `laya` scripts and the `windows-mcp\config.toml` comment file.

| Folder | What | Version | Source | SHA-256 of the download |
|---|---|---|---|---|
| `uv\` | the `uv` package manager (three executables) | 0.13.8 | github.com/astral-sh/uv releases | `529e78a9cec6cd7e3e014f7ecd8bda0c113969d4ed0f2efeedb85c30943325b8` (zip; matches the value GitHub publishes) |
| `python\` | Python 3.14.10 (managed by uv) | 3.14.10 | python-build-standalone, fetched by `uv python install` | verified by uv |
| `windows-mcp\.venv` | Windows-MCP and 85 pinned packages | windows-mcp 0.8.9 | PyPI | wheel `e90e5b02b3e545991f203ce93cefb436a326861657968591653fa254105dc85d`; every package is pinned with hashes in `windows-mcp\requirements.lock` and was installed with `--require-hashes` |
| `agent-desktop\` | the agent-desktop command-line program | 1.3.1 | github.com/vercel-labs/agent-desktop releases | `6ee4b1e078f4e78792e79f16e32e4a78b9919464aa3c55629b7e27ed53a08c13` (tar.gz; matches `checksums.txt`) |
| `laya\typed-decisions\` | Laya decision model weights (typed-decisions variant), tokenizer and config | convaiinnovations/laya, Apache-2.0 | huggingface.co | `model.safetensors` `4fa56de72383a9d3efa9cfa78955733c81b9fc8067a587ca4beb82c78107a24e` (matches the value Hugging Face publishes) |

`_dl\` holds the two archives that were downloaded and extracted (kept, not deleted). `uv-cache\` is uv's package cache.

## Rules for these programs (enforced by OmniWin, see `src\OmniWin.Server\Engines`)
- Windows-MCP is started with `ANONYMIZED_TELEMETRY=false`, `--no-control` and `--exclude-tools PowerShell,Registry,FileSystem,Process`
  (the exclusion is lifted only when `power_tools.enabled` is true). Never run `windows-mcp install`: it registers a scheduled task.
- With power tools off OmniWin itself also refuses: Windows-MCP `App` arguments `executable`, `args`, `cwd` and `save_path` of
  `Screenshot`/`Snapshot` (they start any program or write a file); and for agent-desktop the tools `desktop_launch`, `desktop_batch`,
  `desktop_close_app`, `desktop_trace_export` and `desktop_sandbox_*`. Both engines can still drive a terminal window through the
  screen like any other app: only the dedicated command tools and arguments are filtered.
- agent-desktop is started as `agent-desktop mcp --session omniwin` (headless: no focus changes, no cursor movement; add `--headed`
  in an explicit `engines.list` entry to allow physical input).
- Results from either are never `confirmed` by OmniWin, and an engine call holds the shared input lane.

## Laya (tools\laya)
- `laya_server.py`: the intent server OmniWin starts (stage B of `omniwin_do`). `eval_laya.py` is the offline gate (sends no input).
  `build_eval_set.py` regenerates `eval_set.jsonl` (615 labelled commands).
- `laya\.venv` holds PyTorch 2.15.0 (CPU) and transformers 5.23.0, installed 2026-11-14 with the owner's yes, from `laya\requirements.lock`
  (every package pinned with hashes, installed with `--require-hashes`; about 680 MB). Nothing was installed outside `tools`; the
  uv shim problem did not repeat because `UV_PYTHON_BIN_DIR` pointed under `tools`.
- **Gate result: FAILED, Laya stays off.** Laya offline gate, run 2026-11-14 on this PC (`artifacts/benchmarks/laya-eval-v1.json`; CPU, PyTorch 2.15.0, zero-shot, 793 labelled commands, no input sent): intent accuracy 61% (easy set 63%, hand-written hard set 55%); at the highest threshold tried (0.995) it still answers 33% of the commands and 4.8% of ALL commands would be run as the WRONG one (hard set 18% at 0.9); confident mistakes exist at confidence 1.00 (for example "find me a good thriller on netflix and play it" -> media_play), so no threshold makes it safe; median latency 827 ms (p95 928 ms; a 30-item trial gave 422 ms). All three gates failed (wrong-action <= 0.5%, accepted accuracy >= 95%, median <= 50 ms), so **Laya is not integrated and stays off** (`router.laya.enabled` false). The phrase router is the router. Not tried: fine-tuning (the vendor says it is needed; its repository has no training code) and GPU/NPU acceleration (no discrete GPU; the Intel Arc iGPU would need a separate PyTorch build).
