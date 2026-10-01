---
name: NyaUTAU Material 3
description: Android Material 3 visual language for a desktop singing editor.
colors:
  primary-light: "#6750A4"
  primary-dark: "#D0BCFF"
  surface-light: "#FFFBFE"
  surface-dark: "#141218"
  container-light: "#F3EDF7"
  container-dark: "#211F26"
  on-surface-light: "#1D1B20"
  on-surface-dark: "#E6E0E9"
rounded:
  field: "4px"
  popup: "12px"
  toolbar: "16px"
  action: "20px"
  editor: "24px"
  welcome: "28px"
spacing:
  compact: "4px"
  related: "8px"
  group: "16px"
  section: "24px"
---

# NyaUTAU Material 3

## Overview

Use the Android Material 3 component language throughout the desktop application:
tonal surfaces, pill actions, rounded selection indicators, filled fields and
outlined switches. Preserve the information density of a music editor.

## Colors

`OpenUtau/Colors/LightTheme.axaml` and `DarkTheme.axaml` define complete schemes.
`Brushes.axaml` exposes `Material*Brush` roles, mapped to existing palette entries
so installed YAML themes remain compatible. Resolve brushes dynamically to allow
live theme changes. The purple primary identifies actions; track colors continue
to identify musical content. Pitch curves retain distinct accent roles.

## Typography

Keep the localized `ui.fontfamily` fallback chains for multilingual lyrics and UI.
Headlines use 28–32, settings page titles 24, list section titles 20, major actions
14–15, and compact editor labels 12. Use medium weight for hierarchy.

## Layout

The main window retains desktop menus inside a 56-unit application bar. The
transport precedes the original 24-unit timeline ruler. Track headers preserve
their compact internal rows. The piano toolbar scrolls horizontally when needed;
its canvas and editor coordinates do not depend on control decoration.

Preferences has a category drawer and independently scrolling content. Setting
labels and switches occupy separate columns. Default actions use 40-unit height;
editor tools use 32 and legacy fixed editor rows may remain 20.

## Elevation & Depth

Separate regions with surface/container/high-container tones. Keep the main
editor free of inset shadows. Use outlines only where input or selection needs
an explicit boundary.

## Shapes

Use 12–16-unit corners for popups and tool groups, pills for buttons and category
selection, 24-unit corners for the editor shell, and 28 for the welcome surface.
Musical rectangles and note geometry remain purpose-built for editing.

## Components

`MaterialStyles.axaml` defines `material-primary`, `material-tonal`,
`material-outlined`, `material-text`, and `material-icon` button classes and shared
field, popup, tooltip, progress and selection styles. `MaterialToggleStyles.axaml`
styles existing Avalonia switch and checkbox templates, preserving keyboard and
pointer behavior. Standard switches have 52×32 tracks; note properties use 40×24.

The piano toolbar exposes Smart pitch as a tonal action. It generates pitch control
points for selected notes, or all notes in the current part when the selection is
empty. The same command appears under Batch edits → Notes. Generated points remain
editable and the operation is a single undo step.

## Do's and Don'ts

- Keep light and dark schemes equally usable and check text contrast.
- Reuse localized strings, actual commands and existing control names.
- Keep keyboard focus visible and disabled controls distinguishable.
- Preserve custom theme palette mappings and track-color semantics.
- Do not add phone navigation or change musical hit-test coordinates merely to
  resemble a mobile application.
