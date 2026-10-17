#!/usr/bin/env bash
# Idempotently fetch the pinned portable Wine build (Kron4ek) and create the game's Wine prefix.
# Everything lives under .work/ (gitignored). Safe to re-run; also sourced by run-game.sh,
# which relies on the variables/exports below. RW_ROOT = this repo, RIMWORLD_DIR = the game install.
set -euo pipefail

RW_ROOT=${RW_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
WORK=$RW_ROOT/.work
RIMWORLD_DIR=$("$(dirname "${BASH_SOURCE[0]}")/../rimworld-dir.sh") || { echo "no RimWorld install found; set RIMWORLD_DIR" >&2; exit 1; }
export RIMWORLD_DIR

WINE_VERSION=11.0
WINE_BUILD=wine-$WINE_VERSION-amd64-wow64
WINE_SHA256=39574efa1132c3ca0d5c77dd2eddbe4a49cca0d6cc2c290ff4924493a1c40314
WINE_URL=https://github.com/Kron4ek/Wine-Builds/releases/download/$WINE_VERSION/$WINE_BUILD.tar.xz
WINE_BIN=$WORK/wine/$WINE_BUILD/bin

export WINEPREFIX=$WORK/wineprefix
export WINEARCH=win64
export WINEDEBUG=${WINEDEBUG:--all}
# mscoree must stay enabled: Wine (like Windows) loads IL-only .NET DLLs through it, and RimWorld 1.7's runtime
# loads com.rlabrecque.steamworks.net.dll that way (without it: "Module not found", reported by the game as a
# TypeLoadException for Steamworks.Packsize). Only prefix creation disables it, to skip the wine-mono prompt.
export WINEDLLOVERRIDES=${WINEDLLOVERRIDES:-mshtml=}

if [[ ! -x $WINE_BIN/wine ]]; then
  dl=$WORK/dl/wine-$WINE_VERSION
  tarball=$dl/$WINE_BUILD.tar.xz
  mkdir -p "$dl" "$WORK/wine"
  if [[ ! -f $tarball ]]; then
    curl -fL --retry 3 -o "$tarball.part" "$WINE_URL"
    mv "$tarball.part" "$tarball"
  fi
  echo "$WINE_SHA256  $tarball" | sha256sum -c -
  tar -xJf "$tarball" -C "$WORK/wine" --no-same-owner
fi

if [[ ! -f $WINEPREFIX/system.reg ]]; then
  echo "[setup-wine] creating prefix $WINEPREFIX" >&2
  WINEDLLOVERRIDES=mscoree,mshtml= xvfb-run -a -s "-screen 0 1024x768x24 -nolisten tcp" \
    sh -c '"$1/wineboot" -u && "$1/wineserver" -w' _ "$WINE_BIN"
fi
