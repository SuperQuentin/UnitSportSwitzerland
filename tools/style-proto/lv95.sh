# source me: w E N -> "x z" in world coordinates, origin pinned to Riddes by --origin
OX=2590000; ON=1116500
w() { echo "$(( $1 - OX ))" "$(( ON - $2 ))"; }
