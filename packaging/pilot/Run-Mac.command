#!/bin/bash
cd -- "$(dirname -- "$0")" || exit 1
if [ "$(uname -m)" != "arm64" ]; then echo "Use this package on an Apple Silicon Mac."; exit 1; fi
./bin/lab/PresetSender.Lab run --root "$PWD" "$@"
pilot_exit=$?
if [ -t 0 ] && [ "${PRESET_SENDER_NO_PAUSE:-0}" != "1" ]; then read -r -p "Press Return to close..." _pilot_key; fi
exit "$pilot_exit"
