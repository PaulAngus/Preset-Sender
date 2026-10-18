#!/bin/bash
cd "$(dirname "$0")" || exit 1
./bin/lab/PresetSender.Lab pair --root "$PWD" "$@"
pilot_exit=$?
if [ -t 0 ] && [ "${PRESET_SENDER_NO_PAUSE:-}" != "1" ]; then read -r -p "Press Return to close..."; fi
exit "$pilot_exit"
