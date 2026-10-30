#!/usr/bin/env bash
# Builds every release artifact into packaging/out:
#   Cheatmaster-<ver>-linux-x64.tar.gz        self-contained, glibc distributions
#   Cheatmaster-<ver>-linux-musl-x64.tar.gz   self-contained, musl distributions (Alpine)
#   cheatmaster_<ver>_amd64.deb               Debian / Ubuntu
#   Cheatmaster-<ver>-win-x64-selfcontained.zip   one file, nothing to install
#   Cheatmaster-<ver>-win-x64.zip             needs the .NET 10 Desktop Runtime
# The version is <Version> in Directory.Build.props.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)
OUT=${CHEATMASTER_OUT:-packaging/out}
APP=src/Cheatmaster.App/Cheatmaster.App.csproj
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_NOLOGO=1
rm -rf "${OUT:?}" && mkdir -p "$OUT"

# Stale obj/ output can publish XAML that was never compiled: every release starts from clean trees.
for p in src/Cheatmaster.App src/Cheatmaster.Core; do rm -rf "${p:?}/bin" "${p:?}/obj"; done

publish() { # rid dir [extra msbuild args...]
    local rid=$1 dir=$2; shift 2
    echo "── publish $rid"
    dotnet publish "$APP" -c Release -r "$rid" -o "$dir" -v q --nologo "$@"
    rm -f "$dir"/*.pdb
}

check_xaml() {
    if ! grep -qa CompiledAvaloniaXaml "$1"; then
        echo "!! $1 carries no compiled XAML: refusing to package." >&2
        exit 3
    fi
}

linux_payload() { # dir
    cp LICENSE packaging/README.txt "$1/"
    cp src/Cheatmaster.App/Assets/logo.png "$1/cheatmaster.png"
    cp packaging/io.github.codingncaffeine.Cheatmaster.desktop "$1/"
    # The executable is named for the product; a lower-case launcher is what people type.
    ln -sf Cheatmaster "$1/cheatmaster"
}

# ---------------------------------------------------------------- Linux
for rid in linux-x64 linux-musl-x64; do
    dir=$OUT/publish-$rid
    publish "$rid" "$dir" --self-contained true
    check_xaml "$dir/Cheatmaster.dll"
    [[ -x "$dir/Cheatmaster" ]] || { echo "!! Cheatmaster is missing from the $rid publish." >&2; exit 3; }
    linux_payload "$dir"
    tar -C "$dir" -czf "$OUT/Cheatmaster-$VER-$rid.tar.gz" .
done

echo "── deb"
DEB=$OUT/debroot
PUB=$OUT/publish-linux-x64
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/cheatmaster" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/doc/cheatmaster"
cp -a "$PUB/." "$DEB/usr/lib/cheatmaster/"
rm -f "$DEB/usr/lib/cheatmaster/"{README.txt,LICENSE,cheatmaster.png,cheatmaster,io.github.codingncaffeine.Cheatmaster.desktop}
printf '#!/bin/sh\nexec /usr/lib/cheatmaster/Cheatmaster "$@"\n' > "$DEB/usr/bin/cheatmaster"
chmod 755 "$DEB/usr/bin/cheatmaster"
for s in 16 32 48 64 128 256; do
    install -d "$DEB/usr/share/icons/hicolor/${s}x${s}/apps"
    magick src/Cheatmaster.App/Assets/logo.png -resize "${s}x${s}" "$DEB/usr/share/icons/hicolor/${s}x${s}/apps/cheatmaster.png"
done
cp packaging/io.github.codingncaffeine.Cheatmaster.desktop "$DEB/usr/share/applications/"
cp packaging/README.txt "$DEB/usr/share/doc/cheatmaster/README.txt"
cp LICENSE "$DEB/usr/share/doc/cheatmaster/copyright"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: cheatmaster
Version: $VER
Section: games
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libice6, libsm6, libegl1, libgl1, zlib1g
Recommends: pkexec | policykit-1, libsecret-tools
Maintainer: Cheatmaster <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Cheatmaster
Description: Memory scanner that works out how a game stores a number
 Cheatmaster finds the number you can see in a game without you having to know
 how the game stores it: every plausible encoding (integers, floats, scaled and
 percentage values, fixed point, byte-swapped, XOR-keyed) is tested in one pass.
 .
 Found values can be frozen, set in bulk and toggled with hotkeys; pointer
 scanning keeps them working across restarts, and a per-game library brings them
 back automatically. Native Linux games and Wine/Proton games are both supported.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/cheatmaster_${VER}_amd64.deb" > /dev/null
rm -rf "${DEB:?}"

# ---------------------------------------------------------------- Windows
WIN=$OUT/publish-win-x64
publish win-x64 "$WIN" --self-contained false
check_xaml "$WIN/Cheatmaster.dll"
cp LICENSE "$WIN/"
(cd "$WIN" && zip -qr "../Cheatmaster-$VER-win-x64.zip" .)

WINSC=$OUT/publish-win-x64-sc
publish win-x64 "$WINSC" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
cp LICENSE "$WINSC/"
(cd "$WINSC" && zip -qr "../Cheatmaster-$VER-win-x64-selfcontained.zip" .)

# Publishing for a runtime leaves a second, frozen build under bin/Release/net10.0/<rid>; running
# that by mistake looks exactly like a fix that never landed.
for p in src/Cheatmaster.App src/Cheatmaster.Core; do
    for rid in linux-x64 linux-musl-x64 win-x64; do
        rm -rf "${p:?}/bin/Release/net10.0/$rid" "${p:?}/obj/Release/net10.0/$rid"
    done
done

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
