# Preset Sender project guidance

## Publishing and verification

- Preset Sender must remain a standalone EXE: bundle the native MIDI API and its resource file for automatic app-private extraction on eligible Windows 11 builds. Do not reintroduce a separate MIDI API installation requirement. Windows 10 uses hardware MIDI or user-installed loopMIDI; never extract or load the bundled MIDI Services API there.

- For Windows MIDI setup/startup fixes, validate by double-clicking the published EXE in the user's File Explorer, then check virtual-output discovery and connection. Tool-launched processes alone are insufficient: the optional API can exist in the tool's filesystem view while being absent from the normal desktop. Verify that the Explorer-launched process actually loads the optional API. The latest detection result is recorded in `%LOCALAPPDATA%\PresetSender\midi-backend.log`.

## UI and UX changes

- Keep settings sections visible in ordinary labelled sections or cards. Do not hide forms, groups of controls, explanatory text or additional settings inside expanders, accordions or collapsible panels such as "Additional options", unless the user explicitly requests that layout.
- Reserve dropdowns/ComboBoxes for choosing one value from a list, such as a MIDI port, channel, numbering scheme or command. Do not use them as containers for multiple settings or actions.
- For new or substantially changed UI elements and interactions, consult current official Microsoft Fluent 2 and Windows app design guidance for the relevant pattern before implementing. Do not require the user to request this research each time.
- Apply the guidance to Avalonia using the application's existing theme and controls. Distinguish documented requirements from product choices, and record relevant primary-source links and any deliberate deviations in the applicable UX document.
- Keep dismissal controls separate from task actions. For a details pane, put a clearly labelled close control at the trailing edge of its header, keep it reachable while scrolling, support keyboard dismissal, and return focus to the originating content.
- Check readable labels, spacing, hierarchy, keyboard access, accessible names, and focus behavior. Inspect rendered results in light and dark themes at normal and supported narrow window sizes before completing a layout change.
- Use proportionate regression checks for changed interaction behavior; do not replace visual inspection with assertions that merely mirror control construction.
