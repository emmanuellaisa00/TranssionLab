#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

VERSION="${1:-1.0.0}"
RID="win-x64"
PKG_NAME="TranssionLab-v${VERSION}-${RID}"
STAGE_DIR="/tmp/transsionlab_publish_${RID}"
BUNDLE_DIR="/tmp/${PKG_NAME}"

echo "==> [1/6] Restoring solution dependencies..."
dotnet restore TranssionLab.sln

echo "==> [2/6] Compiling solution with Roslyn static analysis (-warnaserror)..."
dotnet build TranssionLab.sln --configuration Release --no-restore -warnaserror

echo "==> [3/6] Verifying Windows ARM64 compilation target..."
dotnet build src/TranssionLab.App/TranssionLab.App.csproj --configuration Release -r win-arm64 -warnaserror

echo "==> [4/6] Running automated test suite..."
dotnet test tests/TranssionLab.Tests/TranssionLab.Tests.csproj --configuration Release --filter "Category=SimulatedUnit"

echo "==> [5/6] Publishing self-contained Windows x64 single-file executable..."
rm -rf "$STAGE_DIR" "$BUNDLE_DIR"
dotnet publish src/TranssionLab.App/TranssionLab.App.csproj \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  --output "$STAGE_DIR"

echo "==> [6/6] Assembling release archive & SHA-256 checksum..."
mkdir -p "$BUNDLE_DIR/profiles" "$BUNDLE_DIR/docs" "$BUNDLE_DIR/tools" "$REPO_ROOT/releases"
cp "$STAGE_DIR/TranssionLab.exe" "$BUNDLE_DIR/"
cp -r "$REPO_ROOT/profiles/"*.json "$BUNDLE_DIR/profiles/" 2>/dev/null || true
cp -r "$REPO_ROOT/docs/"*.md "$BUNDLE_DIR/docs/"
cp "$REPO_ROOT/README.md" "$BUNDLE_DIR/"

cat << 'EOF' > "$BUNDLE_DIR/tools/README-TOOLS.txt"
Android SDK Platform-Tools (adb.exe / fastboot.exe) Placement
=============================================================
Place official Apache-2.0 licensed Android SDK Platform-Tools (adb.exe, fastboot.exe,
AdbWinApi.dll, AdbWinUsbApi.dll) in this folder or on your Windows PATH, or configure
their exact location in TranssionLab -> Page #17 (Settings).
Official Google download: https://developer.android.com/tools/releases/platform-tools
EOF

ZIP_PATH="$REPO_ROOT/releases/${PKG_NAME}.zip"
rm -f "$ZIP_PATH" "${ZIP_PATH}.sha256"

python3 - "$BUNDLE_DIR" "$ZIP_PATH" << 'PYEOF'
import os, sys, zipfile
src_dir, zip_path = sys.argv[1], sys.argv[2]
prefix = os.path.basename(src_dir)
with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
    for root, _, files in os.walk(src_dir):
        for f in sorted(files):
            full = os.path.join(root, f)
            rel = os.path.join(prefix, os.path.relpath(full, src_dir))
            zf.write(full, rel)
PYEOF

(cd "$REPO_ROOT/releases" && sha256sum "${PKG_NAME}.zip" > "${PKG_NAME}.zip.sha256")
rm -rf "$STAGE_DIR" "$BUNDLE_DIR"

echo "==> Release packaged successfully:"
ls -lh "$REPO_ROOT/releases/"
cat "${ZIP_PATH}.sha256"
