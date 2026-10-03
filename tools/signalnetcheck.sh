#!/usr/bin/env bash
# Traffic lights over loopback (#353): a headless dedicated server and two headless clients that
# each log, every 0.5 s of the server clock (ClockSync.ServerNow), the aspect of every group of one
# signalised junction, as the lamps and the traffic read it (SignalPlan.State). Traffic itself is
# local to each client: only the signal state is compared. Every peer must log the same plan, the
# same aspects per instant (a sample may differ only next to an aspect change, within the gap the
# clocks explain), and each client's server clock must be within 0.1 s of the server's (measured
# through the machine's wall clock, all three run here).
#   tools/signalnetcheck.sh            a crossroads plan built in code, flat fixture world: no terrain data
#   CHUNKS=<dir> JUNCTION=E,N tools/signalnetcheck.sh   the signal record nearest E,N in that map
#                                      (e.g. the Geneva copy, 2499901,1118599)
# (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" signalnet
PORT=${SIGNALNET_PORT:-7851}
SECONDS_LOGGED=${SIGNALNET_SECONDS:-20}
# the server on the flat fixture (the tile is read from --chunks), the clients without terrain
JUNCTION=${JUNCTION:-}
tc_server 300 120 $OUT/signalnet_server.log --server --port $PORT --world fixture ${CH[@]+"${CH[@]}"} --signalnetcheck $JUNCTION
tc_client 240 $OUT/signalnet_A.log --connect 127.0.0.1:$PORT --name A --systems network ${CH[@]+"${CH[@]}"} --signalnetcheck $JUNCTION --seconds $SECONDS_LOGGED &
A=$!
tc_client 240 $OUT/signalnet_B.log --connect 127.0.0.1:$PORT --name B --systems network ${CH[@]+"${CH[@]}"} --signalnetcheck $JUNCTION --seconds $SECONDS_LOGGED
wait $A
tc_stop
grep -h "\[signalnet [^]]*\] \(plan\|RESULT\)" $OUT/signalnet_server.log $OUT/signalnet_A.log $OUT/signalnet_B.log

# per instant (k = floor(server time / 0.5)), every peer's states; the clock error of each client
# is the median of (wall - t) on the server minus that on the client
grep -ah "\[signalnet [^]]*\] k=" $OUT/signalnet_server.log $OUT/signalnet_A.log $OUT/signalnet_B.log | tr -d '\r' | awk '
function val(s, key,   i) { i = index(s, key "="); return substr(s, i + length(key) + 1) + 0 }
function median(arr, n,   i, j, t) {
  for (i = 2; i <= n; i++) { t = arr[i]; for (j = i - 1; j >= 1 && arr[j] > t; j--) arr[j + 1] = arr[j]; arr[j + 1] = t }
  return n % 2 ? arr[(n + 1) / 2] : (arr[n / 2] + arr[n / 2 + 1]) / 2
}
{
  match($0, /\[signalnet [^]]*\]/); peer = substr($0, RSTART + 11, RLENGTH - 12)
  split($0, f, " ")
  for (i in f) {
    if (f[i] ~ /^k=/) k = substr(f[i], 3)
    if (f[i] ~ /^t=/) t = substr(f[i], 3) + 0
    if (f[i] ~ /^wall=/) w = substr(f[i], 6) + 0
    if (f[i] ~ /^edge=/) e = substr(f[i], 6) + 0
    if (f[i] ~ /^states=/) s = substr(f[i], 8)
  }
  peers[peer] = 1
  n[peer]++; off[peer, n[peer]] = w - t
  S[peer, k] = s; T[peer, k] = t; E[peer, k] = e; keys[k] = 1
}
END {
  for (p in peers) { m = n[p]; delete tmp; for (i = 1; i <= m; i++) tmp[i] = off[p, i]; med[p] = median(tmp, m) }
  ref = ("server" in peers) ? "server" : "A"
  maxskew = 0
  for (p in peers) { skew[p] = med[ref] - med[p]; a = skew[p] < 0 ? -skew[p] : skew[p]; if (a > maxskew) maxskew = a
    printf "[signalnetcheck] %s: %d samples, clock %+.4f s from the %s\n", p, n[p], skew[p], ref }
  common = 0; same = 0; explained = 0; bad = 0
  for (k in keys) {
    if (!(("A", k) in S) || !(("B", k) in S)) continue
    common++
    nk = split("A B server", list, " "); diff = 0; ok = 1
    for (i = 1; i <= nk; i++) for (j = i + 1; j <= nk; j++) {
      p = list[i]; q = list[j]
      if (!((p, k) in S) || !((q, k) in S) || S[p, k] == S[q, k]) continue
      diff = 1
      # different only next to a change: the change lies between the two instants, which differ by
      # the frame each sampled in and by the two clocks error
      dt = T[p, k] - T[q, k]; if (dt < 0) dt = -dt
      ds = skew[p] - skew[q]; if (ds < 0) ds = -ds
      edge = E[p, k] < E[q, k] ? E[p, k] : E[q, k]
      if (edge > dt + ds + 0.005) { ok = 0; printf "[signalnetcheck] k=%s: %s %s vs %s %s, %.3f s from a change (unexplained)\n", k, p, S[p, k], q, S[q, k], edge }
    }
    if (!diff) same++; else if (ok) explained++; else bad++
  }
  changes = 0; prev = ""; seen = 0
  for (k = 0; k <= 1e7 && seen < n["A"]; k++) if (("A", k) in S) { seen++; if (prev != "" && S["A", k] != prev) changes++; prev = S["A", k] }
  printf "[signalnetcheck] %d instants logged by both clients, %d aspect changes among them: %d identical on every peer, %d differing next to a change, %d unexplained; max clock skew %.4f s\n", common, changes, same, explained, bad, maxskew
  print (common >= 30 && bad == 0 && maxskew < 0.1 && ("server" in peers)) ? "COMPARE ok" : "COMPARE FAILED"
}' | tee $OUT/signalnet_compare.txt

code=0
tc_ok 2 $OUT/signalnet_A.log $OUT/signalnet_B.log || code=1
grep -q "COMPARE ok" $OUT/signalnet_compare.txt || code=1
# one plan on every peer
[ "$(grep -ah "\[signalnet [^]]*\] plan" $OUT/signalnet_server.log $OUT/signalnet_A.log $OUT/signalnet_B.log | tr -d '\r' | sed 's/^.*\] plan/plan/' | sort -u | wc -l)" -eq 1 ] || { echo "[signalnetcheck] the peers logged different plans"; code=1; }
echo "[signalnetcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
