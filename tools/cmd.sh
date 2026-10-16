#!/bin/sh
# Usage: cmd.sh <GET|POST> <action>[?query]   e.g. cmd.sh POST 'draft?pawn=Human369'
# The game's HttpListener answers 411 to a POST without Content-Length, so POST sends an empty body.
TOKEN=$(cat "$HOME/Library/Application Support/RimWorld/Config/RimEventFeed/token")
[ "$1" = POST ] && BODY="--data-binary @/dev/null" || BODY=""
curl -sS -w '  [HTTP %{http_code}, %{time_total}s]\n' -X "$1" $BODY -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:8798/$2"
