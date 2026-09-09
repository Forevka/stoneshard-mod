# Brief: plan the co-op implementation

**You are in plan mode. Produce a plan only — write no code, build nothing, launch nothing.**

## What you are planning

Co-op multiplayer for Stoneshard, implemented inside an existing native mod. The
architecture is already **decided** — do not relitigate it. Your job is to turn it into an
ordered, testable implementation plan.

**Read these first, in order:**

1. `docs/multiplayer-research.md` — 1,100 lines of static analysis. **§9 is the architecture
   decision and is binding.** §1 summarises; §2 (builtin registry), §3 (async receive path),
   §6 (turn structure) and §7 (suggested steps) are the load-bearing technical sections.
2. `src/` — the existing mod. Particularly `symbols.cpp`, `gml.cpp`, `builtins.cpp`,
   `tracer.cpp`.

## The decision, in one paragraph

Host-authoritative, with per-location client leases while players are apart, deterministic
generation throughout, all saves on the host. Together: host simulates the shared location,
client sends input intents and receives entity state. Apart: the client leases and simulates
its own location locally, handing the serialised blob back on transition. The engine holds
**one live location per process**, which is why leases exist at all. Rationale and rejected
alternatives are in §9 — read it rather than re-deriving.

## What already works (do not re-plan these)

| capability | where | notes |
|---|---|---|
| `version.dll` proxy, DX11 overlay, MinHook | `proxy.cpp`, `hooks.cpp`, `overlay.cpp` | loads and runs |
| Script resolver — 34,167 functions by name | `symbols.cpp` | health-checked, no hardcoded addresses |
| YYC script call bridge + RValue handling | `gml.cpp` | ABI proven by live calls |
| **Builtin registry — ~2,533 builtins incl. all 17 `network_*`, 41 `buffer_*`, reflection** | `builtins.cpp` | **just added; needs live verification** |
| Instance reflection (`variable_instance_get/set`) | `builtins.cpp` | used for player x/y |
| On-demand tracer (~9,260 functions, one hook) | `tracer.cpp` | file output |
| Breakpoints (trap-and-report + call stacks) | `tracer.cpp` | |
| Instance inspector | `inspector.cpp` | raw typed memory |
| Headless remote control (`debug-cmd.txt`) | `remote.cpp` | `status`, `trace`, `bp`, `dump`, `call` |
| Item giving (984 objects), gear spawning (721 CSV rows) | `cheats.cpp`, `assets.cpp` | working in game |

**Project rule, non-negotiable:** nothing may be pinned to a hardcoded address. Everything
resolves by name or by consensus pattern at runtime, so a game update cannot break it. Every
existing resolver follows this; your plan must too.

## Known-unknown, and it gates transport

The **socket-type and `network_config_*` constant values are unknown.** YYC folds GML
constant names at compile time, so `network_socket_tcp` etc. do not exist in the binary. Only
the async `type` codes were recovered (data = 3, non-blocking connect = 4).

These must be established **experimentally**. Your plan must say how — the mod already has a
headless `call` command that can invoke a builtin with literal arguments, which is the
obvious lever. Treat "find the constants" as an explicit, early, testable step, not an
assumption.

## Other open risks to plan around

- **The builtin registry is new and unverified in game.** §7 Step 2 proposes a three-call ABI
  check (`buffer_create` → `buffer_get_size` → `buffer_delete`). Plan that as a gate before
  anything depends on it.
- **The async receive hook targets unnamed runner code** (`sub_145348230`). §3.3 / §7.3.1
  give a fully name-anchored chain to find it. Unproven in practice.
- **176 hard singleton dereferences of `o_player`.** Do not spawn a second `o_player`. §4.6.
- **`o_player_observer` is UNVERIFIED** as a remote-avatar candidate — the name is
  suggestive; the code was not read.
- **Quest-flag and shared-stash conflict rules are undesigned** (§9.5).
- The user's own idea was to represent the remote player with an existing friendly NPC
  (guard-like) and sync gear/items onto it. Evaluate that against `o_player_observer` and
  `scr_load_player` (`0x141A4AE70`, the existing character-class instantiation entry point).

## What the plan must contain

1. **Ordered milestones**, each independently testable, each with an explicit pass criterion.
   Prefer milestones that fail fast: transport before replication, one packet before a
   protocol.
2. **A first milestone that proves the riskiest assumption**, not the easiest one.
3. For each milestone: files touched, functions/symbols involved, and how it is verified —
   the mod's headless `debug-cmd.txt` interface and the tracer make in-game verification
   scriptable, so use them.
4. **Explicit fallbacks.** If the async hook cannot be made to work, what replaces it? If
   leases prove too costly, tethered co-op is the accepted fallback (§9.7).
5. **What is deliberately out of scope** for a first playable version.
6. A short section on how to test two instances on one machine.

## Honesty requirements

This project has repeatedly lost time to confident-sounding guesses that turned out wrong
(a heuristic that latched onto a zero-valued field twice; a "gear is unreachable" conclusion
that was really "wrong container"). So:

- Mark every assumption you have not verified against the binary or the running game.
- Where the research says UNVERIFIED, carry that through — do not launder it into fact.
- Prefer "this needs an experiment, here is the experiment" over a plausible guess.

Write the plan to `docs/coop-implementation-plan.md`.
