#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$project_dir/../.." && pwd)
export MACOSX_DEPLOYMENT_TARGET=13.0
app_version=${BUAA_NATIVE_VERSION:-0.1.0}
if ! printf '%s\n' "$app_version" | grep -Eq '^(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8})){2}$'; then
    printf '%s\n' 'BUAA_NATIVE_VERSION must be a three-part numeric version.' >&2
    exit 1
fi
if ! command -v cargo >/dev/null 2>&1; then
    case "$(uname -m)" in arm64) rust_arch=aarch64 ;; x86_64) rust_arch=x86_64 ;; *) rust_arch=unknown ;; esac
    rust_bin="$HOME/.rustup/toolchains/stable-${rust_arch}-apple-darwin/bin"
    if [ -x "$rust_bin/cargo" ]; then
        PATH="$rust_bin:$PATH"
        export PATH
    else
        printf '%s\n' 'Cargo not found. Install Rust or add its bin directory to PATH.' >&2
        exit 1
    fi
fi
if [ -n "${BUAA_CARGO_CONFIG:-}" ]; then
    cargo build --release --locked --config "$BUAA_CARGO_CONFIG" --manifest-path "$root/native/core/Cargo.toml"
else
    cargo build --release --locked --manifest-path "$root/native/core/Cargo.toml"
fi
swift build --package-path "$project_dir" -c release
binary_dir=$(swift build --package-path "$project_dir" -c release --show-bin-path)
app="${BUAA_NATIVE_APP_PATH:-$root/dist/native/BUAA Learn.app}"
mkdir -p "$app/Contents/MacOS"
cp "$binary_dir/BUAALoginProbe" "$app/Contents/MacOS/BUAALoginProbe"
mkdir -p "$app/Contents/Resources/icons"
cp "$project_dir/Resources/automation.html" "$project_dir/Resources/automation-bundle.js" "$app/Contents/Resources/"
cp "$project_dir/Resources/timetable.html" "$project_dir/Resources/timetable-bundle.js" "$project_dir/Resources/timetable.css" "$app/Contents/Resources/"
cp "$root/buaa_agent/static/automations.css" "$app/Contents/Resources/"
cp "$root"/buaa_agent/static/icons/*.svg "$app/Contents/Resources/icons/"
cp "$root/buaa_agent/static/fonts/source-serif-4.ttf" "$app/Contents/Resources/"
cp "$root/buaa_agent/static/fonts/source-serif-4-italic.ttf" "$app/Contents/Resources/"
cp "$root/buaa_agent/static/fonts/source-serif-4-LICENSE" "$app/Contents/Resources/"
cp "$root/buaa_agent/static/icons/LICENSE" "$app/Contents/Resources/icons/"
icon_dir=$(mktemp -d "${TMPDIR:-/tmp}/buaa-native-icon.XXXXXX")
trap 'rm -rf "$icon_dir"' EXIT
mkdir "$icon_dir/AppIcon.iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$root/desktop/assets/icon.png" --out "$icon_dir/AppIcon.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$root/desktop/assets/icon.png" --out "$icon_dir/AppIcon.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icon_dir/AppIcon.iconset" -o "$app/Contents/Resources/AppIcon.icns"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>BUAALoginProbe</string>
<key>CFBundleIdentifier</key><string>io.github.fyzzzz05.buaa-login-probe</string>
<key>CFBundleName</key><string>BUAA Learn</string>
<key>CFBundleDisplayName</key><string>BUAA Learn</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$app_version</string>
<key>NSHighResolutionCapable</key><true/>
<key>LSMinimumSystemVersion</key><string>13.0</string>
</dict></plist>
PLIST
codesign --force --deep --sign - "$app"
printf 'Built %s\n' "$app"

if [ -n "${BUAA_NATIVE_DMG_DIR:-}" ]; then
    case "$(uname -m)" in
        arm64) app_arch=arm64 ;;
        x86_64) app_arch=x64 ;;
        *) printf '%s\n' 'Unsupported macOS architecture.' >&2; exit 1 ;;
    esac
    mkdir -p "$BUAA_NATIVE_DMG_DIR"
    mkdir "$icon_dir/dmg"
    ditto "$app" "$icon_dir/dmg/BUAA Learn.app"
    ln -s /Applications "$icon_dir/dmg/Applications"
    hdiutil create -volname 'BUAA Learn' -srcfolder "$icon_dir/dmg" -format UDZO \
        "$BUAA_NATIVE_DMG_DIR/BUAA-Learn-$app_version-mac-native-$app_arch.dmg"
fi
