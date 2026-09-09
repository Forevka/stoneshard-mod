# Co-op implementation plan

Companion to `docs/multiplayer-research.md`. §-references point at that document.
**Architecture is fixed by §9 and is not reopened here.**

Everything below marked **UNVERIFIED** has not been checked against the running game. The
research was a static pass; almost nothing in it has been executed. Treat unmarked claims as
having a stated evidence source in the research doc, and marked ones as experiments waiting to
be run.

## Decisions taken before writing this plan

1. **Bytes move over the mod's own socket, not GameMaker's `network_*`.** The mod is native
   code; it does not need the engine to move bytes. This deletes the two largest technical
   risks the research identified — the unknown `network_socket_*` / `network_config_*`
   constants (§8.2, the "known-unknown that gates transport") and the hook on unnamed runner
   code `sub_145348230` (§3.3). §9 is untouched: it specifies authority, leases and sync tick,
   and says nothing about the transport mechanism. The GM path stays documented as M3's
   fallback.
2. **Players connect by pasting a ticket, not an IP.** iroh, linked into `version.dll` via FFI.
   Built behind a `Transport` interface with plain TCP first, so no game logic ever depends on
   which transport is underneath.
3. **Leases ("apart" mode) are in scope for the first playable**, not deferred behind a
   tethered checkpoint. Tethered co-op remains the §9.7 fallback if leases prove too costly.
4. **The remote-avatar object is chosen by experiment**, not asserted. `o_player_observer` is
   UNVERIFIED in the research (§8.2) — the name is suggestive, the code was never read.
5. **One exe, one DLL**, role chosen at runtime in the overlay (Host / Join).
6. **Both players load their own existing saves before connecting.** The client brings their
   character into the host's world; the host's world is the world.
7. **The client's character is copied in and nothing comes back.** The client's own save is
   untouched and frozen for the session. See §2 — at first-playable scope this choice costs
   almost nothing either way, and becomes real only when inventory replication lands.
8. **On join, the client is moved to the host's location.** Not "wherever they were, lease if
   apart" — that would require the lease machinery to work before anyone could connect at all.

## 0. Shape of the thing being built

Each process runs one unmodified game with the mod in it. Each process has **exactly one
`o_player`** — the human at that keyboard. The *other* player is a non-`o_player` avatar object
in that process. This is what keeps §4.6's **176 hard singleton dereferences**
(`o_player.something`, which silently bind to whichever instance is found first) working
unchanged on both machines.

Authority differs, not structure:

| | host process | client process |
|---|---|---|
| local `o_player` | simulated | position/health written from host packets (together) or simulated locally (apart, under lease) |
| remote player | avatar object, driven by packets | avatar object, driven by packets |
| enemies / world | simulated | mirrored (together) or simulated locally (apart) |
| saves | store of record | suppressed for the session |

Sync tick is the turn boundary, not the frame (§9.4). `gml_Script_scr_allturn`
(`0x1416C1E60`) is a named symbol reachable through the existing `sym::Find` resolver, and
hooking it catches all 109 call sites / 98 distinct callers at once (§6.2).

Project rule carried forward: **no hardcoded addresses**. Every symbol comes from `sym::Find`
(name), `builtins::Find` (name), or a consensus pattern scan. Addresses in this document are
from the research build and appear only as identifiers, never as values to compile in.

## 1. What already exists and is reused

| capability | where | used for |
|---|---|---|
| script resolver, 34,167 names | `src/symbols.cpp` (`sym::Find`, `sym::FindScript`) | hooking `scr_allturn`, `scr_player_move`, `scr_savegame` |
| YYC script caller + `RValue` | `src/gml.cpp` (`gml::Call`, `gml::CallAs`, `gml::SetReal/SetString`) | calling game scripts |
| builtin registry, ~2,533 names | `src/builtins.cpp` (`builtins::Find`, `builtins::Call`) | reflection, `instance_*`, `ds_map_*` |
| reflection read by name | `builtins::GetInstanceVar` | reading/writing player and entity state |
| MinHook detour pattern | `gml::InstallPlayerTracker`, `InstallWeaponRecorder` | every hook in this plan |
| headless command file | `src/remote.cpp` (`debug-cmd.txt` / `debug-reply.txt`) | scripted in-game verification |
| tracer + breakpoints | `src/tracer.cpp` | investigating `scr_load_player`, location scripts |
| static RE tooling | `tools/re/*.py` (`relib`, `pdata`, `assetref`, `datawin`, `who`, `callees`) | offline questions during M2 and M7 |
| two-instance harness | `tools/coop.ps1` — per-instance launch, `Invoke-SsCmd`, save hashing | every milestone from M3 on |
| save reader | `tools/savepeek.py`, `tools/checkcksum.py` | M7a location blob, M9 time, M10 quests/stash, M11 scrub |

**Prior art worth reading before M11:** `MikaBuchholz/stoneshard-editor` (TypeScript + Python)
already decodes, edits and re-signs Stoneshard saves. Its `app/src/codec/save.ts` is where the
checksum salt came from.

`src/gml.cpp:750` already reads player `x`/`y` through `builtins::GetInstanceVar`, so the
builtin path has one live consumer — but the registry is new and its in-game behaviour is
unverified. M1 gates on it explicitly.

## 2. Connection and session flow

The end state the milestones build toward. None of it is implemented yet.

1. Both players launch the game and load **their own existing save**. The mod needs a live
   `o_player` before it can do anything, so this ordering is a convenience rather than a
   restriction: it means the mod never has to drive the main menu or `scr_loadGame`.
2. Host opens the mod's Multiplayer tab and clicks **Host**. The mod starts listening and shows
   a **connection string** with a Copy button.
3. Host sends that string to the other player by any means — Discord, chat, out of band.
4. Client clicks **Join**, pastes the string, connects.
5. Handshake; the client's character is moved into the host's location; play begins.

### The connection string

The mod treats it as opaque — whatever the active transport produces.

| transport | string | reach |
|---|---|---|
| TCP (M3) | `192.168.1.5:27015` | LAN, or WAN with port forwarding. **Unencrypted — trusted networks only.** |
| iroh (M3b) | base32 node ticket | Anywhere. Hole-punched direct where possible, relayed otherwise. No router configuration. |

An iroh ticket encodes a NodeId — an ed25519 public key — so the connection is authenticated
and encrypted, and the ticket *is* the identity; there is nothing to man-in-the-middle. It is
also a **capability**: anyone holding it can connect. The host panel therefore needs a
regenerate button and an accept/deny prompt on the first connection from an unknown NodeId.

**How the string is built.** Verified against `n0-computer/iroh-c-ffi` (`main.c`, read
2026-09-09): the C API exposes the address *components* — `endpoint_addr`,
`public_key_as_base32`, `endpoint_addr_relay_urls_nth`, `endpoint_addr_ip_addrs_nth` — and the
inverse for dialling: `public_key_from_base32`, `url_from_string`, `socket_addr_from_string`,
`endpoint_addr_new`, `endpoint_addr_add_relay_url`, `endpoint_addr_add_ip_addrs`,
`endpoint_connect`.

Whether a single opaque ticket string is *also* exposed to C is **UNVERIFIED** — `iroh-tickets`
is a declared dependency of the bindings, but the shipped example passes the components as
separate arguments rather than one ticket. **This gates nothing:** if no ticket encoder is
reachable from C, the mod defines its own one-line format over those components. Roughly twenty
lines of base64, and the question disappears.

### The join handshake

| direction | carries | milestone |
|---|---|---|
| both | protocol version, build fingerprint (`SizeOfImage` + section sizes, already logged by `sym::Scan`, `symbols.cpp:108`) | M3 |
| host → client | world seed, host's current location id, host's world time | M7 |
| client → host | character representation (below) | M7 |
| host → client | accept, plus the client's spawn position in the host's location | M7 |

Mismatched build fingerprints refuse to connect. §5.1's point that determinism is a
version-matching problem applies directly to §9.1's deterministic generation.

The **world seed** matters because the client is now in the host's world: generation on the
client while leasing must use the host's seed, not its own. **UNVERIFIED** — where a world seed
lives, or whether the game has one at all. M7a settles it.

### What "brings their character" actually means

Full character import means inventory, and inventory is the object-id vs CSV-key split (§4.5) —
the same problem as inventory replication, which §4 lists as out of scope. So at first-playable
scope the client's character is **represented on the host, not transferred**:

- The host receives enough to simulate and render it: position, hp, the stats that affect
  combat resolution, and equipped-gear appearance.
- The client's inventory stays on the client and stays client-authoritative. This is the middle
  design §5.5 already recommends — host-authoritative for world/turn/combat, client owns its
  own UI-local state.

**UNVERIFIED:** the minimum field set that lets the host resolve combat correctly for a
character it did not create. Derive it from `variable_instance_get_names` on a live `o_player`
(§4.1) once M1 passes, cross-checked against what `scr_atr_calc` consumes.

Consequence worth stating plainly: **because nothing transferable moves yet, the choice between
"lent", "checked out" and "copy in" barely matters at this scope.** There is nothing that could
be duplicated. "Copy in, nothing comes back" is chosen because it is cheapest. The decision
becomes real when inventory replication lands — revisit it then, not before.

### The save story

- The **host's** save is the store of record (§9.1) and saves normally. Nothing on the host
  side changes.
- The **client's** save must not be written naively. `scr_savegame` serialises the client
  *process's* current state, and mid-session that state is mostly the host's world: an
  `o_player` at host-world coordinates, a host-world location id, the host's world time. Write
  that into the client's own character file and the character later loads solo into a location
  its own world may never have generated. **UNVERIFIED** what the game does then; none of the
  possibilities are good.
- **First-playable default: suppress it.** Hook `gml_Script_scr_savegame` on the client and
  skip the original for the whole session, so the client's save is byte-identical before and
  after — which §5 checksums every session. It is **UNVERIFIED** whether a room transition
  survives without it (M0); if it does not, redirect the client's save directory instead.
- **The client's character therefore gains nothing across a session.** That is a real cost, not
  a design preference. **M11 removes it** by replacing the suppression with a scrubbed save —
  stash the host-world fields, write back the pre-join ones recorded at the M7 handshake, call
  the original, restore. The character persists as "in your own world, at the spot you left,
  with the progress it earned". That milestone is deliberately sequenced after the first
  playable because the scrub's cost is unknown until `scr_savegame` is actually read.

## 3. Milestones

Ordered so the riskiest structural assumption is proven before anything is built on it, and so
nothing that needs two machines is attempted before one machine works.

---

### M0 — Two game instances on one machine

**Why first:** everything after M3 is untestable without it, and it is cheap. It is not the
riskiest milestone; it is a hard prerequisite that fails in minutes if it fails.

Three things currently collide between two processes:

- `MOD_DATA_DIR` is a compile-time constant (`CMakeLists.txt:86`) pointing at the repo. Both
  instances would share `mod.log`, `imgui.ini`, `debug-cmd.txt` and `debug-reply.txt` — and
  `remote::Poll` **deletes** the command file after reading it (`src/remote.cpp:164`), so
  whichever instance polls first eats the other's command.
- Both write `%LOCALAPPDATA%\StoneShard\characters_v1` (`src/savebackup.cpp:19`).
  `scr_savegame` (`0x14104C210`) calls `scr_slotUpdate`, which reads as a shared slot index —
  so concurrent writes are likely to conflict. **UNVERIFIED.**
- Whether the runner enforces a single-instance mutex is **UNVERIFIED**.

**Work**

- New `src/paths.{h,cpp}`: `mod::paths::DataDir()` returns `%SSMOD_DATA_DIR%` when set, else
  `MOD_DATA_DIR`. Route `log.cpp`, `overlay.cpp` (imgui.ini), `remote.cpp` (both files) and
  `savebackup.cpp` (backup destination) through it. Create the directory if missing.
- Overlay title bar shows the data dir and the pid, so two windows are tellable apart.

**Experiments**

1. Launch `StoneShard.exe` twice from the same folder. If the second refuses, copy the game
   folder and retry; if it still refuses, find the mutex (Process Explorer handle search, or a
   temporary `CreateMutexW` IAT hook in the mod) and decide whether to neutralise it.
2. With both running, load a *different* character in each, walk through a door in each (this
   is what triggers `scr_savegame`, via `o_smoothRoomChanger_Other_15/16/17`, §4.4), quit both,
   and check both characters still load. **Keep copies of the save files either side of each
   transition** — M11 diffs them to work out which bytes the location and position fields own,
   and collecting them costs nothing while the harness is already up.

**If experiment 2 fails**, in order of preference:

1. Run the second instance under a second Windows account (`runas`) — real separate
   `%LOCALAPPDATA%`, no game code touched.
2. Hook `gml_Script_scr_savegame` in the client role and skip the original. Precise and
   available (named symbol, same MinHook pattern as `InstallWeaponRecorder`), but
   **UNVERIFIED** whether the room transition survives without it — `scr_savegame` also calls
   `saveSelfBuffs`, `scr_save_item`, `scr_globalmapFogSave`, `scr_globalmapPaperSave`, and the
   transition may depend on them. **This hook is needed by §2's save story regardless**, so
   proving it here pays for itself twice.
3. Test whether the runner resolves its save directory via the `LOCALAPPDATA` environment
   variable rather than `SHGetKnownFolderPath`. **UNVERIFIED** — the research never traced the
   save-path resolution.

**Pass criterion:** two StoneShard processes running simultaneously, each with the overlay up,
each writing its own `mod.log`, each responding independently to a `status` command written to
its own `debug-cmd.txt`, and each able to save and reload its own character without damaging
the other's.

**Files:** new `src/paths.{h,cpp}`; edits to `log.cpp`, `overlay.cpp`, `remote.cpp`,
`savebackup.cpp`, `CMakeLists.txt`.

---

### M1 — Builtin registry proven live, and callable headlessly

**Why:** the registry (`src/builtins.cpp`) was added but never run in game. Reflection is the
foundation of §9.4's whole replication design — "write received values onto local instances by
name" — so if it is wrong, everything after M2 is wrong. §7 Step 2's three-call ABI check is
the gate.

Note: the mod does **not** currently have a headless command that can invoke a builtin.
`remote.cpp`'s `call` routes to `console::Execute`, which resolves names only through
`sym::Find` (the script table); builtins are not in that table (§2.1). A separate command is
needed before any builtin experiment can be run headlessly.

**Work**

- `builtins::SelfTest()` — must run on the game thread, from the Present hook, beside
  `gml::AbiSelfTest()` (`overlay.cpp:379`):
  - `buffer_create(64, 1, 1)` → real, positive
  - `buffer_get_size(id)` → exactly `64`
  - `buffer_delete(id)`
  - `random_get_seed()` → a real
  - `variable_instance_get(player, "x")` agrees with `gml::PlayerPosition`
- New remote command `callb <builtin> [args...]`, mirroring `console::Execute`'s tokenising and
  number/string coercion but dispatching through `builtins::Call`. Prints `kind=N value` to
  `debug-reply.txt`.
- New remote commands `getvar <name>` / `setvar <name> <value>` operating on the current player
  instance via reflection — used constantly in later milestones for verification.
- `names` — dump `variable_instance_get_names(player)` to the reply file. This is also §5.5's
  missing bandwidth estimate and §2's character-field question; take both measurements here.
- Overlay: surface `builtins::Status()` and the self-test result.

**Known soft spot:** `builtins::GetInstanceVar` (`src/builtins.cpp:266`) passes `-1.0` as the
instance argument and relies on the `self` parameter for context. Whether GML's `-1` ("self")
resolves correctly when `self` is supplied explicitly to the `TRoutine` is **UNVERIFIED**. If
reads fail, pass the real numeric instance id instead; recover it with
`instance_find(<object index>, 0)` or by reading `id` off the instance.

**Fallback if the array walk fails** (§2.7): per-name backward decode — from the
`lea rcx,[rip+d32]` site that loads the builtin's name string, walk back ~0x20 bytes to the
preceding `lea rdx,[rip+d32]` (`48 8D 15`); that operand is the `TRoutine`. Verified statically
against all 17 `network_*` names; **UNVERIFIED** for other names. Needs no globals, but costs
one scan per name.

**Pass criterion:** `debug-reply.txt` shows `buffer_get_size` returning exactly `64`; the log
shows the registry resolving a count in 2,400–2,700 with arity checks passed; `getvar x`
matches the character's on-screen position; `names` returns a plausible field list; no crash
across five minutes of normal play with the registry initialised.

**Files:** `src/builtins.{h,cpp}`, `src/remote.cpp`, `src/overlay.cpp`.

---

### M2 — Remote avatar: a second visible, mod-driven character in ONE process

**This is the riskiest-assumption milestone.** It needs no networking at all, which is the
point: if a second player cannot be represented without breaking the game, co-op does not
exist, and that should be discovered before a single byte is sent.

Two things are being proved at once:

1. A non-`o_player` object can stand in for a player — visible, positioned, animated.
2. `variable_instance_set` actually *writes* live state the game respects, not just reads it.
   (§4.1 lists it as registered with arity 3; that it works as a live mutator is
   **UNVERIFIED**.)

**M2a — investigation (offline + in-game, no code committed yet)**

- Read `o_player_observer` (object index 1796; events `Create_0`, `Step_0`, `Draw_64`,
  `CleanUp_0`; referenced by exactly one function). Use `tools/re/callees.py`, `strings_in.py`
  and `who.py` on `gml_Object_o_player_observer_*`. **The research never read this code** — the
  name is the only evidence there is.
- Read `gml_Script_scr_load_player` (`0x141A4AE70`, `0x444A` bytes), the existing
  character-class instantiation entry point, called from the `Create_0` of the nine playable
  classes (§4.4). Determine what it needs and whether it can run against a non-`o_player`
  instance.
- Survey friendly-NPC candidates: use `tools/re/datawin.py`'s `objt_indexed.json` to find
  guard/companion objects, and `assetref.py` to check how heavily each is singleton-referenced.
  A candidate that already has movement, sprite direction, equipment rendering and a health bar
  is worth a lot.
- Also read `gml_Script_scr_unitTurnNext` (`0x141699BE0`, only `0x4D7` bytes) — §8.2's open
  question, whether unit turn order is a structure that accepts an extra participant. The
  answer decides whether M6 inserts the avatar into the existing order or bolts arbitration on
  outside.

**M2b — spawn**

Via builtins: `instance_create_depth(x, y, depth, object_index)` (`0x1451F21E0`, arity 4).
Object index obtained by name at runtime — do not compile in 1796. Add remote commands
`spawnavatar <object_name>` and `killavatar`.

**M2c — drive it**

Each frame (or each turn), write `x`, `y`, `image_index`/direction, `hp` onto the avatar with
`variable_instance_set`. Confirm the sprite moves and faces correctly.

**Ranking to expect, stated in advance so the experiment can refute it:** the friendly-NPC route
is the most likely to give a working avatar cheaply, because such an object already has the
movement/animation/equipment machinery. `o_player_observer` is the cleanest *if* it is what its
name suggests, and is worth 30 minutes to check first because it is referenced by only one
function and therefore has near-zero blast radius. `scr_load_player` is the heaviest and is
studied for reference, not called — it is wired to the class `Create_0` events, and driving it
risks producing something too close to a real player.

**Pass criterion:** an avatar visible beside the player, moved to arbitrary tiles by mod-driven
reflection writes only, for sixty seconds, with:

- `instance_number(<o_player index>)` still exactly `1`,
- normal play unaffected — take a turn, open inventory, attack something, transition a room,
- and no crash. The avatar surviving a room transition is *not* required at this stage.

**Files:** new `src/avatar.{h,cpp}`; `src/remote.cpp`; `src/overlay.cpp`.

---

### M3 — Transport: a message between two processes

Plain TCP behind a `Transport` interface. No GameMaker networking, no unknown constants, no
runner hooks, no Rust yet.

The UI built here is the **final** UI: Host shows a connection string with a Copy button, Join
takes a pasted string. That is true whether the string is `192.168.1.5:27015` or an iroh
ticket, so M3b changes nothing above the transport layer.

**Work**

- New `src/transport.h` — the interface every transport implements: `StartHost`,
  `Join(string)`, `ConnectionString()`, `Send(frame)`, `PollReceived()`, `State()`, `Close()`.
- New `src/net_tcp.cpp` — TCP implementation. Host listens; client connects. Two players only.
- Framing, defined once and reused by every transport: 4-byte length prefix + 2-byte message
  type + payload. Little-endian, explicit.
- A dedicated worker thread does `accept`/`recv`/`send`. Received frames go into a
  mutex-guarded queue; the queue is drained on the **game thread** from the Present hook,
  immediately next to `remote::Poll()` (`overlay.cpp:373`) — the same discipline the mod
  already uses for the one thread where calling into GML is safe.
- Outbound sends are queued from the game thread and flushed by the worker, so no game-thread
  call ever blocks on the socket. `Present` must never block: this is the same hazard
  `tracer.h` already documents for breakpoints.
- Handshake: protocol version + build fingerprint (§2). Mismatched builds refuse to connect.
- Clean teardown on disconnect, on `DLL_PROCESS_DETACH`, and on host quit.

**New `src/protocol.h`** — message types, all defined up front even if unimplemented:
`Hello`, `Welcome`, `Ping`/`Pong`, `JoinRequest`, `JoinAccept`, `Intent`, `EntityState`,
`WorldState`, `TurnAdvance`, `LeaseRequest`, `LeaseGrant`, `LocationBlob`, `Bye`.

**New overlay tab "Multiplayer":** role (Host / Join), connection string with Copy
(`ImGui::SetClipboardText`), paste field, connection state, peer build fingerprint, RTT,
bytes/s in and out, last error. Plus remote commands `host`, `join <string>`, `netstat` so it
is scriptable headlessly.

**Pass criterion:** two instances from M0; one hosts, one joins by pasted string; `Ping`/`Pong`
round-trips and the overlay shows a plausible RTT; `netstat` in each `debug-cmd.txt` reports
connected; killing either process makes the other report a clean disconnect within a second
with no hang in `Present` and no crash.

**Fallback** (only if native sockets turn out to be blocked by something unforeseen): the GM
path from §7 Steps 3–4 — discover socket constants by probing `network_create_socket(type)` for
`type` in 0..5 with the new `callb` command, then resolve and hook the async data builder
`sub_145348230` via §7.3.1's chain, anchored on the `message_type` string (it is the only
network function using it) confirmed against `SocketMutex`, using a runtime `.pdata` walk for
the enclosing function bounds. That fallback needs a `.pdata` reader the mod does not have.

**Files:** new `src/transport.h`, `src/net_tcp.cpp`, `src/protocol.h`; `src/overlay.cpp`,
`src/remote.cpp`, `CMakeLists.txt` (add sources, link `ws2_32`).

---

### M3b — iroh: connect by ticket, no port forwarding

Linked into `version.dll` via FFI. Touches no game code, so it is independent of M4–M10 and can
be scheduled whenever convenient — but it must exist before anyone plays over the internet.

**The binding to use is `n0-computer/iroh-c-ffi`** — a purpose-built C FFI, distinct from the
uniffi-based `iroh-ffi` that targets Python / Swift / Kotlin. Facts below were read from the
repository on 2026-09-09; re-check them before starting, since they are a snapshot.

| | value |
|---|---|
| `crate-type` | `["staticlib", "cdylib", "lib"]` — staticlib is what this project needs |
| iroh pin | `iroh = "1.0.0"`, `iroh-tickets = "1.0.0"` — the stable line, not a pre-1.0 pin |
| binding tool | `safer-ffi = "0.1.13"` with `async-fn`; header via `cargo run --features headers --bin generate_headers` |
| tokio | `1.45.1`, `rt-multi-thread` |
| status | `pushed_at 2026-06-25`, not archived, 1 open issue, 26 stars |

**The API surface covers everything needed.** From the shipped `main.c`:
`endpoint_config_default` / `endpoint_config_add_alpn` / `endpoint_default` / `endpoint_bind` /
`endpoint_online` for setup; `endpoint_accept` and `endpoint_connect` for both directions;
`connection_open_bi` / `connection_accept_bi` with `send_stream_write_timeout`,
`recv_stream_read_timeout` and `send_stream_finish` for framed traffic — the timeout variants
matter, because nothing on the game thread may block indefinitely. `connection_rtt` and
`connection_packet_loss` feed the Multiplayer tab's stats panel for free.

**What the spike still has to answer — it is now Windows, and only Windows:**

1. **Does it build and link on MSVC against `/MT`?** `CMakeLists.txt:10` sets `MultiThreaded`,
   so the Rust side needs `-C target-feature=+crt-static`. The repo's own example links with
   `-lSystem -lc -lm`, which is macOS; nothing in it documents Windows. **UNVERIFIED.** A CRT
   mismatch gives duplicate-symbol link errors at best and cross-heap corruption at worst.
2. **Does it survive living inside the game process?** iroh runs a multi-threaded tokio
   runtime, and the C example has no explicit runtime init — it is started internally, so the
   mod has less control over its lifetime than it would like. Bind from the mod's existing init
   thread, never from the game thread and never from `DllMain`, and call `endpoint_close`
   before `RemoveHooks` (`dllmain.cpp:73`).
3. **Wrap every FFI entry point in `catch_unwind`.** A Rust panic unwinding across the FFI
   boundary aborts the process — which here means the player's game closes without warning.
   `safer-ffi` may already guard this; confirm rather than assume.

**Work**

- `rust/` crate wrapping `iroh-c-ffi` and producing a staticlib; cargo driven from CMake
  (corrosion-rs, or a custom command, with a checked-in prebuilt `.lib` as the low-tech
  option).
- `src/net_iroh.cpp` implementing the same `Transport` interface as `net_tcp.cpp`. ALPN
  identifier `stoneshard-coop/0`. QUIC gives a reliable ordered stream, so M3's framing is
  reused unchanged.
- Connection string encode/decode over the address components (§2), or iroh's own ticket type
  if the C surface exposes one.
- Host panel: string, Copy, regenerate; accept/deny prompt on the first connection from an
  unknown public key.
- Transport selector so LAN play can still use plain TCP without involving a relay.

**Fallback, and it is cheap:** if the spike fails or runs long, run iroh in a **sidecar
process** — an ordinary Rust exe using the normal `iroh` crate, with the mod talking to it over
localhost. That removes `iroh-ffi`, the CRT question and the in-process tokio runtime
completely, and it costs very little *precisely because M3 already built the localhost TCP
transport*. The mod spawns it, kills it with the game, and displays whatever ticket it prints.
Price: a second file to ship.

**Dependency accepted:** iroh's hole punching falls back to relays operated by n0. That puts a
third-party service in the connection path. Availability, terms of use for a game mod, and what
a relay can observe (payloads are encrypted; endpoint IPs and timing are not) are
**UNVERIFIED** and worth reading before shipping.

**Pass criterion:** host and client on two *different* networks — not the same LAN — connect
from a pasted ticket alone with no router configuration on either side, and pass the same
`Ping`/`Pong` and clean-disconnect checks as M3.

**Files:** new `rust/`, `src/net_iroh.cpp`; `CMakeLists.txt`, `src/overlay.cpp`.

---

### M4 — First replicated state: host player position, host → client

Deliberately does **not** involve locations. Both players stay in whatever location their own
save put them in; the avatar appears at the host's coordinates inside the client's own location.
Visually nonsense, which is fine — this milestone proves the pipe, not the geography.

**Work**

- New `src/session.{h,cpp}`: role, peer state, and the per-turn replication step.
- Hook `gml_Script_scr_allturn` via `sym::FindScript("scr_allturn")` + MinHook, same pattern as
  `gml::InstallWeaponRecorder` (`gml.cpp:789`). This is the sync tick (§9.4). At this milestone
  the hook only *observes* — it always calls the original.
- On the host, per turn: read local `o_player` `x`, `y`, `hp`, facing by reflection; send one
  `EntityState`.
- On the client: apply to the M2 avatar by reflection.

**Pass criterion:** the host walks; within one turn `getvar x` / `getvar y` on the host and the
avatar's coordinates read back on the client match exactly. Sustained over 50 turns with no
drift and no crash.

**Files:** new `src/session.{h,cpp}`; `src/avatar.cpp`, `src/net_tcp.cpp`.

---

### M5 — Client intents, host → client authority over the client's own character

Under §9.4 the client sends *input intents*, never positions, and the host derives position. So
on the client, the local `o_player` must stop moving itself and start being written by the host.

**Work**

- Client: hook `gml_Script_scr_player_move` (`0x141805180`, 12 sites / 9 distinct callers) and
  `gml_Script_scr_attack` (`0x14129BB90`); convert each into an `Intent` message and skip the
  original.
- Host: on receiving an `Intent`, apply it to the client's avatar — which on the host is the
  thing that acts — and let the world resolve it.
- Host → client: the resulting authoritative position for the client's character, applied to
  the client's own `o_player` by reflection.

**Biggest risk here, stated plainly:** whether suppressing `scr_player_move` / `scr_attack` on
the client leaves the client's UI, animation and turn state consistent is **UNVERIFIED**. Both
are large scripts (`0x35FA` and `0x5BCD`) with many callers, and they may do local presentation
work that the client still needs.

**Fallback if suppression breaks the client:** let the client run the script locally for
presentation and *correct* it from the host's authoritative reply — position snapping rather
than input prevention. Uglier, visibly rubber-bandy, but does not require understanding what
those scripts do internally.

**Pass criterion:** the client presses a direction key; the host's simulation moves that
character; the corrected position arrives back and both screens agree within one turn. Ten
consecutive moves without divergence.

---

### M6 — Turn arbitration: one turn per round, not two

§6.3 is the hardest constraint in the whole design: `scr_global_turn` (`0x1416C2A60`, 12.5 KB)
applies hunger, thirst, pain, intoxication, psyche, fatigue, bleeding and regeneration **once
per turn, globally**. Two players each advancing their own turn ticks every survival stat twice
per round.

**Work**

- Promote the M4 `scr_allturn` hook to a gate. Host: let it through, then broadcast
  `TurnAdvance`. Client (together): suppress the local call; advance only on `TurnAdvance`.
- If M2a found that unit turn order accepts an extra participant (`scr_unitTurnNext`), prefer
  inserting the remote player into the existing order as a time-cost participant, the way NPCs
  are (§6.5) — structurally cleaner than external arbitration.

**Fallback if suppressing `scr_allturn` breaks the client** (likely enough to plan for — it is
reached from 98 distinct callers and much of what it does may be local presentation): let the
client run `scr_allturn` normally and gate `gml_Script_scr_global_turn` instead. That is the
function that actually double-applies, and it has only 12 call sites / 12 distinct callers, so
it is a much narrower thing to suppress.

**Pass criterion, measured not eyeballed:** read hunger, thirst, fatigue on both characters via
`getvar`; take exactly 20 turns of shared play; read again. The decrements must correspond to
20 turns, not 40. Also confirm the in-game clock advanced once per turn.

---

### M7 — Co-location: the client enters the host's location

**This is what "join" actually means**, and it is a larger step than it sounds. Moving the
client to the host is not a position write: the client's process has a *different* location
loaded, and a GameMaker process holds exactly one live location (§9.2). To stand where the host
stands, the client's game must instantiate the host's location.

**Correction to this plan's earlier ordering:** entity replication was scheduled before the
location machinery. It cannot be — two players cannot share a location until the client can
enter the host's location, and that needs the same machinery leases need. The investigation
previously filed under the lease milestone moves here, because it now gates the first genuinely
playable moment rather than only the "apart" mode.

**M7a — investigation**

- Read `scr_locationRoomEntityMobsSaveDataGet`/`Set`, the container equivalents
  (`scr_locationRoomEntityContainersInstanceCreate`/`InstanceDestroy`), and
  `scr_locationRoomDelete`. Establish (i) how the game is told "load location X" and whether
  that entry point can be driven from the mod, and (ii) where the serialised location blob
  lives — a global `ds_map`, an array, a file. Use the tracer with a breakpoint on
  `o_smoothRoomChanger_Other_15/16/17` to catch a real transition in flight.
- Determine whether generation is **deterministic from a stored seed**. §9.1 assumes it is; the
  research never checked. `random_set_seed` and `randomize` are both registered (§2.6), so the
  game may re-randomise per entry. Test by generating the same location twice from the same
  save and comparing. **If it is not deterministically seeded, §9.1's "this needs no protocol"
  claim fails** — the host must ship the generated blob instead. More bandwidth, not a design
  change, but it must be known before M9.
- Find where the **world seed** lives, if one exists. §2's handshake needs it.

**M7b — the join sequence**

1. Client sends `JoinRequest` with its character representation (§2).
2. Host replies `JoinAccept` with world seed, its current location id, world time, and a spawn
   position.
3. Client suppresses its own `scr_savegame` for the rest of the session (§2), adopts the host's
   world time, and transitions to the host's location id — generating it from the host's seed,
   or applying a blob the host sends, whichever M7a established.
4. Client's `o_player` is placed at the spawn position. Both avatars spawn. Play begins.

**Pass criterion:** two players who loaded unrelated saves end up standing next to each other in
the same location, each seeing the other's avatar at the correct tile, and each able to walk
around it. The client's own save file is byte-identical before and after the session.

**Fallback:** if driving a location transition from the mod proves intractable, restrict the
first playable to a **fixed rendezvous location** — both players manually walk to the same named
location (a town) before connecting, and the mod only verifies the ids match rather than forcing
the move. Worse UX, but it unblocks M8 and M9 and is a two-line check instead of a milestone.

---

### M8 — Shared-location entity replication

**Work**

- Host, per turn: enumerate live entities in the current location and send a compact
  `EntityState` list — `{stable id, object index, x, y, hp, state flags}`. Enumeration through
  `instance_find` / `instance_number` (both registered, §4.1) over the relevant object indices.
  **UNVERIFIED:** which object index or parent covers "all enemies" — `o_enemy` exists (§5.2)
  but whether it is the parent of every hostile was never established. Determine in-game with
  the inspector and `instance_number`.
- Client: create, update and destroy mirror instances to match. Mirrors are non-authoritative
  presentation objects; they never run AI.
- Per §4.5, anything item-shaped must move as **(object index, CSV key, per-instance
  modifiers)**, never as an instance id — a spawned item on the client has a different id.

**Pass criterion:** the host fights an enemy; the client sees it at the right tile with the right
HP each turn, and sees it die. Ten enemies simultaneously without frame-time regression a player
would notice.

---

### M9 — Leases: separated locations

§9.2's reason leases exist: a GameMaker process holds exactly one live location. The host cannot
simulate two dungeons at once, so a separated client must simulate its own.

M7a already answered the hard questions — where the blob lives, how a transition is driven,
whether generation is seeded. This milestone is the protocol on top of them.

**Work**

- `LeaseRequest` when the client transitions to a location the host is not in; `LeaseGrant`
  carrying either that location's stored blob or "never generated, generate it from the seed".
- `LocationBlob` back to the host on handback (§9.5). The host stores it and does not validate
  it (§9.6 accepts this).
- **Merge:** the arriving client discards its local state for that location and adopts the
  host's live state wholesale (§9.5). Host state always wins.
- **World time** on merge becomes `max` of the two clocks (§9.5). Reached through
  `scr_timeUpdate` / `scr_characterTimeUpdate`, or by writing the time globals through
  `variable_global_set` — **UNVERIFIED** which is safe.

**Pass criterion:** player A stays in town; player B enters a dungeon, kills a mob, returns to
town (handback); A then enters that dungeon and the mob is still dead. Then a merge: B walks
into A's location and adopts A's state without a desync. Then a clock check: the idle player's
world time jumps forward to match, once, not repeatedly.

**Fallback:** §9.7's accepted one — tethered co-op. Both players always in the same location;
the client cannot leave without disconnecting. Removes separated dungeons, which is a stated
requirement, but it is a real shipping option if leases prove too costly.

---

### M10 — Quest flags and shared stash

§9.5 lists these as **open — not yet designed**. Designing them is part of this milestone, not a
prerequisite.

Proposed starting rules, to be validated here and revised if the game fights them:

- **Quest flags:** host-authoritative always. A leased client records flag changes as an ordered
  journal and replays them onto the host at handback. Conflicts resolve host-first, and the
  client is told what was rejected.
- **Shared stash:** locked to whichever player holds the lease on the location the stash is in.
  A player without the lease sees it read-only. Crude, but it makes the failure mode "you can't
  touch it right now" rather than "your items vanished".

**Pass criterion:** a quest advanced by the leased client is reflected on the host after
handback; two players cannot remove the same stash item; no item duplicates across a split/merge
cycle.

---

### M11 — Client character progression: scrub, save, restore

Removes the cost §2 accepts — that the client's character gains nothing across a session.
Independent of M8–M10, and it can be pulled forward to any point after M7 once the save-file
diffs collected in M0 say the scrub is tractable.

**The trigger is already there.** It is *not* a message from the host: the client calls
`scr_savegame` on its own room transitions, and that is precisely the call M7 suppresses. So the
client's existing hook becomes the mechanism — instead of skipping the original, scrub around
it:

```
on client scr_savegame:
    stash the host-world values (location id, x, y, world time)
    write back the pre-join values recorded at M7b's handshake     <- reflection, by name
    call the original scr_savegame
    restore the host-world values
```

The character then persists as *"in your own world, at the spot you left, with the progress it
earned"* — a coherent thing to hand a player. The pre-join values cost nothing to keep, because
the join sequence already has to know what it is overwriting.

A host-side hook on `scr_savegame` broadcasting `SaveNow` is still worth adding, but only as a
convenience: it lets the host force a client checkpoint at a moment the client is not itself
transitioning. It is not the mechanism.

**Why this beats a mod-side blob** (the alternative: host writes the client's character
representation to its own file keyed by peer identity). `scr_savegame` is the *game's*
serialiser, so it captures whatever the character actually has — inventory included — with no
enumeration on our part. A blob only ever persists the fields someone remembered to list, and
would need extending the moment inventory replication lands. This approach is already correct
then.

**The real work is the scrub, and its cost is unknown.** `scr_savegame` is 12,073 bytes and
calls `saveSelfBuffs`, `scr_save_item`, `scr_globalmapFogSave`, `scr_globalmapPaperSave` and
`scr_slotUpdate` (§4.4). Which of those read world-entangled state, and whether reflection
reaches all of it by name, is **UNVERIFIED**. Size it with a differential experiment before
committing: save, change one field with `setvar`, save again, diff the two files. That says
exactly which bytes each field owns. The save copies M0 collects are the first input.

**Open sub-question, cheap either way:** the fog and paper saves would write the *host's* global
map exploration into the client's file. That is a spoiler, not corruption. Decide whether to
scrub it or accept it — accepting is one less thing to get right.

**This reopens duplication, later.** At first-playable scope nothing transferable moves (§2), so
a client-side save cannot duplicate anything. Once inventory replication lands, client-side save
plus client-side inventory is the classic dupe: join, receive an item, save, disconnect, rejoin,
receive another. The fix then is a session lock on the client's save or a host-side ledger — the
same decision §2 already defers. Named here so it is not a surprise.

**Pass criterion:** the client joins at level 5, earns a level in the host's world, disconnects
cleanly, then loads their own save solo. The character is level 6, standing where it stood
before joining, in its own world, with its own quest state and its own location intact. Repeat
across three sessions with no accumulating drift.

---

## 4. Explicitly out of scope for the first playable

- More than two players.
- Host migration, reconnect, or resume after a host crash. Host disconnect ends the session
  (§9.6).
- Validating client-supplied location blobs. The host trusts them (§9.6). No anti-cheat.
- Matchmaking, lobbies, server browsers, friend lists. Connection is by pasted ticket, shared
  out of band.
- **Full character transfer.** The client's character is represented on the host, not moved
  (§2). Inventory stays client-side.
- **Client character progression.** Nothing comes back at session end; the client's save is
  suppressed and untouched. **M11 is the planned removal of this limitation** and can land any
  time after M7 — it is out of the *first playable*, not out of the project.
- Full inventory replication. Only equipped-gear *appearance* on the remote avatar, if M2's
  chosen object supports it cheaply. The object-id vs CSV-key split (§4.5) makes real inventory
  sync its own project.
- Trading, dropping items to each other, shared crafting.
- GameMaker `network_*` and the `network_config_*` constants — removed from scope by the
  own-socket decision, kept only as M3's documented fallback.
- Cross-version play. Builds must match; the handshake enforces it.
- Save merging tools, or any way to recover a session whose host save was lost.
- Localisation, UI polish, and any change to the game's own menus.

## 5. Testing two instances on one machine

Two processes of the same `StoneShard.exe`, the same `version.dll`, role chosen at runtime in
the overlay. There is no separate host build.

**Setup**

1. `cmake --build build --target deploy` puts `version.dll` in the game folder.
2. Copy the game folder to a second directory if M0's experiment 1 shows the runner refuses a
   second instance from the same one.
3. Launch each with its own mod data directory so logs and command files never collide:

   ```powershell
   $env:SSMOD_DATA_DIR = "D:\projects\stoneshard-mod\run\host"
   & "D:\torrent\Stoneshard (Early Access)\Stoneshard\StoneShard.exe"
   ```

   and, in a second shell, the same with `run\client`. `run/` goes in `.gitignore`.
4. If M0's save experiment failed, launch the second instance under a second Windows account
   with `runas` so it gets its own `%LOCALAPPDATA%`.

Use the **TCP** transport for all local testing — `127.0.0.1:27015` needs no relay and no
network round trip. iroh (M3b) can only be meaningfully tested between two *different* networks,
which is a separate exercise with a second machine.

**Driving both headlessly**

Each instance polls its own `$SSMOD_DATA_DIR\debug-cmd.txt` once a second and appends to its own
`debug-reply.txt`. A test script writes a command to one and reads the other's reply:

```powershell
Set-Content run\host\debug-cmd.txt   'host'
Start-Sleep 2
$ticket = (Get-Content run\host\debug-reply.txt -Tail 1)
Set-Content run\client\debug-cmd.txt "join 127.0.0.1:27015"
Start-Sleep 3
Set-Content run\host\debug-cmd.txt   'netstat'
Set-Content run\client\debug-cmd.txt 'netstat'
Get-Content run\host\debug-reply.txt -Tail 5
```

This is how every milestone from M3 onward is verified without touching either window: `getvar`
on both sides gives numbers to compare, `netstat` gives connection health, and the tracer can be
armed on either side to capture what the game did during a desync.

**Sanity checks to run every session**

- `status` on both — symbol count, `gml` ABI proven, builtin registry ready.
- Compare `mod.log` fingerprints (image size, section sizes) between the two — a mismatch means
  the two instances are not the same build.
- **Checksum the client's save file before and after.** §2 promises it is untouched; that is a
  testable claim and it should be tested every session, not assumed.
- Back up saves before every session. `backup::EnsureBackupOnce` fires on the first console
  call, but for co-op testing take a manual copy of `%LOCALAPPDATA%\StoneShard\characters_v1`
  first, because two processes writing it is exactly the scenario M0 is testing.

## 6. Consolidated list of unverified assumptions

Carried forward from the research plus everything this plan adds. Nothing here has been checked
against the running game. **This section is the project's honesty ledger; it is meant to shrink.
Replace an entry with a stated finding and its evidence as each experiment lands.**

### Resolved — findings, with evidence

**2026-09-09, first live run (M0 control plane, M1 phase A).**

- **The installed build is the one the research analysed.** Registrar RVA `0x529D2C0` and count
  global RVA `0x992A318` both match §2.2 exactly, once rebased off the live image.
- **The builtin registry resolves and the builtin ABI is right.** 11/11 anchors agree on the
  registrar, no runner-up. The three globals land adjacent at `ptr`, `+8`, `+0xC` as §2.7
  predicted. **2,532 of 2,535 registrations resolve with the arity spot-checks passing.**
  §7 Step 2's gate passes live: `buffer_create(64,1,1)` -> `8`, `buffer_get_size` -> exactly
  `64`, `buffer_delete` clean, `random_get_seed` -> a real.
- **The runner permits two instances from the same folder.** No single-instance mutex. Two
  processes ran together, each resolving the registry independently, each answering `status`
  on its own command file with its own pid.
- **Per-process data dirs work.** `SSMOD_DATA_DIR` splits the log, `imgui.ini`, `debug-cmd.txt`
  and `debug-reply.txt`, so neither instance eats the other's command.

**2026-09-09, with a character loaded (M1 phase B).**

- **Reflection reads work through `-1`/"self".** `variable_instance_get(-1, "x")` with the
  player's `CInstance*` supplied as the TRoutine's `self` returns the right value. This is the
  path `builtins::GetInstanceVar` and `gml::PlayerPosition` were already using, so §9.4's
  read-by-name design stands.
- **`variable_instance_set` is a working live mutator.** `x` 481 -> 490, read back 490, then
  restored to 481. **This is M2's gate, settled a milestone early**: state can be written onto
  an instance by name, not merely read.
- **`o_player` has 927-928 instance variables**, and `asset_get_index("o_player")` returns
  **5376**, independently confirming §4.2's object index. The field count is §5.5's missing
  bandwidth input: a naive full-state push is ~928 fields per entity per turn, so replication
  must send a curated subset, not everything reflection can see.
- **The `variable_instance_get_names` -> `array_get` route works**, so instance variables can be
  enumerated by name without decoding a GML array's memory layout.

**A wrong assumption this plan carried, now corrected.** `instance_find` does **not** return a
numeric instance id in this runtime - it returns **kind 15 (`kRef`)**, an instance reference.
The first implementation demanded a real and discarded the answer, which is why the id path
reported `FAIL` while the `self` path worked. Instance handles are RValues to be passed
straight back through, never numbers to be decoded. This matters for M2: driving a remote
avatar means addressing an instance that is *not* the running `self`, so the reference path is
the one that has to work there.

**2026-09-09, save format and the concurrent-write test (M0 experiment 2).**

**The save format is keyed JSON, and §4.4 is wrong about it.** The research concluded from
`saveSelfData` having no field-name strings that "the save format is *not* self-describing" and
is "a positional array over numeric variable ids". The compiled serialiser may well be
positional; what it **writes** is not. Every save file is:

```
zlib( <json text> <32 hex checksum> <NUL> )

salt     = "stOne!" + "!".join(dir path from characters_v1 down) + "!shArd"
checksum = md5(json text + salt)
```

**The checksum is fully understood and verified 7/7 against a live save directory**
(`tools/checkcksum.py`). The salt shape came from `MikaBuchholz/stoneshard-editor`
(`app/src/codec/save.ts`); the generalisation to "the containing directory's path" was
confirmed here, including the two cases that repo's own form does not cover
(`characters.map` -> `stOne!characters_v1!shArd`, `character_N/character.map` ->
`stOne!characters_v1!character_N!shArd`). Because the salt embeds the folder path, **a save
copied into a different character folder fails its own checksum** — which is a useful property:
it makes cross-character save smuggling detectable rather than silent.

`character_2/autosave_1/data.sav` is 16,829 bytes on disk, **137,092 bytes of JSON**, with 22
named top-level keys. `tools/savepeek.py` reads them.

This is the single biggest de-risking of the whole plan, because those 22 keys *are* the §9
design surface, already named and already readable offline:

| key | milestone it serves |
|---|---|
| `locationsRoomsDataMap` | **M7a / M9 — this is the location blob.** Keyed by grid coordinate (`"32_10"`) -> room name (`r_taverninside1floor`) -> variant -> `{flags, entitiesDataMapString}`, where the entities are a nested JSON string of `marksMap` / `npcMap` with `dynamic` and `static` sets, each NPC carrying `x`, a `roomEntityHash`, and a full `dataMap` of stats |
| `timeDataMap` | **M9 world time** — `{seconds, minutes, hours, days, months}`, nothing more |
| `characterDataMap` (111 keys) | **M7 character representation, M11 scrub.** Carries the world-entangled fields by name: `playerGridX`, `playerGridY`, `localX`, `localY`, `locationTitleKey`, `checkpointLocation`, `groundLevel` |
| `questsDataMap` (34) | **M10 quest flags** |
| `caravanStashDataList1..4` | **M10 shared stash** |
| `inventoryDataList` | §4.5 inventory |
| `locationsFogDataMap`, `globalmapDataMap.fogString` | M11's fog question |

**M11's scrub is now a named-field operation**, not a reverse-engineering job: the exact fields
to swap are listed above, and `tools/savepeek.py` can diff two saves as JSON.

**And M11 gains a second, independent implementation option.** With the checksum solved, a save
can be rewritten directly — decompress, edit the named fields, re-checksum, recompress — with
no game involvement at all. That is a genuine alternative to the scrub-then-`scr_savegame`
approach: it cannot leave the live instance in a half-scrubbed state, and it can run after the
process has exited. It trades the game's own serialiser for our own writer, so it only captures
what the JSON holds — but the JSON holds everything, including inventory. **Decide between the
two at M11**, on evidence, rather than now. Prior art worth reading first:
`MikaBuchholz/stoneshard-editor` already does exactly this round trip.

**The concurrent-write test found no corruption.** Two instances forced `scr_savegame` at the
same moment; both processes survived, each rewrote its own `character.map`, and all seven save
files still decompress and parse as valid JSON with the right character names. The shared index
turned out to be far less dangerous than assumed: `characters.map` is 54 bytes of
`{ "lastCharacter": ..., "lastSave": ... }` and **does not enumerate characters at all** - so the
worst a collision can do is disagree about who played last, not lose a character.

**M0 experiment 2 passed definitively.** After the concurrent saves, both instances were
restarted and both characters loaded and played normally, each resolving the registry and
passing phase B independently. M0 is complete.

**Save keys are NOT instance variable names.** This was flagged as a caveat; it is now
confirmed, and it costs M11 real work. Of six `characterDataMap` keys tried against the live
`o_player` by reflection, only `LVL` and `HP` exist under that name. `nameKey`, `classKey`,
`XP` and `locationTitleKey` all return undefined — and they are not GML globals either
(`variable_global_get` returns undefined for each). The instance carries differently-named
counterparts: `name` / `id_name`, `max_xp` / `Received_XP` / `xp_mul`, `globalGridX` /
`globalGridY` / `grid_x` / `grid_y`. So `scr_savegame` runs a **translation layer** between
instance state and save keys, and that mapping is not yet known.

Consequences:

- **M11's scrub-via-reflection needs the mapping first**, which is unscoped work. The exact
  fields are known by their *save* names; their *instance* names are not.
- **This strengthens the direct file-rewrite option for M11.** Editing the JSON by its own key
  names needs no mapping at all, and the checksum is solved. The reflection route's advantage
  was using the game's serialiser; that advantage is smaller now that the translation layer has
  to be reverse-engineered to use it.
- A dump of all 931 `o_player` instance variable names is at `run/o_player-variables.txt`,
  regenerable any time with the `names 1000` command.

**A real bug in the existing mod, found by using it.** `remote::Poll` reads commands with
`fgets`, which keeps the trailing newline, and `console::Tokenize` strips only spaces and tabs.
So every `call <symbol>` sent through `debug-cmd.txt` looked up `"<symbol>\n"`, failed, and
reported the failure only to the ImGui buffer where nothing outside the game can see it. The
`bp` path worked because `remote.cpp`'s own `Split` does strip newlines. Fixed in both places.

**Two corrections the research's own recipe needed** (§2.7 steps 1-4), both found by running it:

1. *Step 1-2 assumed one `.rdata` occurrence per name.* Half the anchors have **two**
   (`buffer_create`, `buffer_get_size`, `buffer_delete`, `random_set_seed`, `json_encode`,
   `variable_instance_get`). Taking the first occurrence and bailing on the first `lea` site
   resolved only 2 of 5 anchors. The resolver now keeps every occurrence and tallies candidates
   by *distinct anchor* across one pass of each section.
2. *Step 4 said the globals are in the registrar's "first ~0x40 bytes".* Count and capacity
   are, but the **array pointer is not read until ~0xAA in**. A 0x60-byte window finds two of
   three and fails. The window is now 0x200, and the pointer is cross-checked against
   `count - 8` rather than taken as "the first qword global read".

**From the research (§8.2), still open**

- `o_player_observer`'s actual purpose. Never read. (M2a)
- Whether unit turn order accepts an extra participant (`scr_unitTurnNext`). (M2a, M6)
- Connect/disconnect async `type` values. (Moot — the mod uses its own socket.)
- Socket-type and `network_config_*` constants. (Moot; needed only for M3's fallback.)
- `sub_1451C3EC0`'s exact identity — 429 of the 910 `o_player` reference sites.
- Bandwidth budget. Needs `variable_instance_names_count` on a live `o_player`. Measured by
  M1's `names` command.

**Mod and game state**

- That reflection works against an instance that is **not** the running `self`, addressed by
  the `kRef` handle `instance_find` returns. Reads and writes via `-1`/"self" are proven; the
  reference path is what M2 needs and is not yet confirmed. (M1 phase B, re-run)
- Whether `instance_create_depth`'s return value can be used as a handle directly, or whether a
  `CInstance*` must be recovered from it to use the `self` path. (M2b)
- That an avatar object can be spawned and driven without disturbing the 176 singleton
  dereferences. (M2)
- That two instances can be kept off the same save directory. (M0 experiment 2)
- That skipping `scr_savegame` leaves a room transition consistent — needed by M0 *and* by §2's
  save story. (M0)
- That suppressing `scr_player_move` / `scr_attack` on the client leaves it consistent. (M5)
- That suppressing `scr_allturn` on the client leaves it consistent. (M6)
- Which object index covers all hostile entities. (M8)
- The minimum field set that lets the host resolve combat for a character it did not create.
  (M1 measurement, M7 use)
- That a location transition can be driven from the mod at all. (M7a)
- That location generation is deterministically seeded — §9.1 depends on this. (M7a)
- Whether the game has a world seed, and where it lives. No obvious seed key appeared among the
  22 save keys, which is mild evidence against §9.1's assumption. (M7a)
- Whether world time can be written safely through globals or must go through
  `scr_timeUpdate`. The *storage* is now known (`timeDataMap`); the live path is not. (M9)
- Whether two instances can be made to collide on `characters.map` under heavier contention.
  One concurrent pair of saves produced no damage, but that is one trial of a race, not proof
  that the race cannot be lost.
- Whether the world-entangled fields now known by name in `characterDataMap` (`playerGridX`,
  `playerGridY`, `localX`, `localY`, `locationTitleKey`, `checkpointLocation`, `groundLevel`)
  are the *complete* set, and whether reflection reaches each of them on the live instance under
  those same names. The save keys and the instance variable names need not match. (M11)
- Whether a scrubbed save loads correctly in the client's own world afterwards. (M11)

**iroh and the build**

*Resolved 2026-09-09 by reading `n0-computer/iroh-c-ffi` — kept here as findings, not
assumptions, and worth re-checking before M3b starts:* a purpose-built C FFI exists, builds a
`staticlib`, pins `iroh = "1.0.0"`, uses `safer-ffi 0.1.13` for the header, and exposes the
whole surface the mod needs — endpoint + ALPN, accept and connect, bidirectional streams with
read/write timeouts, RTT and packet-loss counters.

Still open:

- Whether it builds and links on MSVC against `/MT` with `+crt-static`. The repo's own example
  links `-lSystem -lc -lm`; nothing in it documents Windows. (M3b spike — now the main risk)
- Whether the internally-started multi-threaded tokio runtime coexists with the game and shuts
  down cleanly through `endpoint_close`. (M3b spike)
- Whether `safer-ffi` already guards the FFI boundary against unwinding panics, or whether the
  mod must add `catch_unwind` itself. (M3b spike)
- Whether a single opaque ticket string is reachable from C, or the mod encodes the address
  components itself. Gates nothing either way (§2).
- Relay availability, terms of use for a game mod, and what a relay observes. (M3b)

## 7. New and modified files, at a glance

| file | status | milestone |
|---|---|---|
| `src/paths.{h,cpp}` | new | M0 |
| `src/log.cpp`, `src/savebackup.cpp` | edit — route through `paths::DataDir()` | M0 |
| `src/builtins.{h,cpp}` | edit — `SelfTest`, instance-id fallback | M1 |
| `src/remote.cpp` | edit — `callb`, `getvar`, `setvar`, `names`, `host`, `join`, `netstat`, `spawnavatar` | M1, M2, M3 |
| `src/avatar.{h,cpp}` | new — spawn and drive the remote avatar | M2 |
| `src/transport.h` | new — the interface both transports implement | M3 |
| `src/net_tcp.cpp` | new — TCP, framing, worker thread, queues | M3 |
| `src/protocol.h` | new — message types and wire layout | M3 |
| `rust/` | new — iroh staticlib crate, driven from CMake | M3b |
| `src/net_iroh.cpp` | new — iroh behind the same `Transport` interface | M3b |
| `src/session.{h,cpp}` | new — role, join, replication, turn arbitration, leases | M4–M10 |
| `src/clientsave.{h,cpp}` | new — the `scr_savegame` hook: suppress (M7), then scrub/save/restore (M11) | M7, M11 |
| `src/overlay.cpp` | edit — Multiplayer tab, per-instance title, status | M0–M3b |
| `CMakeLists.txt` | edit — new sources, link `ws2_32`, cargo integration | M0, M3, M3b |

No existing hook is replaced. Every new hook uses the established `sym::Find` +
`MH_CreateHook` pattern from `gml::InstallPlayerTracker`.

## 8. Execution order summary

```
M0   two instances                  prerequisite, fails in minutes if it fails
M1   builtin registry live          gate for all reflection
M2   remote avatar, one process     RISKIEST — no network needed to prove it
M3   TCP transport + final UI       first bytes between two games
M3b  iroh by ticket                 independent of M4-M10; before any internet play
M4   host position -> client        first replicated state, no geography
M5   client intents -> host         authority round-trip
M6   turn arbitration               one turn per round, measured
M7   co-location                    "join" actually puts you in the same place
M8   shared-location entities       playable together
M9   leases                         playable apart
M10  quest flags and stash          §9.5's open design
M11  client save: scrub and restore the client keeps what it earned; any time after M7
```

First playable is M9 complete, with M3b for anyone not on the same LAN. M10 makes it safe to
play a campaign in, and M11 makes it worth the client's time.

Do not start a milestone whose predecessor's pass criterion has not been met and recorded.
