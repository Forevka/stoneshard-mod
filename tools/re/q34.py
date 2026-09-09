"""Measurements for research questions 3 (state inventory) and 5 (turn structure)."""
import sys, re, json, collections
sys.path.insert(0, '.')
import relib as R, idx, pdata as P, assetref as A

T = R.table()
SET = {v: k for k, v in T.items()}
OPLAYER = 5376

def uses_oplayer(sym):
    a = T[sym]
    f = P.func_of(a)
    if not f: return 0
    hits = [h for h in A.int_hits(OPLAYER) if f[0] <= h < f[1] and A._B[h - A.TVA0 - 1] in A.PREV_OK]
    return len(hits)

def section(title):
    print('\n' + '=' * 72); print(title); print('=' * 72)

# --- exact o_player event list ---
section('o_player events (exact, excludes o_player_AI / _corpse / _observer / etc.)')
evs = sorted(k for k in T if re.fullmatch(r'gml_Object_o_player_(Alarm|Step|Draw|Other|Mouse|KeyPress|KeyRelease|Keyboard|Create|Destroy|CleanUp|Collision)_\d+', k))
print('count:', len(evs))
for k in evs: print('   %-46s %#x' % (k, T[k]))

# --- turn pipeline ---
section('turn pipeline: call-site counts (gml_Script_* = compiled body)')
turns = ['scr_allturn', 'scr_global_turn', 'scr_global_turn_end', 'scr_turn', 'scr_turn_npc',
         'scr_unitTurnNext', 'scr_unitTurnGetTime', 'scr_unitTurnBreak', 'scr_unitTurnEvents',
         'scr_unitTurnPositionEvents', 'scr_unitTurnCheckPlayerTurn', 'scr_turnallow',
         'scr_skip_turn', 'scr_enemy_turn', 'scr_timeUpdate', 'scr_characterTimeUpdate',
         'scr_timeIsFrozen', 'scr_player_move', 'scr_attack', 'scr_everyPlayerTurnQuestTriggers']
rows = []
for t in turns:
    n = 'gml_Script_' + t
    if n not in T: print('   %-34s MISSING' % t); continue
    a = T[n]
    f = P.func_of(a)
    sites = len(idx.calls_to(a)) + len(idx.jmps_to(a))
    callers = len(set(P.func_of(s)[0] for s in idx.calls_to(a) if P.func_of(s)))
    rows.append((sites, t, a, f[1] - f[0] if f else 0, callers, uses_oplayer(n)))
for sites, t, a, sz, callers, op in sorted(rows, reverse=True):
    print('   %-34s %#11x size=%-7s sites=%-4d callers=%-4d o_player_refs=%d' % (t, a, hex(sz), sites, callers, op))

# --- player state scripts ---
section('player-state script families (count of gml_Script_* symbols per prefix)')
fams = ['scr_atr', 'scr_skill', 'scr_perk', 'scr_inv', 'scr_item', 'scr_state', 'scr_buff',
        'scr_hunger', 'scr_thirst', 'scr_pain', 'scr_psy', 'scr_fatigue', 'scr_intoxication',
        'scr_unit', 'scr_player', 'scr_save', 'scr_load', 'scr_quest', 'scr_npc', 'scr_time']
for p in fams:
    ks = [k for k in T if k.startswith('gml_Script_' + p)]
    print('   %-22s %d' % (p + '*', len(ks)))

section('save/load scripts (the closest thing to a full player-state serialiser)')
for k in sorted(k for k in T if k.startswith('gml_Script_') and re.search(r'(save|load)', k, re.I)):
    a = T[k]; f = P.func_of(a)
    sites = len(idx.calls_to(a))
    if sites or (f and f[1] - f[0] > 0x800):
        print('   %-56s %#x size=%-8s sites=%d' % (k, a, hex(f[1] - f[0]) if f else '?', sites))
