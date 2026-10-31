#!/usr/bin/env bash
# Installs each Linux artifact in packaging/out into a clean container and starts it under Xvfb.
# A system passes when the Cheatmaster window exists and no native library is missing:
#   Debian 12, Debian 13, Ubuntu 22.04, Ubuntu 24.04   the .deb
#   Alpine 3.22                                         the linux-musl-x64 tarball
# Needs podman. Exits non-zero when any system fails; the report is packaging/out/install-test.txt.
set -uo pipefail
cd "$(dirname "$0")/.."

OUT=$(cd "${CHEATMASTER_OUT:-packaging/out}" && pwd)
VER=$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)
REPORT=$OUT/install-test.txt

if ! command -v podman >/dev/null; then
    echo "!! The install test needs podman. Install it, or skip the test with --skip-install-test." >&2
    exit 2
fi
for f in "cheatmaster_${VER}_amd64.deb" "Cheatmaster-${VER}-linux-musl-x64.tar.gz"; do
    [[ -f "$OUT/$f" ]] || { echo "!! $OUT/$f is missing: build the release first." >&2; exit 2; }
done

# Runs inside each container with $APP set; prints WINDOW-OK, or WINDOW-MISSING and the app's log.
SMOKE='mkdir -p /tmp/x && export XDG_DATA_HOME=/tmp/x/d XDG_CONFIG_HOME=/tmp/x/c && Xvfb :5 -screen 0 1400x900x24 >/dev/null 2>&1 & sleep 2; export DISPLAY=:5; ($APP > /tmp/app.log 2>&1 &); sleep 12; if xprop -name Cheatmaster WM_CLASS 2>/dev/null | grep -q Cheatmaster; then echo WINDOW-OK; else tail -20 /tmp/app.log; echo WINDOW-MISSING; fi'

: > "$REPORT"
failed=0
record() { # name output
    local line
    line="$1: $(echo "$2" | tr '\n' ' ')"
    echo "$line" | tee -a "$REPORT"
    # A missing native library can leave the window opening and a feature broken.
    if [[ "$2" != *WINDOW-OK* || "$2" == *MISSING-LIB* ]]; then failed=1; fi
}

for img in debian:12 debian:13 ubuntu:22.04 ubuntu:24.04; do
    echo "── install test: $img"
    r=$(timeout 600 podman run --rm -v "$OUT:/out:ro" "docker.io/library/$img" bash -c "export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq /out/cheatmaster_${VER}_amd64.deb xvfb x11-utils >/tmp/apt.log 2>&1 || { tail -5 /tmp/apt.log; exit 1; }; ldd /usr/lib/cheatmaster/libSkiaSharp.so 2>&1 | grep -E 'not found|Error loading' | sed 's/^/MISSING-LIB /'; APP=cheatmaster; $SMOKE" 2>&1 | tail -6)
    record "$img" "$r"
done

echo "── install test: alpine:3.22 (musl)"
r=$(timeout 600 podman run --rm -v "$OUT:/out:ro" docker.io/library/alpine:3.22 sh -c "apk add -q bash icu-libs fontconfig font-dejavu libx11 libice libsm libxi libxcursor libxrandr libxext libxrender libstdc++ libgcc mesa-egl xvfb xprop >/tmp/apk.log 2>&1 || { tail -5 /tmp/apk.log; exit 1; }; mkdir -p /opt/cm && tar -xzf /out/Cheatmaster-${VER}-linux-musl-x64.tar.gz -C /opt/cm; ldd /opt/cm/libSkiaSharp.so 2>&1 | grep -E 'not found|Error loading' | sed 's/^/MISSING-LIB /'; APP=/opt/cm/cheatmaster bash -c '$SMOKE'" 2>&1 | tail -6)
record "alpine:3.22 (musl tarball)" "$r"

if (( failed )); then
    echo "!! The install test failed on at least one system; see $REPORT." >&2
    exit 1
fi
echo "── install test: every system opened the window."
