#!/usr/bin/env bash
# What N server-run bots cost (#837): a dedicated server with --bots N (free roam, round the default spawn),
# --serverstats and --botprofile, and one client among them recording a session perf log (--perflog), so both
# sides are measured: the server's busy ms per frame, and the client's frame time with N figures in view.
#   GODOT=<exe> tools/botperf.sh <bots> [seconds=120] [WINDOWED=1 CLIENT_ARGS="--screen 1"] [SERVER_ARGS=...]
# Output: test_output/loadtest/botperf<N>/server_summary.txt, test_output/botperf_*.log, the client's summary.txt.
. "$(dirname "$0")/lib/twoclient.sh" botperf
N=${1:?usage: tools/botperf.sh <bots> [seconds]}
SECS=${2:-120}
PORT=${PORT:-7836}
AT=2583250,1113250
tc_server $((SECS + 200)) 120 "$OUT/botperf_server.log" --server --port $PORT --no-query --serverstats,botperf$N --bots $N --botprofile ${CH[@]+"${CH[@]}"} ${SERVER_ARGS:-}
# the perf log stops itself after SECS and writes its summary; the timeout then ends the client
tc_client $((SECS + 60)) "$OUT/botperf_A.log" --connect 127.0.0.1:$PORT --name "BPA" --cache "$OUT/botperf_cache_A" --at "$AT" --perflog $SECS ${CH[@]+"${CH[@]}"} ${CLIENT_ARGS:-}
tc_stop
echo "[botperf] $N bots, ${SECS}s"
grep -h "busy\|frame_ms\|gc\|ws_mb\|net_" "$OUT/loadtest/botperf$N/server_summary.txt" 2>/dev/null | head -20
grep -h "^\[botprofile\]" "$OUT/botperf_server.log" | tail -3
grep -h "slow frame" "$OUT/botperf_server.log" | tail -5
summary=$(find "$OUT/userdata_botperf" -path "*perf_logs*" -name summary.txt 2>/dev/null | tail -1)
[ -n "$summary" ] && { echo "[botperf] client: $summary"; cat "$summary"; }
