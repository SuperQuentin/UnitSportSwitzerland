# The "to be tested by a human" list

`tests/TO-TEST-BY-HUMAN.md`: what a change could not verify by itself, kept where the owner finds it.

## Rule

- A PR that leaves something unverified adds lines to the list **in the same PR**, under a heading for its feature with the PR link. That covers:
  - a click no check drives;
  - a real pad or a headset;
  - a real-size region (the dev region, a first boot without caches);
  - a look or a feel to judge.
- Each line is `- [ ] what to do → what should happen`, with any launch flags needed to get there.
- The PR's "Not verified" section points at those lines rather than repeating them.
- When a line is tested: delete it. If it failed, open an issue and leave the line with the link until the fix lands.
- A heading with no lines left goes too.
- A check that should run again after every change is a playtest scenario (`general/playtest`), not a line here.

## Why

The owner asked for one place for these follow-ups (#1277, Oct 2026): until then they lived only in PR bodies and chat replies, and were lost once the PR merged.
