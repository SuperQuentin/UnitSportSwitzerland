#!/bin/zsh
# usage: perf.sh <tag> <style> [game flags...]   (FWD=1 for Forward+)
source ${0:a:h}/lv95.sh
p() { w $1 $2 | awk -v y=$3 '{print $1","y","$2}'; }
tag=$1; style=$2; shift 2
d=test_output/styles/perf; mkdir -p $d
q=$d/q_$tag.txt
{ [[ -n $FWD ]] && echo "$(p 2583250 1112800 900),-35,0,40,$d/${tag}_warmup.png"; } > $q
cat >> $q <<Q
$(p 2583100 1112600 700),-8,300,30,$d/${tag}_valley.png
$(p 2583400 1113100 g2),-3,45,10,$d/${tag}_street.png
$(p 2583250 1113000 g12),-10,180,10,$d/${tag}_chase.png
quit
Q
eng=(); [[ -n $FWD ]] && eng=(--rendering-method forward_plus)
godot --always-on-top $eng --path . -- --shot-queue $q --origin 2590000,1116500 --nohud --nocapture --time 14 --style $style --title "181 perf $tag" "$@" > $d/$tag.log 2>&1
echo "$tag [$(grep -o 'origin LV95 [0-9/]*' $d/$tag.log | head -1)]: $(grep -o 'frame=[0-9.]*ms' $d/$tag.log | tr '\n' ' ') $(grep -o 'prims=[0-9]*' $d/$tag.log | tr '\n' ' ')"
