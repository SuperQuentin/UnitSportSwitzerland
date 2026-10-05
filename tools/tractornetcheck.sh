#!/usr/bin/env bash
# The farm machines from another peer, over loopback (#494, src/Player/TractorNetProbe): a dedicated
# server on the flat fixture with an admin password and two headless clients. A takes the tractor,
# lowers its plough and parks it; B sees the plough down on A's tractor and on the parked one. A
# takes a tipping trailer with 77 sacks of wheat and parks the train; B reads the sacks in the code,
# sees the heap and the parked train keep them. A takes the combine with 50 sacks of barley; B reads
# them from its flags. Logistics: B tows an empty tipping trailer and A's combine augers 30 sacks of
# wheat into it through the server (both agree); at a stand-in farm co-op (--farmcoop, the same LV95
# point on the server and both clients) the server refuses A a delivery claimed from afar and B's
# seed, and pays B's tipped trailer 30 x 25 CHF; A sees the bin up and the trailer empty. No terrain
# data needed. Output in test_output/tractornet_*.log.
#   GODOT=<exe> [PORT=] tools/tractornetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" tractornet
PORT=${PORT:-7881}
WORLD="--world fixture --chunks fixture:flat --traffic 0"
# the stand-in co-op: 150 m west and 90 m north of the fixture's start (SpawnPoint.Default), facing south
COOP="--farmcoop 2583100,1113340"
tc_server 560 120 "$OUT/tractornet_server.log" --server --port $PORT --world fixture --chunks fixture:flat --admin-password test $COOP
client() { tc_client 500 "$OUT/tractornet_$1.log" --connect 127.0.0.1:$PORT --name "Farmer$1" $WORLD $COOP --tractornet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[tractornet" "$OUT/tractornet_A.log" "$OUT/tractornet_B.log"
# the server's own word: the sacks the auger moved, and what it paid for the tipped load
grep -h "\[passengers\] auger\|\[shop\] peer" "$OUT/tractornet_server.log"
PAID=$(grep -c "delivered 30 Wheat to the farm co-op .* for 750 CHF" "$OUT/tractornet_server.log")
if [ "$PAID" = 1 ] && tc_ok 2 "$OUT/tractornet_A.log" "$OUT/tractornet_B.log"; then echo "[tractornetcheck] RESULT: ok"; exit 0; fi
echo "[tractornetcheck] RESULT: FAILED (see $OUT/tractornet_*.log; server paid $PAID times)"; exit 1
