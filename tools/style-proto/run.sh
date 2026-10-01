#!/bin/zsh
# usage: run.sh <style> <tag> [engine args before --]; TIME=hour, EXTRA="game flags"
source ${0:a:h}/lv95.sh
p() { w $1 $2 | awk -v y=$3 '{print $1","y","$2}'; }   # LV95 E N + height -> x,y,z
style=$1; tag=$2; shift 2
d=test_output/styles
q=$d/q_$tag.txt
cat > $q <<Q
$(p 2583250 1112800 900),-35,0,30,$d/${tag}_warmup.png
$(p 2583250 1112800 900),-35,0,5,$d/${tag}_aerial.png
$(p 2583250 1112900 560),-12,0,8,$d/${tag}_low.png
$(p 2583400 1113100 g2),-3,45,6,$d/${tag}_street.png
$(p 2583100 1113400 g2),-3,300,5,$d/${tag}_house.png
$(p 2583250 1113000 g12),-10,180,5,$d/${tag}_chase.png
$(p 2583100 1112600 700),-8,300,5,$d/${tag}_valley.png
quit
Q
godot --always-on-top "$@" --path . -- --shot-queue $q --origin 2590000,1116500 --nohud --nocapture --time ${TIME:-14} --style $style --title "181 $tag" ${=EXTRA} > $d/$tag.log 2>&1
echo "exit $?"
grep -E '\[shot\] fps|\[style\]|origin LV95|SHADER ERROR' $d/$tag.log | head -20
