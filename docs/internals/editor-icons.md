# Editor icons

How ePlugin's editor icons are drawn, so that new ones match the existing set. The icons live in
`src/eplugin-framework/addons/ePlugin/icons/` and are rendered by `Internal/Manager/EditorIcons.cs`.

## Format

- SVG, `width="16" height="16" viewBox="0 0 16 16"`, like Godot's own editor icons. No PNGs: the icons are rendered at
  the editor scale, and a raster would blur.
- One icon per file, named after the action in snake_case (`rescan.svg`, `downgrade.svg`), not after the place it is
  used.
- Keep every stroke inside the canvas: with a 1.5 stroke, centerlines stay between 0.75 and 15.25.
- Masks use `maskUnits="userSpaceOnUse"` and only `#fff`/`#000`, so recoloring never touches them.
- `eplugin.svg` is the colored brand logo and follows none of this.

## Colors

Only these colors appear in an icon. `EditorIcons.Render` replaces them, so they are placeholders as much as colors:

| Color | Meaning | Rendered as |
|---|---|---|
| `#e0e0e0` | Base color of every icon | `#e0e0e0` on dark icon palettes, `#5a5a5a` on light ones |
| `#5fff97` | Success: installs a version (upgrade, reinstall) | the editor theme's `success_color` |
| `#ffdd65` | Warning: goes back or needs care (downgrade) | the theme's `warning_color` |
| `#ff5f5f` | Error: deals with something that failed (retry) | the theme's `error_color` |

The accent values are the ones Godot's own icons use, so the imported texture (`convert_colors_with_editor_theme`) also
looks right.

Rules:

- The ring or outline is always `#e0e0e0`. An accent goes only on the glyph inside it.
- At most one accent per icon.
- Actions that change no installed plugin stay monochrome (rescan, settings, about).
- `update.svg` is drawn in `#fff` because it doubles as the white mask that `UpdateIndicator` tints. Don't copy that into
  new icons.

## The ring style

Most action icons are a ring with a glyph inside. The ring says how often the action happens, and the glyph says what
it does.

| Ring | Meaning | Path (stroke 1.5) |
|---|---|---|
| Closed | Do it once | `<circle cx="8" cy="8" r="6.6"/>` |
| Clockwise arrow | Do it again (check again, install again) | `M14.6 8A6.6 6.6 0 1 1 13.41 4.21M13.41 1.3v2.91h-2.91` |
| Counter-clockwise arrow | Try again after a failure | `M1.4 8A6.6 6.6 0 1 0 2.59 4.21M2.59 1.3v2.91h2.91` |

All rings use `fill="none"`, `stroke-linecap="round"` and `stroke-linejoin="round"`.

Glyphs sit inside the ring, roughly between 4.4 and 11.6, with a stroke of 1.6 to 1.8 and round caps:

| Glyph | Path | Stroke |
|---|---|---|
| Up arrow | `M8 11.6V4.9M5.2 7.6 8 4.8l2.8 2.8` | 1.7 |
| Down arrow | `M8 4.4v6.7M5.2 8.4 8 11.2l2.8-2.8` | 1.7 |
| Plus | `M8 5v6M5 8h6` | 1.7 |
| Exclamation mark | `M8 4.6v3.8` plus `<circle cx="8" cy="11.1" r="1.05"/>` filled | 1.8 |
| Information | `<circle cx="8" cy="4.9" r="1.05"/>` filled plus `M8 7.6v3.8` | 1.8 |

An action with a well-known shape of its own (the gear for settings) may skip the ring. It uses the same weight: shapes
at least 1.5 wide, filled in `#e0e0e0`.

## Current icons

| File | Action | Ring | Glyph |
|---|---|---|---|
| `eplugin_editor.svg` | ePlugin Manager, ePlugin rows | – | Robot head and puzzle piece (`id="piece"` is tinted by status) |
| `update.svg` | Update available, Check for updates | Closed | Up arrow (white mask) |
| `rescan.svg` | Rescan for updates | Clockwise | Up arrow |
| `settings.svg` | Settings | – | Gear |
| `retry.svg` | Retry failed updates | Counter-clockwise | `!` in error |
| `upgrade.svg` | Upgrade | Closed | Up arrow in success |
| `reinstall.svg` | Reinstall | Clockwise | Plus in success |
| `downgrade.svg` | Downgrade | Closed | Down arrow in warning |
| `about.svg` | About | Closed | `i` |

Rescan and upgrade differ by the ring and the color only, so don't put them next to each other without a label.

## Adding an icon

1. Draw it on the 16×16 grid with the colors, ring and glyph rules above. Check it at 16 px on a dark and a light
   editor theme. Detail finer than about 1.5 px disappears at 100 % scale.
2. Save it in `addons/ePlugin/icons/` and let the editor import it. In its `.import` file, set
   `editor/scale_with_editor_scale=true` and `editor/convert_colors_with_editor_theme=true`, then commit both files.
3. Add a path constant and a property to `EditorIcons`, and use the property. Don't load the imported texture: a
   headless editor imports it at 100 % scale.
4. Add the path to `ActionIconsFollowTheIconPaletteAndTheThemeStatusColors` in `tests/EditorIconsTests.cs`.
5. Add the icon to the table above.
