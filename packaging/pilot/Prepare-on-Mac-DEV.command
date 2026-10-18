#!/bin/bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
if [ "$(uname -m)" != "arm64" ]; then echo "Prepare this bundle on the Apple Silicon Mac development VM."; exit 1; fi
pilot_entitlements=$(mktemp "${TMPDIR:-/tmp}/preset-sender-entitlements.XXXXXX")
trap 'rm -f "$pilot_entitlements"' EXIT
cat > "$pilot_entitlements" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>
PLIST
echo "Signing private test binaries on the Mac development machine. No SDK or PowerShell is installed."
while IFS= read -r -d '' pilot_file; do
    if /usr/bin/file -b "$pilot_file" | /usr/bin/grep -q 'Mach-O'; then
        chmod u+x "$pilot_file"
        case "$pilot_file" in
            bin/lab/PresetSender.Lab|bin/probe/PresetSender.Pilot|bin/tests/PresetSender.Tests|bin/tests/PresetSender|app/PresetSender)
                /usr/bin/codesign --force --sign "${PILOT_SIGNING_IDENTITY:--}" --timestamp=none --entitlements "$pilot_entitlements" "$pilot_file" ;;
            *) /usr/bin/codesign --force --sign "${PILOT_SIGNING_IDENTITY:--}" --timestamp=none "$pilot_file" ;;
        esac
        /usr/bin/codesign --verify --strict "$pilot_file"
    fi
done < <(/usr/bin/find bin app -type f -print0)
./bin/lab/PresetSender.Lab seal --root "$PWD"
echo "Prepared. Run Setup-Mac.command here to validate, then copy this whole folder to the test Macs."
echo "The same folder can be used in both native macOS boots and the macOS VM."
if [ -t 0 ] && [ "${PRESET_SENDER_NO_PAUSE:-0}" != "1" ]; then read -r -p "Press Return to close..." _pilot_key; fi
