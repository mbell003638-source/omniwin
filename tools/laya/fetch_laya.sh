#!/bin/bash
# Downloads the Laya typed-decisions variant (Apache-2.0, convaiinnovations/laya) into this folder and verifies the SHA-256 of
# the large weight file against the value Hugging Face publishes for it. Resumable (curl -C -). Owner approved 2026-11-13.
set -u
cd "$(dirname "$0")"
BASE=https://huggingface.co/convaiinnovations/laya/resolve/main
mkdir -p typed-decisions/encoder typed-decisions/tokenizer eval
get() { curl -sL -C - -m 3000 -o "$2" "$BASE/$1" && echo "ok $1 $(stat -c %s "$2")"; }
get README.md README.md
get rl_common.py rl_common.py
get rl_agent_api.py rl_agent_api.py
get config.json config.json
get eval/results.md eval/results.md
get typed-decisions/rl_agent_config.json typed-decisions/rl_agent_config.json
get typed-decisions/encoder/config.json typed-decisions/encoder/config.json
get typed-decisions/tokenizer/tokenizer.json typed-decisions/tokenizer/tokenizer.json
get typed-decisions/tokenizer/tokenizer_config.json typed-decisions/tokenizer/tokenizer_config.json
get typed-decisions/model.safetensors typed-decisions/model.safetensors
want=4fa56de72383a9d3efa9cfa78955733c81b9fc8067a587ca4beb82c78107a24e
got=$(sha256sum typed-decisions/model.safetensors | cut -d' ' -f1)
echo "sha256 want $want"
echo "sha256 got  $got"
if [ "$want" = "$got" ]; then echo "LAYA_DOWNLOAD_VERIFIED"; else echo "LAYA_DOWNLOAD_HASH_MISMATCH"; fi
