#!/usr/bin/env bash
# Pack / publish MailCal .NET global tools
#
# nuget.org is published from CI via Trusted Publishing (OIDC) — see
# .github/workflows/ci.yml (pack locally; publish workflow optional) and docs/install.md.
# This script does NOT use a long-lived nuget.org API key.
#
# Usage (from repo root):
#   ./scripts/pack-mailcal.sh                 # pack only → ./artifacts/nuget
#   ./scripts/pack-mailcal.sh --push-github   # push existing (or pack then push)
#                                             # nupkgs to GitHub Packages
#   VERSION=0.1.2 ./scripts/pack-mailcal.sh
#
# Env:
#   VERSION          SemVer (default 0.1.2)
#   GITHUB_TOKEN     required for --push-github
#   OWNER            GitHub org/user for Packages (default kern-services)
#   PACK_SKIP=1      skip pack (use existing artifacts/nuget/*.nupkg)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${ROOT}/artifacts/nuget"
VERSION="${VERSION:-0.1.2}"
OWNER="${OWNER:-kern-services}"
GH_SOURCE_URL="https://nuget.pkg.github.com/${OWNER}/index.json"
PUSH_GITHUB=false

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

  echo "Packing MailCal tools version ${VERSION} → ${OUT}"
  dotnet pack "$ROOT/Ironbrain.MailCal.Cli/Ironbrain.MailCal.Cli.csproj" \
    -c Release -o "$OUT" -p:Version="$VERSION" --nologo
  dotnet pack "$ROOT/Ironbrain.MailCal.Mcp/Ironbrain.MailCal.Mcp.csproj" \
    -c Release -o "$OUT" -p:Version="$VERSION" --nologo

  ls -la "$OUT"
}

if [ "${PACK_SKIP:-}" = "1" ]; then
  if ! compgen -G "$OUT"/Ironbrain.MailCal.*.nupkg > /dev/null; then
    echo "PACK_SKIP=1 but no nupkgs in ${OUT}" >&2
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
  echo "Or from nuget.org (after CI Trusted Publishing):"
  echo "  dotnet tool install -g Ironbrain.MailCal.Cli"
  exit 0
fi

if [ -z "${GITHUB_TOKEN:-}" ]; then
  echo "GITHUB_TOKEN is required for --push-github" >&2
  exit 1
fi

echo "Pushing to GitHub Packages (${GH_SOURCE_URL})"
for pkg in "$OUT"/Ironbrain.MailCal.*.nupkg; do
  dotnet nuget push "$pkg" \
    --source "$GH_SOURCE_URL" \
    --api-key "$GITHUB_TOKEN" \
    --skip-duplicate
done

echo "Published to GitHub Packages (secondary). Primary install is nuget.org:"
echo "  dotnet tool install -g Ironbrain.MailCal.Cli"
echo "  dotnet tool install -g Ironbrain.MailCal.Mcp"
