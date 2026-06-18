# Namespace package marker so `tools.smoke.*` imports resolve when pytest/uv runs
# from the repo root. The .NET projects under tools/ (e.g. Gert.Web.Bundle) are
# unaffected - they are not Python.
