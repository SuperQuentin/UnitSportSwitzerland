#!/usr/bin/env bash
# A CD in a car stereo over loopback (#211): a dedicated server that burns two fixture CDs at boot
# (ffmpeg on PATH), a headless driver that takes a car and works the radio panel (a station, then
# "play the list" and CD A; A runs out and its changer puts on B; then it parks), and a windowed
# watcher (a headless client has no speaker) that must hear A, then B, from the driver's car and B
# from the parked car, on the shared clock from the right file.
#   tools/carcdcheck.sh                 (terrain_chunks/ in this checkout)
#   CHUNKS=/path/to/terrain_chunks tools/carcdcheck.sh   (a worktree without terrain data)
. "$(dirname "$0")/lib/twoclient.sh" carcd
PORT=7811   # its status query answers on PORT+1: keep clear of the other checks
PW=carcdpw
FIXA="$PWD/$OUT/carcdA.wav"
FIXB="$PWD/$OUT/carcdB.wav"
# click tracks of different lengths, so the watcher can tell which file a speaker loaded
ffmpeg -y -loglevel error -f lavfi -i "sine=f=520:b=4:d=20" -ac 1 -ar 22050 "$FIXA" || { echo "ffmpeg is needed on PATH"; exit 1; }
ffmpeg -y -loglevel error -f lavfi -i "sine=f=780:b=4:d=50" -ac 1 -ar 22050 "$FIXB"
# a full-world server blends its horizon for half a minute before it listens: wait for it
tc_server 700 300 $OUT/carcd_server.log --server --port $PORT --admin-password $PW --cdfixture "$FIXA" --cdfixture "$FIXB" ${CH[@]+"${CH[@]}"}
sleep 2
tc_client 450 $OUT/carcd_driver.log --connect 127.0.0.1:$PORT --name Driver --carcdcheck driver --carcdpw $PW --traffic 0 --cache "$PWD/$OUT/carcd_cache_driver" ${CH[@]+"${CH[@]}"} &
DRIVER=$!
sleep 2
tc_client 445 $OUT/carcd_watch.log --windowed --connect 127.0.0.1:$PORT --name Watcher --carcdcheck watch --traffic 0 --cache "$PWD/$OUT/carcd_cache_watch" ${CH[@]+"${CH[@]}"}
code=$?
wait $DRIVER   # headless runs may exit 139 after their result: read the RESULT line
grep -q "RESULT: ok" $OUT/carcd_driver.log || code=1
grep -q "RESULT: ok" $OUT/carcd_watch.log || code=1
grep -h "\[carcdcheck\]\|\[cd\]\|\[radio\]" $OUT/carcd_server.log $OUT/carcd_driver.log $OUT/carcd_watch.log
tc_stop
echo "[carcdcheck] exit $code"
exit $code
