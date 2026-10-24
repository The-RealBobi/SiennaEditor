#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ $# -eq 1 ]] || { echo "Usage: $0 RID (osx-arm64, osx-x64, win-x64 or linux-x64)" >&2; exit 2; }
rid="$1"
case "$rid" in osx-arm64|osx-x64|win-x64|win-arm64|linux-x64|linux-arm64) ;; *) echo "Unsupported runtime: $rid" >&2; exit 2 ;; esac
version="$(sed -nE 's/.*<VersionPrefix>([^<]+)<\/VersionPrefix>.*/\1/p' "$root/Directory.Build.props")"
output="$root/dist/$rid"
dotnet publish "$root/src/SiennaEditor/SiennaEditor.csproj" -c Release -r "$rid" --self-contained true -o "$output" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
archive="$root/dist/Sienna Editor-$version-$rid.zip"
if [[ "$rid" == osx-* ]]; then
  bundle="$output/Sienna Editor.app"
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources/data"
  cp -R "$output/data/." "$bundle/Contents/Resources/data/"
  cp "$root/src/SiennaEditor/Assets/app.icns" "$bundle/Contents/Resources/app.icns"
  mv "$output/Sienna Editor" "$bundle/Contents/MacOS/Sienna Editor"
  cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>Sienna Editor</string>
<key>CFBundleDisplayName</key><string>Sienna Editor</string>
<key>CFBundleExecutable</key><string>Sienna Editor</string>
<key>CFBundleIdentifier</key><string>io.github.the-realbobi.siennaeditor</string>
<key>CFBundleIconFile</key><string>app.icns</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$version</string>
</dict></plist>
EOF
  chmod +x "$bundle/Contents/MacOS/Sienna Editor"
  rm -f "$archive"
  (cd "$output" && zip -q -r "$archive" "Sienna Editor.app")
else
  executable="Sienna Editor"
  [[ "$rid" == win-* ]] && executable="$executable.exe"
  rm -f "$archive"
  (cd "$output" && zip -q -r "$archive" "$executable" data)
fi
echo "Created $archive"
