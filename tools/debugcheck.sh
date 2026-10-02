#!/usr/bin/env bash
# The debug menu's admin rule over loopback (#339): a headless dedicated server with an admin
# password on a generated world, and one headless client that must be refused the menu, get it
# after /login, and lose it (every tool off) after /admin remove. No terrain data needed.
#   tools/debugcheck.sh          (GODOT = the editor executable, docs/notes/general/godot-exe.md)
. "$(dirname "$0")/lib/twoclient.sh" debug
PORT=${DEBUG_PORT:-7841}
tc_server 400 120 $OUT/debugcheck_server.log --server --port $PORT --generated-world --admin-password debugcheck
tc_client 300 $OUT/debugcheck_client.log --connect 127.0.0.1:$PORT --name Debugger --debugcheck --traffic 0
tc_stop
code=0
grep -q "RESULT: ok" $OUT/debugcheck_client.log || code=1
grep -h "\[debugcheck\]" $OUT/debugcheck_client.log
grep -h "\[admin\]" $OUT/debugcheck_server.log
echo "[debugcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
