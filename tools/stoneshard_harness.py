"""Plays Stoneshard over the test host, through the StoneshardHarness mod.

The game must run with the test host on and the harness deployed:
    tools\\run-game.ps1 -Game Stoneshard -TestHost -Deploy -Mods StoneshardHarness

As a module:
    from stoneshard_harness import Stoneshard
    g = Stoneshard()
    print(g.state())
    for e in g.enemies():
        print(g.attack(e["id"], turns=20))

As a command (prints JSON; actions wait until the game has settled):
    python tools\\stoneshard_harness.py state
    python tools\\stoneshard_harness.py attack 401593 20

Looking: state, player, enemies [all], npcs [all], objects [reach] [all],
inventory, log [n], dialogue, buttons, screen <gx> <gy>.
Acting: move, goto, attack, interact, use, wait, say, press, key, click,
actions (each returns hx.result once the action is done).
Protocol: managed/README.md#test-host.
"""
import json
import os
import sys
import threading
import time

DEFAULT_GAME_DIR = r"D:\torrent\Stoneshard (Early Access)\Stoneshard"

# Commands that start an action: answered with {seq}, then polled with hx.result.
ACTIONS = {"move", "goto", "attack", "interact", "use", "wait", "say", "press", "key", "click", "actions"}


class HarnessError(Exception):
    """The game answered ok:false (bad argument, nothing to act on...)."""


class Stoneshard:
    def __init__(self, game_dir=None, pipe=None):
        self.game_dir = game_dir or os.environ.get("STONESHARD_DIR", DEFAULT_GAME_DIR)
        self._pipe_name = pipe
        self._pipe = None
        self._next = 0

    # ------------------------------------------------------------ transport

    def _connect(self):
        if self._pipe is not None:
            return self._pipe
        name = self._pipe_name
        if not name:
            path = os.path.join(self.game_dir, "Lodestone", "Logs", "testhost.pipe")
            with open(path, encoding="utf-8") as f:
                name = f.read().strip()
        self._pipe = open(r"\\.\pipe" + "\\" + name, "r+b", buffering=0)
        return self._pipe

    def close(self):
        if self._pipe is not None:
            self._pipe.close()
            self._pipe = None

    def call(self, cmd, *args, timeout=30):
        """Sends one test-host command; returns its result or raises HarnessError."""
        self._next += 1
        line = json.dumps({"id": self._next, "cmd": cmd, "args": list(args), "timeout": timeout}) + "\n"
        pipe = self._connect()
        # The timeout sent only bounds when the game may start the request; a
        # game stopped on a modal error dialog never answers at all. So the
        # read runs on a thread and is given up two seconds past the timeout
        # (closing the pipe ends the blocked read).
        box = {}

        def read():
            try:
                answer = b""
                while not answer.endswith(b"\n"):
                    chunk = pipe.read(1)
                    if not chunk:
                        raise ConnectionError("the game closed the pipe")
                    answer += chunk
                box["answer"] = answer
            except OSError as e:
                box["error"] = e

        try:
            pipe.write(line.encode("utf-8"))
        except OSError:
            self.close()
            raise
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        reader.join(timeout + 2)
        if reader.is_alive() or "error" in box:
            self.close()
            if "error" in box:
                raise box["error"]
            raise TimeoutError(f"no answer to '{cmd}' within {timeout + 2} s (is a dialog blocking the game?)")
        reply = json.loads(box["answer"])
        if not reply.get("ok"):
            raise HarnessError(f"{cmd} failed: {reply.get('error')}")
        return reply.get("result")

    def act(self, verb, *args, timeout=60):
        """Starts an hx.<verb> action and returns hx.result once it is done."""
        started = self.call("hx." + verb, *args)
        seq = started["seq"]
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = self.call("hx.result", seq)
            if result.get("done"):
                return result
            time.sleep(0.2)
        raise TimeoutError(f"action {seq} ({verb}) still running after {timeout} s (it goes on in the game)")

    # ------------------------------------------------------------ looking

    def state(self):
        return self.call("hx.state")

    def player(self):
        return self.call("hx.player")

    def enemies(self, all=False):
        return self.call("hx.enemies", *(["all"] if all else []))

    def npcs(self, all=False):
        return self.call("hx.npcs", *(["all"] if all else []))

    def objects(self, reach=12, all=False):
        return self.call("hx.objects", reach, *(["all"] if all else []))

    def inventory(self):
        return self.call("hx.inventory")

    def log(self, n=10):
        return self.call("hx.log", n)

    def dialogue(self):
        return self.call("hx.dialogue")

    def buttons(self):
        return self.call("hx.buttons")

    def screen(self, gx, gy):
        return self.call("hx.screen", gx, gy)

    # ------------------------------------------------------------ acting

    def move(self, dx, dy):
        return self.act("move", dx, dy)

    def goto(self, gx, gy, timeout=180):
        return self.act("goto", gx, gy, timeout=timeout)

    def attack(self, enemy_id, turns=1, timeout=None):
        return self.act("attack", enemy_id, turns, timeout=timeout or 30 + 15 * turns)

    def interact(self, thing_id, action=None):
        return self.act("interact", thing_id, *([action] if action else []))

    def use(self, item_id, action=None):
        return self.act("use", item_id, *([action] if action else []))

    def actions(self, thing_id):
        return self.act("actions", thing_id)["extra"]["actions"]

    def wait(self, turns=1):
        return self.act("wait", turns, timeout=30 + 10 * turns)

    def say(self, option):
        return self.act("say", option)

    def press(self, button):
        return self.act("press", button)

    def key(self, key):
        return self.act("key", key)

    def click(self, sx, sy, right=False):
        return self.act("click", sx, sy, *(["right"] if right else []))


def _arg(word):
    """A command-line word as a JSON value: numbers by their spelling, the rest as text."""
    try:
        return int(word)
    except ValueError:
        try:
            return float(word)
        except ValueError:
            return word


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    verb, args = argv[1], [_arg(a) for a in argv[2:]]
    game = Stoneshard()
    try:
        if verb in ACTIONS:
            result = game.act(verb, *args, timeout=600)
        else:
            result = game.call(verb if verb.startswith("hx.") or "." in verb or verb in ("ping", "status") else "hx." + verb, *args)
    except HarnessError as e:
        print(e, file=sys.stderr)
        return 1
    # TimeoutError is an OSError: it has to be caught first.
    except TimeoutError as e:
        print(e, file=sys.stderr)
        return 3
    except OSError as e:
        print(f"cannot reach the game: {e}", file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
