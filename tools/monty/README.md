# Gert monty sandbox sidecar

A tiny HTTP service that wraps [Pydantic Monty](https://github.com/pydantic/monty) - a
minimal Python interpreter written in Rust, with **no syscalls** (no filesystem, network,
or env access exist in the language) - as Gert's `run_python` sandbox backend.

It speaks the contract the .NET `MontySandbox` adapter
([src/Gert.Tools/Sandbox/Monty/MontySandbox.cs](../../src/Gert.Tools/Sandbox/Monty/MontySandbox.cs))
calls:

```
POST /run
  { "code": "...", "wall_clock_seconds": 10, "memory_mib": 256, "max_output_bytes": 65536 }
->
  { "stdout": "...", "stderr": "...", "exit_code": 0, "timed_out": false }
```

`wall_clock_seconds` / `memory_mib` map onto monty's `ResourceLimits`
(`max_duration_secs` / `max_memory`); a `MontyError` (syntax, runtime, typing, or a
resource-limit trip) becomes `exit_code: 1` with the rendered traceback on `stderr`, so
the model reads the failure and adapts.

## Run it

No container needed:

```sh
cd tools/monty
uv run python app.py            # listens on 127.0.0.1:8077
```

Then point Gert at it (these are already the defaults in `appsettings.json`):

```
Gert:Tools:Sandbox:Type        = Monty
Gert:Tools:Sandbox:Parameters:BaseUrl = http://127.0.0.1:8077
```

`GERT_MONTY_HOST` / `GERT_MONTY_PORT` override the bind address.

## Locking it down (security F5)

Monty is the *capability* boundary - untrusted code can't even express a syscall. This
process is the *OS* boundary wrapped around it: run it as an **unprivileged** user, with
**no mount** of Gert's per-user data root (`/data`) and **egress off**. Even a full monty
escape then lands in a process that can see neither user databases nor the network. Pin
the `pydantic-monty` version - it is experimental (`0.0.x`).

> The automated tests do **not** use this service. The FakeE2E / smoke harness points the
> adapter at a wire-level fake ([tools/smoke/mocks/monty.py](../smoke/mocks/monty.py)) so
> CI needs no Rust/monty install; this real sidecar is validated on a host that has the
> package.
