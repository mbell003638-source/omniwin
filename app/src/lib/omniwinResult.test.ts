import assert from "node:assert/strict";
import { explain } from "./omniwinResult";

// Quick command, checked.
assert.deepEqual(explain("status=confirmed input_sent=true channel=api retry=done total=3ms\nrouted rule=volume.set"), {
  outcome: "done",
  recipe: null,
  detail: null,
  suggestion: null,
});

// A learned task, run from the words, which OmniWin could not confirm: never shown as done.
const unverified = explain(
  "workflow_run name=fill-form status=unverified input_sent=true steps=3/3 total=310ms\nrouted rule=recipe.fill-form",
);
assert.equal(unverified.outcome, "unverified");
assert.equal(unverified.recipe, "fill-form");

// A pick by the decision model names its recipe the same way.
assert.equal(
  explain("workflow_run name=x status=confirmed input_sent=true steps=2/2 total=9ms\nrouted rule=decision.recipe.x").recipe,
  "x",
);

// Stopped part-way by a changed screen.
assert.equal(
  explain("workflow_run name=x status=stale input_sent=true reason=not_found steps=2/3 stopped_at=3 total=90ms\nrouted rule=recipe.x").outcome,
  "stopped",
);

// Needs the AI, with OmniWin's suggestion picked up.
const ai = explain(
  'needs_brain reason=no_rule input_sent=false retry=safe\ndetail: No quick rule matches.\nrecipe: fill-form(name) steps=3 trust=candidate goal="fill the form for bob"\nlearn: ...',
);
assert.equal(ai.outcome, "needsAi");
assert.equal(ai.detail, "No quick rule matches.");
assert.equal(ai.suggestion, "fill-form");

// Refused with nothing sent, and stopped after something was sent.
assert.equal(explain("status=denied input_sent=false reason=policy_forbidden retry=no total=1ms").outcome, "refused");
assert.equal(explain("status=blocked input_sent=false reason=user_active retry=wait total=1ms").outcome, "refused");
assert.equal(explain("status=error input_sent=true reason=input_interrupted retry=check_state total=1ms").outcome, "stopped");
assert.equal(explain("status=error input_sent=false reason=engine_timeout retry=safe total=1ms").outcome, "failed");
assert.equal(explain("something else entirely").outcome, "failed");

console.log("omniwinResult: all checks passed");
