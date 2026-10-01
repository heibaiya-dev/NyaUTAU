# Product

<!-- impeccable:product-schema 1 -->

## Platform

Cross-platform Avalonia desktop application for Windows, macOS, and Linux.

## Product Purpose

NyaUTAU builds on OpenUtau to arrange singing tracks, edit lyrics, notes and pitch,
configure voicebanks and phonemizers, and render and play singing projects.
These capabilities are established by the repository and its existing views.

## Capabilities and Constraints

Preserve project and voicebank compatibility, editor commands, shortcuts,
localization, track colors, and custom YAML themes. Dense piano-roll controls and
timeline coordinates must remain usable with a mouse and keyboard.

Smart pitch generates editable pitch control points for selected notes, or all
notes in the current voice part when none are selected, with one-step undo.

Automatic vocal tuning combines pitch, vibrato, dynamics, breathiness and
articulation using Natural, Gentle and Powerful presets with adjustable strength.
It uses the same selection scope and one-step undo, and only enables expression
channels supported by the track and singer. It is deterministic rule-based tuning;
no trained model or external service is required.

USTX version recovery is explicitly chosen from an unsupported-version prompt.
It writes a separate converted project in the supported schema, preserving the
source file. Existing valid tempo events are retained; missing initial tempo is
recovered from the original project's header BPM when available.

## Brand Commitments

The user requested and confirmed Material 3 styling across the whole desktop UI,
the NyaUTAU display name, and the supplied appicon.png/appicon.svg artwork.
This request changes the desktop interface; it does not establish an Android
application target. Existing project and plugin identifiers remain compatible.

First launch opens a quick-start page with both Continue and Skip actions.
Either action records completion and resumes any pending project or recovery.

## Open Decisions

No Android packaging, phone layout, or wallpaper-derived dynamic color is required
by the current desktop redesign.
