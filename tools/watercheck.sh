#!/usr/bin/env bash
# The water over loopback (#299): a headless dedicated server on the lake fixture course started
# gamey (--sea-state gamey), and a headless client on the same course that must get the sea state
# on join, see the same wave surface as the server at the server's wave time (/water E N, to the
# millimetre, three points), and get an admin's /seastate storm. No terrain data needed.
. "$(dirname "$0")/lib/twoclient.sh" watercheck
PORT=${WATER_PORT:-7841}

WORLD="--world fixture --chunks fixture:lake"
tc_server 1800 120 $OUT/watercheck_server.log --server --port $PORT $WORLD --sea-state gamey --admin-password test
tc_client 240 $OUT/watercheck_client.log --connect 127.0.0.1:$PORT --name Swimmer $WORLD --watercheck net
code=$?
grep -h "\[watercheck\]" $OUT/watercheck_client.log
grep -h "sea state\|\[admin\]" $OUT/watercheck_server.log
grep -q "RESULT: ok" $OUT/watercheck_client.log && code=0 || code=1
echo "[watercheck net] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
