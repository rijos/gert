#!/bin/sh
# Inject the SPA's OIDC config into index.html, then start the API.
#
# The SPA reads window.GERT_AUTH (services/auth.js); nothing sets it server-side,
# so we splice a tiny classic <script> in before </head>. Classic inline scripts
# run during parse, before the deferred app.js module - so window.GERT_AUTH is set
# by the time the SPA boots. Idempotent: we always regenerate from a pristine copy.
set -eu

INDEX=/app/wwwroot/index.html

if [ -n "${GERT_AUTH_AUTHORITY:-}" ] && [ -f "$INDEX" ]; then
  [ -f "$INDEX.orig" ] || cp "$INDEX" "$INDEX.orig"
  CLIENT_ID="${GERT_AUTH_CLIENT_ID:-gert}"
  SNIPPET="<script>window.GERT_AUTH={authority:\"${GERT_AUTH_AUTHORITY}\",clientId:\"${CLIENT_ID}\"};</script>"
  # '#' delimiter: URLs contain '/' and ':' but never '#'.
  sed "s#</head>#${SNIPPET}</head>#" "$INDEX.orig" > "$INDEX"
fi

exec dotnet Gert.Api.dll
