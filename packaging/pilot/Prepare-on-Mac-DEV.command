#!/bin/bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
if [ "$(uname -m)" != "arm64" ]; then echo "Prepare this bundle on the Apple Silicon Mac development VM."; exit 1; fi
echo "Signing private test binaries on the Mac development machine. No SDK or PowerShell is installed."
while IFS= read -r -d '' pilot_file; do
    if /usr/bin/file -b "$pilot_file" | /usr/bin/grep -q 'Mach-O'; then
        chmod u+x "$pilot_file"
        /usr/bin/codesign --force --sign "${PILOT_SIGNING_IDENTITY:--}" --timestamp=none "$pilot_file"
        /usr/bin/codesign --verify --strict "$pilot_file"
    fi
done < <(/usr/bin/find bin app -type f -print0)
./bin/lab/PresetSender.Lab seal --root "$PWD"
echo "Prepared. Run Setup-Mac.command here to validate, then copy this whole folder to the test Macs."
echo "The same folder can be used in both native macOS boots and the macOS VM."
if [ -t 0 ] && [ "${PRESET_SENDER_NO_PAUSE:-0}" != "1" ]; then read -r -p "Press Return to close..." _pilot_key; fi
