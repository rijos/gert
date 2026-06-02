# Components

```
┌────────────────┐        ┌─────────────────┐
│  VanJS SPA     │  PKCE  │   Pocket ID     │
│  (browser)     │◄──────►│   (OIDC IdP)    │
└──────┬─────────┘        └────────┬────────┘
       │  GET / (SPA bundle)       │ JWKS (public keys)
       │  + Bearer <access JWT>    │
       ▼                           ▼
┌──────────────────────────────────────────────────────────────────────┐
│                  Gert.Api — ASP.NET Core Web API                       │
│  Static files (SPA bundle) · JWT bearer middleware → UserContext       │
│  Controllers · Chat orchestrator · Ingestion worker · Tool adapters    │
└───┬───────────────┬───────────────┬───────────────┬───────────────────┘
    │               │               │               │
    ▼               ▼               ▼               ▼
┌────────┐     ┌──────────┐    ┌──────────┐    ┌──────────────────────┐
│  vLLM  │     │ SearXNG  │    │  gVisor  │    │  /data/users/{key}/   │
│ models │     │  search  │    │  sandbox │    │   chat.db + rag.db    │
└────────┘     └──────────┘    └──────────┘    └──────────────────────┘
```

The SPA bundle and the API are **served by the same ASP.NET Core app on one origin** — the browser fetches the SPA from Gert.Api and then calls `/api/*` on that same origin.

| Component | Role | Talks to it |
|-----------|------|-------------|
| **VanJS SPA** | UI, served as static files by Gert.Api. Holds the access token, calls the API. | Browser → IdP (login), Browser → API |
| **Pocket ID** | OIDC provider. Authenticates users **with passkeys**, issues JWTs, owns the user list. | SPA (login), API (JWKS only) |
| **Gert.Api** | The subject of this document. Stateless REST API **+ static host for the SPA bundle**. | vLLM, SearXNG, gVisor, per-user SQLite |
| **vLLM** | Serves the chat + embedding models (OpenAI-compatible API). | API only |
| **SearXNG** | Self-hosted web search. | API only |
| **gVisor sandbox** | Runs untrusted Python for the code tool. | API only |

Because the SPA and API share one origin, **no CORS configuration is needed** for SPA↔API calls. The only cross-origin traffic is the browser → Pocket ID login/token exchange, which is governed by Pocket ID's own allowed **web origins** config, not by Gert.Api.
