# A facing look-ahead must be bounded by DISTANCE, not just time

- **A facing look-ahead must be bounded by DISTANCE, not just time.** `HeadingLookahead` is 2.5 s
  either side, which is ~17 m on foot and 60-100 m on a bike. That is fine for outrunning GPS
  jitter on a raw recording and wrong on a road-matched one, which has real corners: a hairpin is
  chorded straight across, and on a switchback the two samples land on opposite legs so the
  difference collapses toward the degenerate guard and the heading **freezes**. Bounded now by
  `MaxHeadingChordM`. The facing slerp also has to be clock-scaled like the position follow next
  to it, or at 8x the body keeps up with the course while its heading lags eight times as far
  behind every corner.
