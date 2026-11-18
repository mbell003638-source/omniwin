# Proposal: WS3-detected-boxes (step 4 of the fallback order)

## Status
**Not built. Waiting for an owner decision on a download.** The owner decided on 2026-11-08 that nothing is downloaded now, so this is a ready-to-build plan only. No model file, ML runtime or package has been added.

## Where it sits
The model reads a screen in this order (written into the tool descriptions):
1. real buttons and fields (`desktop_observe`, `browser_observe`);
2. text on screen (`screen_ocr`, then `oN` refs);
3. a look-only picture (`screen_view`, `browser_view`);
4. **detected boxes**, for apps that show nothing to read: canvas games, custom-drawn controls, remote views. This is the part that is missing.

Never act by guessing a pixel position. Detected boxes would be the only pixel-based way to act, and each box is checked before a click (below).

## What would be downloaded (needs the owner's yes)
No download has been made; the figures below are what the choice would involve and must be confirmed from the source at the time.

| Part | Candidate | Source | Size (approx.) | Licence |
|---|---|---|---|---|
| Runtime | ONNX Runtime (CPU), the `Microsoft.ML.OnnxRuntime` NuGet package | nuget.org | about 10 MB for the CPU build | MIT |
| Model | A small UI-element detector exported to ONNX. Two realistic options: (a) a YOLO-family model trained on UI screenshots; (b) a permissively licensed detector such as RT-DETR or a MobileNet-SSD fine-tuned on UI data. | Hugging Face or the model authors | 6 to 40 MB depending on the model | **Check before choosing.** Many YOLO models, including the Ultralytics ones, are **AGPL-3.0**, which would put conditions on how OmniWin is shared. Prefer Apache-2.0 or MIT weights. |

Recommendation: option (b) with Apache-2.0 or MIT weights, so the licence stays simple. Nothing is chosen until the owner approves a specific model and its licence.

## Expected speed on this PC's CPU (estimate, not measured)
A small detector at 640 x 640 input on a modern desktop CPU, without a GPU, typically takes about 40 to 150 ms per frame. Add the capture and masking time of `screen_view` (measured at about 32 ms p50 for the 1080p masking and encoding pipeline, see `artifacts/benchmarks/view-screen-v1.json`; the capture step itself is not yet measured). So a call would be expected near 100 to 250 ms. These are estimates and must be measured on the reference PC once a model is chosen. There is no budget in contract §15 for it yet; a report-only row of 300 ms for one 1920 x 1080 window is proposed.

## How a detected box becomes a ref
- New tool `screen_detect` (look-only: it reads pixels like `screen_view` and sends no input). Params: `window?`, `region?`. Same window rules, same masking as `screen_view`, so password fields and blocked windows are black before the detector sees them.
- Each box gets a ref `dN`, numbered monotonically per server session and never reused, like `oN`. Output line: `d7 "button-like" conf=0.82` plus the box in physical screen pixels, under the untrusted-content header. If OCR text sits inside the box, it is attached as the name.
- A `DetectedBoxStore` (like `OcrRefStore`) keeps the box, the window handle and the capture time.
- `desktop_act` would accept `dN` refs for **click, double_click and right_click only**, exactly like `oN`.

## How a box is checked before a click
Same pre-checks as `oN` (contract §7), plus one for detection:
1. **Not stale:** the capture is at most 10 seconds old, otherwise `stale`.
2. **Same window:** the window handle still exists, is not minimized, is not elevated or on the secure desktop, and is not one of the owner's blocked windows.
3. **Nothing covering it:** the point to click is inside that window and no other top-level window is above it there (`occluded_by_window` otherwise).
4. **Box still there:** a fresh capture of just that box's area is compared with the stored crop (a perceptual hash of the crop within a set distance). If the pixels changed, the result is `stale` and nothing is sent. This is the extra check that `oN` does not have, because a detected box has no text to match.
5. **Honest result:** with no explicit expectation the best result is `unverified`, since a box has no element identity. `confirmed` needs an `expect`.
6. The stop key and the user-activity pause apply as for every click.

## How the no-delete rule still applies
- The gate sees the action with the box's **name**: the attached OCR text if there is any, otherwise the detector's label. A name that matches the delete rule (for example OCR text "Delete") is denied by the existing `DeletePolicy`, with `input_sent=false`.
- A box with **no readable name** cannot be proven safe. The owner's policy blocks only deleting, so such a click is allowed, but it is labelled `Risky` (a record only; the label never blocks) and logged in the audit line.
- Typed text and keys sent after a click are still checked by the existing delete-command rules; detected boxes add no way around them.
- The detector adds no way to run a command, open a path or reach a window the existing blocklist refuses.

## Work involved once approved
About one working session: the `screen_detect` tool and `DetectedBoxStore` in `OmniWin.Ocr`, `dN` handling in `desktop_act` (a small `OmniWin.Desktop` change next to the `oN` code), tests with a fake detector (box-to-ref, stale, covered, changed-pixels, delete name, unnamed box), a real-model smoke test on the fixture app, and a speed measurement.

## Decision needed from the owner
1. May ONNX Runtime (MIT, about 10 MB) be added as a NuGet package?
2. May a UI-detector ONNX model be downloaded? Please name the model or accept the recommendation (permissive licence only), and say whether an AGPL model is acceptable (not recommended).
