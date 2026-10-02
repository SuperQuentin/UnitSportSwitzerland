#!/bin/zsh
# Screenshots and frame times at fixed viewpoints around Riddes (VS), for comparing visual
# styles, or a branch against main (issue #212). One game launch per run, --shot-queue.
#
#   tools/style-shots.sh <tag> [game flags...]     e.g. tools/style-shots.sh ps1 --style ps1
#
# Run from the checkout to test. Writes test_output/styles/<tag>_<view>.png and <tag>.log, then
# prints each shot's frame time and primitives. Needs the Riddes tiles (2582-2583 / 1112-1113).
# The origin is pinned with --origin, so the views do not move when the manifest's suggested
# origin changes. Env: TIME=hour (default 14), SETTLE=seconds per shot (default 8),
# ENGINE="godot engine args" (e.g. "--rendering-method forward_plus"), VIEWS="street top" to
# take only those (the warm-up shot is always taken). The interior goes last: it leaves its
# front door open.
# A run whose frame times are a flat ~6.9 ms with repeated primitive counts drew nothing new
# (the macOS window stopped presenting): discard it and run again.
set -e
tag=$1; shift
d=test_output/styles; mkdir -p $d
OE=2590000; ON=1116500
# LV95 E N + y (metres, g<m> above the ground, or i<m>: through the door in front, into the
# house, docs/notes/core/commands.md) -> x,y,z in world coordinates
p() { echo "$(( $1 - OE )),$3,$(( ON - $2 ))"; }
s=${SETTLE:-8}
q=$d/q_$tag.txt
cat > $q <<Q
$(p 2583250 1112800 900),-35,0,$s,$d/${tag}_warmup.png
$(p 2583250 1112800 900),-35,0,$s,$d/${tag}_aerial.png
$(p 2583250 1112900 560),-12,0,$s,$d/${tag}_low.png
$(p 2583400 1113100 g2),-3,45,$s,$d/${tag}_street.png
$(p 2583395 1113109 g1.7),-3,159,$s,$d/${tag}_house.png
$(p 2583250 1113000 g12),-10,180,$s,$d/${tag}_chase.png
$(p 2583100 1112600 700),-8,300,$s,$d/${tag}_valley.png
$(p 2583100 1112600 1400),-70,300,$s,$d/${tag}_top.png
$(p 2583390 1113097 i1.6),-8,192,$s,$d/${tag}_interior.png
quit
Q
if [[ -n $VIEWS ]]; then
    keep="_warmup.png|quit$(for v in ${=VIEWS}; do printf '|_%s.png' $v; done)"
    grep -E "($keep)" $q > $q.tmp && mv $q.tmp $q
fi
# a fixed window size and place: the size is the frame being timed. On macOS Godot skips drawing
# while its window is covered (another game window in the middle of the screen), hence on top
# there; never on Windows, where it stays above the person's work (core/windows-launch-focus)
ontop=(); [[ $OSTYPE == darwin* ]] && ontop=(--always-on-top)
godot $ontop --resolution 1152x648 --position 24,48 ${=ENGINE} --path . -- --shot-queue $q --origin $OE,$ON --nohud --nocapture \
    --time ${TIME:-14} --title "212 $tag" "$@" > $d/$tag.log 2>&1 || echo "exit $?"
grep -E '\[shot\] (wrote|FAILED)' $d/$tag.log | sed -E 's/.*(wrote|FAILED[^ ]*) ([^ ]+).*/\1 \2/' > $d/$tag.names
grep -E '\[shot\] fps' $d/$tag.log | sed -E 's/.*(prims=[0-9]+).*(frame=[0-9.]+ms).*/\2 \1/' | paste $d/$tag.names - | column -t
grep -E 'SHADER ERROR|ERROR:' $d/$tag.log | head -5 || true
# the window changing size (macOS does, now and then) invalidates the pictures
sizes=$(grep -oE '\[shot\] wrote .* \([0-9]+x[0-9]+\)' $d/$tag.log | grep -oE '[0-9]+x[0-9]+' | sort -u)
[[ $sizes != 1152x648 ]] && echo "WARNING: pictures not all 1152x648 ($(echo $sizes | tr '\n' ' ')): run again"
true
