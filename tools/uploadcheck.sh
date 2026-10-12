#!/usr/bin/env bash
# A CD from a player's own file over loopback (#736): a headless dedicated server on the flat
# fixture and a headless client that uploads a generated song as a shared CD. The server must scan
# it with its antivirus (ClamAV on Linux, Windows Defender on Windows; none and the check fails, as
# uploads are then refused) and burn it into the shared list; a second upload straight after is
# refused (one a minute). ffmpeg on PATH (or in bin/).
#   tools/uploadcheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" upload
PORT=${UPLOAD_PORT:-7862}
WORLD="--world fixture"
SONG="$PWD/$OUT/uploadfixture.mp3"
SECOND="$PWD/$OUT/uploadsecond.wav"
ffmpeg -y -loglevel error -f lavfi -i "sine=f=523:b=4:d=30" -ac 1 -ar 22050 "$SONG" || { echo "ffmpeg is needed on PATH"; exit 1; }
ffmpeg -y -loglevel error -f lavfi -i "sine=f=392:b=4:d=10" -ac 1 -ar 22050 "$SECOND"
tc_server 240 120 $OUT/upload_server.log --server --port $PORT $WORLD
tc_client 220 $OUT/upload_client.log --connect 127.0.0.1:$PORT --name Uploader $WORLD --uploadcheck "$SONG" "$SECOND"
tc_stop
grep -h "\[uploadcheck\]\|\[cd\]" $OUT/upload_server.log $OUT/upload_client.log
code=0
tc_ok 1 $OUT/upload_client.log || code=1
echo "[uploadcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
