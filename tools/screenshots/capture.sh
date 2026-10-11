#!/usr/bin/env bash
# Regenerates the documentation screenshots in docs/images.
#
# Starts the Godot editor on the sample project with a throw-away editor configuration (English, default theme, no
# local plugin directories of your own), opens every ePlugin dialog with sample data, and captures each window with
# its frame. Needs a desktop session with X11 or Xwayland, GODOT_BIN pointing at a Godot .NET editor, the .NET SDK,
# python3 with python-xlib and Pillow, and git for the progress helper build. Windows appear on screen while it runs;
# don't touch the mouse or keyboard until the editor closes.
#
#   tools/screenshots/capture.sh [output directory, default docs/images]
#
# EPLUGIN_SCREENSHOTS_SCALE (default 0.5) scales the images, e.g. 0.5 for a 200% display.
# EPLUGIN_SCREENSHOTS_DARK=1 switches GNOME to its dark style while capturing, so window frames match the editor.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
project="$repo/src/eplugin-framework"
out=$(realpath -m "${1:-$repo/docs/images}")
# Paths below this directory show up in the screenshots, so it has a short, neutral name.
samples=/tmp/eplugin-docs
work=$(mktemp -d)
: "${GODOT_BIN:?Set GODOT_BIN to a Godot .NET editor binary.}"

restore() {
    rm -f "$project/DocScreenshots.cs" "$project/DocScreenshots.cs.uid"
    [[ -f "$work/project.godot" ]] && cp "$work/project.godot" "$project/project.godot"
    [[ -f "$work/eplugin-state.json" ]] && cp "$work/eplugin-state.json" "$project/addons/eplugin-state.json"
    rm -f "$project/addons/eplugin-update-sites.json"
    [[ -n "${color_scheme:-}" ]] && gsettings set org.gnome.desktop.interface color-scheme "$color_scheme"
    rm -rf "$work" "$samples"
}
trap restore EXIT

cp "$project/project.godot" "$work/project.godot"
cp "$project/addons/eplugin-state.json" "$work/eplugin-state.json"
mkdir -p "$out" "$work/config/godot"

# English editor UI regardless of the system language; everything else keeps Godot's defaults.
cat > "$work/config/godot/editor_settings-4.7.tres" <<'EOF'
[gd_resource type="EditorSettings" format=3]

[resource]
interface/editor/editor_language = "en"
EOF

# Example releases of the sample plugins for the local plugin directory: 1.1.0 of three plugins and an older 1.0.5.
rm -rf "$samples"
python3 - "$project/addons" "$samples/releases" <<'EOF'
import os, re, sys, zipfile
addons, releases = sys.argv[1], sys.argv[2]
os.makedirs(releases)
for slug in ("sample_plugin", "sample_dependant_plugin", "sample_optional_plugin"):
    for version in ("1.0.5", "1.1.0"):
        with zipfile.ZipFile(os.path.join(releases, f"{slug}-{version}.zip"), "w", zipfile.ZIP_DEFLATED) as archive:
            for directory, _, files in os.walk(os.path.join(addons, slug)):
                for name in files:
                    path = os.path.join(directory, name)
                    target = os.path.join("addons", slug, os.path.relpath(path, os.path.join(addons, slug)))
                    if name == "plugin.cfg":
                        text = re.sub(r'^version=".*"$', f'version="{version}"', open(path).read(), flags=re.M)
                        archive.writestr(target, text)
                    else:
                        archive.write(path, target)
EOF

# The failed final build the update failure dialog reports.
journal="$samples/my-game/.godot/eplugin/updates/20261011-143205-a1b2c3"
mkdir -p "$journal"
cat > "$journal/final-build.log" <<'EOF'
  Determining projects to restore...
  All projects are up-to-date for restore.
Scripts/Player.cs(42,17): error CS0117: 'SampleLogger' does not contain a definition for 'LogInfo'
Scripts/Hud.cs(18,9): error CS0246: The type or namespace name 'SampleOptions' could not be found
Build FAILED.
    2 Error(s)
EOF

# The driver joins the project for this run only.
cp "$here/DocScreenshots.cs" "$project/DocScreenshots.cs"
printf '\n[autoload]\n\nDocScreenshots="*res://DocScreenshots.cs"\n' >> "$project/project.godot"
dotnet build "$project/EPluginFramework.sln" -nologo -v quiet -clp:ErrorsOnly
# The progress window is a separate helper; publish it like a release does when it is missing.
if [[ ! -f "$project/addons/ePlugin/progress/ActivationProgress.dll" ]]; then
    dotnet publish "$repo/src/activation-progress/ActivationProgress.csproj" -c Release -nologo -v quiet -clp:ErrorsOnly -o "$project/addons/ePlugin/progress"
fi

if [[ "${EPLUGIN_SCREENSHOTS_DARK:-0}" == 1 ]]; then
    color_scheme=$(gsettings get org.gnome.desktop.interface color-scheme)
    gsettings set org.gnome.desktop.interface color-scheme prefer-dark
    # The GTK 3 progress helper does not follow the color scheme by itself.
    export GTK_THEME=Adwaita:dark
fi

# X11 for Godot and the GTK progress helper, so their windows can be captured on Wayland desktops too (through Xwayland).
LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8 GDK_BACKEND=x11 \
XDG_CONFIG_HOME="$work/config" \
EPLUGIN_SCREENSHOTS_OUT="$out" \
EPLUGIN_SCREENSHOTS_CAPTURE="$here/capture.py" \
EPLUGIN_SCREENSHOTS_RELEASES="$samples/releases" \
EPLUGIN_SCREENSHOTS_JOURNAL="$journal" \
    timeout 600 "$GODOT_BIN" --editor --display-driver x11 --path "$project" 2>&1 | grep --line-buffered DocScreenshots || true
[[ -n "${EPLUGIN_SCREENSHOTS_KEEP_RAW:-}" ]] || rm -rf "$out/raw"
