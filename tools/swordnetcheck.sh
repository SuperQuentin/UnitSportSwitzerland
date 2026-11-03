#!/usr/bin/env bash
# The sword over loopback (#1371, src/Combat/SwordNetProbe): a headless dedicated server on the flat fixture and two clients.
# A puts a training dummy down with /dummy; B must see it. A's three taps, a held attack (the charged blow) and a dodge must
# show on B (A's copy swings, the dummy takes the blows and recoils); ten forged dodges and a hit nobody swung are refused by
# the server; a server-run bot with a sword hits the dummy; with /pvp on A's blow hurts and knocks back B on its own machine,
# with /pvp off it does nothing.
#   tools/swordnetcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 [STYLE=cartoon|ps1] tools/swordnetcheck.sh    (B windowed, for a look at A's sword and the dummy: test_output/swordnet_B_*.png)
# Real time: the server's guard counts wall seconds, so no --checkspeed here.
. "$(dirname "$0")/lib/twoclient.sh" swordnet
PORT=${SWORD_PORT:-7857}
WORLD="--world fixture"
AT=2583250,1113250
AT_B=2583253,1113250   # never on top of A: two bodies spawned in one place throw each other kilometres
tc_server 260 120 $OUT/swordnet_server.log --server --port $PORT $WORLD --admin-password swordnet
tc_client 240 $OUT/swordnet_A.log --connect 127.0.0.1:$PORT --name SwordA $WORLD --at $AT --swordnet A ${A_EXTRA:-} &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
STYLE_ARG=; [ -n "${STYLE:-}" ] && STYLE_ARG="--style $STYLE"
tc_client 240 $OUT/swordnet_B.log $WIN $STYLE_ARG --connect 127.0.0.1:$PORT --name SwordB $WORLD --at $AT_B --nocapture --swordnet B
wait $A
tc_stop
grep -h "\[swordnet\|\[sword\]" $OUT/swordnet_A.log $OUT/swordnet_B.log
grep -h "\[sword\]" $OUT/swordnet_server.log | head -40
code=0
tc_ok 2 $OUT/swordnet_A.log $OUT/swordnet_B.log || code=1
echo "[swordnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
