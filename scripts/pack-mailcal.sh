#!/usr/bin/env bash
# Pack / publish MailCal .NET global tools + shared libraries
#
# nuget.org is published from CI via Trusted Publishing (OIDC) — see
# .github/workflows/publish-mailcal.yml.
# This script does NOT use a long-lived nuget.org API key.
#
# Usage (from repo root):
#   ./scripts/pack-mailcal.sh                 # pack only → ./artifacts/nuget
#   ./scripts/pack-mailcal.sh --push-github   # push existing (or pack then push)
#                                             # nupkgs to GitHub Packages
#   VERSION=0.1.2 LIB_VERSION=0.1.0 ./scripts/pack-mailcal.sh
#
# Env:
#   VERSION          SemVer for Cli/Mcp tools (default 0.1.2)
#   LIB_VERSION      SemVer for Ironbrain.Email / Ironbrain.Calendar (default 0.1.0)
#   GITHUB_TOKEN     required for --push-github
#   OWNER            GitHub org/user for Packages (default kern-services)
#   PACK_SKIP=1      skip pack (use existing artifacts/nuget/*.nupkg)
#   PACK_LIBS=0      skip packing shared libraries

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${ROOT}/artifacts/nuget"
VERSION="${VERSION:-0.1.3}"
LIB_VERSION="${LIB_VERSION:-0.1.1}"
OWNER="${OWNER:-kern-services}"
GH_SOURCE_URL="https://nuget.pkg.github.com/${OWNER}/index.json"
PUSH_GITHUB=false
PACK_LIBS="${PACK_LIBS:-1}"

for arg in "$@"; do
  case "$arg" in
    --push-github) PUSH_GITHUB=true ;;
    --push)
      echo "Use --push-github for GitHub Packages. nuget.org is published from CI via OIDC (Trusted Publishing), not a long-lived API key." >&2
      exit 1
      ;;
    *) echo "Unknown arg: $arg" >&2; exit 1 ;;
  esac
done

do_pack() {
  mkdir -p "$OUT"
  rm -f "$OUT"/Ironbrain.MailCal.*.nupkg
  rm -f "$OUT"/Ironbrain.Email.*.nupkg
  rm -f "$OUT"/Ironbrain.Calendar.*.nupkg

  if [ "$PACK_LIBS" = "1" ]; then
    echo "Packing shared libs version ${LIB_VERSION} → ${OUT}"
    dotnet pack "$ROOT/Ironbrain.Email/Ironbrain.Email.csproj" \
      -c Release -o "$OUT" -p:Version="$LIB_VERSION" --nologo
    dotnet pack "$ROOT/Ironbrain.Calendar/Ironbrain.Calendar.csproj" \
      -c Release -o "$OUT" -p:Version="$LIB_VERSION" --nologo
  fi

  echo "Packing MailCal tools version ${VERSION} → ${OUT}"
  dotnet pack "$ROOT/Ironbrain.MailCal.Cli/Ironbrain.MailCal.Cli.csproj" \
    -c Release -o "$OUT" -p:Version="$VERSION" --nologo
  dotnet pack "$ROOT/Ironbrain.MailCal.Mcp/Ironbrain.MailCal.Mcp.csproj" \
    -c Release -o "$OUT" -p:Version="$VERSION" --nologo

  ls -la "$OUT"
}

if [ "${PACK_SKIP:-}" = "1" ]; then
  if ! compgen -G "$OUT"/Ironbrain.MailCal.*.nupkg > /dev/null; then
    echo "PACK_SKIP=1 but no tool nupkgs in ${OUT}" >&2
    exit 1
  fi
  echo "PACK_SKIP=1 — using existing nupkgs in ${OUT}"
  ls -la "$OUT"
else
  do_pack
fi

if [ "$PUSH_GITHUB" != true ]; then
  echo "Pack complete (no push). Install locally with:"
  echo "  dotnet tool install --global --add-source ${OUT} Ironbrain.MailCal.Cli"
  echo "  dotnet add package Ironbrain.Email --source ${OUT}"
  echo "Or from nuget.org (after CI Trusted Publishing is retargeted to this repo):"
  echo "  dotnet tool install -g Ironbrain.MailCal.Cli"
  exit 0
fi

if [ -z "${GITHUB_TOKEN:-}" ]; then
  echo "GITHUB_TOKEN is required for --push-github" >&2
  exit 1
fi

echo "Pushing to GitHub Packages (${GH_SOURCE_URL})"
for pkg in "$OUT"/Ironbrain.*.nupkg; do
  dotnet nuget push "$pkg" \
    --source "$GH_SOURCE_URL" \
    --api-key "$GITHUB_TOKEN" \
    --skip-duplicate
done

echo "Published to GitHub Packages (secondary). Primary install is nuget.org once Trusted Publishing points at ironbrain-mailcal."
echo "  dotnet tool install -g Ironbrain.MailCal.Cli"
echo "  dotnet tool install -g Ironbrain.MailCal.Mcp"
echo "  dotnet add package Ironbrain.Email"
echo "  dotnet add package Ironbrain.Calendar"
