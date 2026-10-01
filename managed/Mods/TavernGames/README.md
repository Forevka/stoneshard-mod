# Tavern Games

Gambling and contests with the locals in **Stoneshard**, for crowns. The mod loads only in that
game.

## Use

Talk to someone in a tavern (an innkeeper, a drunk, a sellsword, or anyone friendly inside the inn).
Their conversation offers a line asking for a game, just above the goodbye. Choosing it opens a
table drawn with the game's own panels, buttons, text and sounds. Pick a game and a stake, and play.

| Game | How it plays |
|---|---|
| **Poker Dice** | Five dice each, a round of betting (raise, check, call, fold), one reroll of any dice. Click your dice to mark them. |
| **Twenty-One** | The NPC deals from a deck on the table: cards slide out and turn over. Hit, stand, or double. The dealer draws to a total set by its temperament. |
| **Thimblerig** | Follow the ball under the shuffled cups and click the right one. Five levels: each one won offers the next, with more cups, faster swaps and a higher stake. |
| **Arm Wrestling** | A tug-of-war: press **Space** (or click) while the cursor is in the zone. The zone is as wide as your STR against theirs. Costs real **Fatigue**. |
| **Drinking Contest** | Mug for mug: down each by stopping a swaying marker in the middle. **Every mug is real Drunkenness**, with the game's own confusion, vomiting and sleep, and you may pass out at the table. |

- **Opponents** are cautious (innkeepers, staff), steady or reckless (drunks, sellswords), and play
  that way.
- **Purses:** each opponent has a limited purse that runs dry and refills over turns.
- **Your stake** leaves your purse when you commit it and comes back with the winnings.
- **Leaving:** Esc or *Leave* stands up. Leaving mid-round folds it, unless the round is already
  decided.

## Settings (`Mods\TavernGames.json`)

| Key | Default | |
|---|---|---|
| `playKey` | empty | a key to play with whoever stands next to you, with an on-screen prompt; off when empty |
| `reachTiles` | `1.5` | how near someone must stand for the play key |
| `anywhere` | `false` | play with anyone friendly anywhere, not only in taverns |

## Test host

| Command | What it does |
|---|---|
| `tg.npcs` | the nearest NPCs and whether they would play |
| `tg.open [game [npcId]]` | sits down at the table |
| `tg.state` | the table, the round and the game's state |
| `tg.press <id>` | presses a table or game button |
| `tg.toggle <0-4>` | Poker Dice: marks a die to throw again |
| `tg.cup <slot>` | Thimblerig: picks a cup |
| `tg.act [aim\|miss]` | the action key (Arm Wrestling, Drinking Contest) |
| `tg.seed <n>` | repeatable dice, deck and cups |
| `tg.close` | stands up |

## Building and art

- Written against Stoneshard's generated interop (`<InteropGame>StoneShard</InteropGame>`): run the
  game once with Lodestone, or `tools\setup-dev.ps1`, before building it.
- `assets-src/make_assets.py` generates the dice, cards, cup and ball.
- `assets-src/import_generated.py` brings drawn art (arm wrestling and drinking) down to sprite size.
  Its header lists the sizes and pivots the code expects.
- A new game is one `MiniGame` class added to the list in `TavernGamesMod.cs`; the `Table` runs the
  stake, purses, window and input.
