#!/usr/bin/env bash
# Wraps the Pilewise.app from Velopack's portable zip into a drag-to-Applications .dmg, then signs,
# notarizes and staples the .dmg.
#
#   build/macos/make-dmg.sh <Pilewise-osx-*-Portable.zip> <out.dmg>
#
# Environment (all optional; leave them out for an unsigned local test build):
#   SIGN_IDENTITY   codesign identity, e.g. "Developer ID Application: Name (TEAMID)"
#   NOTARY_PROFILE  notarytool keychain profile (xcrun notarytool store-credentials <name> …)
#   KEYCHAIN        keychain holding the identity and the profile (default: the login keychain)
#
# The .app inside the zip is already signed, notarized and stapled by `vpk pack`; notarizing the
# .dmg too lets Gatekeeper open it offline without the "unidentified developer" prompt.

set -euo pipefail

if [ $# -ne 2 ]; then
  echo "usage: $0 <portable.zip> <out.dmg>" >&2
  exit 2
fi

zip=$1
dmg=$2
volname=Pilewise

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

ditto -x -k "$zip" "$stage"
app=$(find "$stage" -maxdepth 1 -name '*.app' -print -quit)
if [ -z "$app" ]; then
  echo "error: no .app inside $zip" >&2
  exit 1
fi

if [ -n "${SIGN_IDENTITY:-}" ]; then
  codesign --verify --deep --strict --verbose=2 "$app"
fi
if [ -n "${NOTARY_PROFILE:-}" ]; then
  xcrun stapler validate "$app"
fi

ln -s /Applications "$stage/Applications"

mkdir -p "$(dirname "$dmg")"
rm -f "$dmg"
# hdiutil sometimes fails with "Resource busy" on CI runners; a retry is usually enough.
for attempt in 1 2 3; do
  if hdiutil create -volname "$volname" -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$dmg"; then
    break
  fi
  if [ "$attempt" -eq 3 ]; then
    echo "error: hdiutil create failed" >&2
    exit 1
  fi
  sleep 5
done

keychain_args=()
if [ -n "${KEYCHAIN:-}" ]; then
  keychain_args=(--keychain "$KEYCHAIN")
fi

if [ -n "${SIGN_IDENTITY:-}" ]; then
  codesign --force --timestamp --sign "$SIGN_IDENTITY" ${keychain_args[@]+"${keychain_args[@]}"} "$dmg"
  codesign --verify --verbose=2 "$dmg"
fi

if [ -n "${NOTARY_PROFILE:-}" ]; then
  result=$(xcrun notarytool submit "$dmg" --keychain-profile "$NOTARY_PROFILE" \
    ${keychain_args[@]+"${keychain_args[@]}"} --wait --output-format json)
  echo "$result"
  status=$(plutil -extract status raw -o - - <<< "$result")
  if [ "$status" != "Accepted" ]; then
    id=$(plutil -extract id raw -o - - <<< "$result")
    xcrun notarytool log "$id" --keychain-profile "$NOTARY_PROFILE" ${keychain_args[@]+"${keychain_args[@]}"} || true
    echo "error: notarization finished with status '$status'" >&2
    exit 1
  fi
  xcrun stapler staple "$dmg"
  xcrun stapler validate "$dmg"
  spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg"
fi

echo "Created $dmg"
