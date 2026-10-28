# Together: the rule, the group yield and the week (#1266)

Plan: `docs/plans/adventure-together.md`. Code: `src/Adventure/Together/`. Every server method checks `AdventureService.Active(peer)`, so none of it runs in a Battle Royale lobby or round.

## The rule

- `TogetherRules` (pure): a party member, adventuring, not down, within 60 m (40 m vertical), at most 3 counted. `Radius` is also `Seals.Coop`'s.
- Server: `TogetherService.Near(peer)`. Client: `NearHere`, refreshed at 1 Hz from the party and the members' replicated bodies, shown as the "Together ×n" chip (`TogetherChip`).
- Party members only: a party is joined by consent. Bots are never in parties.

## The group yield

`TogetherRules.Yield(base, n, u)` = floor(base × (1 + 0.25 n) + u), stochastic rounding so the mean is exact. It is applied by the owner (the inventory is local):

- `Loot/Gathering` (firewood, stone, dead wood, water) and a new rare find, `Bergkristall` (stone above 1 800 m);
- `HandFarming.HarvestAt`;
- fishing: `FishRules.MeanBiteSeconds(together:)` and the rare weights;
- quest cash: `AdventureService.Grant` adds +10 % a friend on the server, except for `pk:` ids (errands pay their own bonus).

Chests, edelweiss, cairns and trials are untouched.

## The week and the punch card

`TogetherProgress` (section `together`, v1): seconds together (200 m, `PlayRadius`) per day and per ISO week (Europe/Zurich). 30 min in a week: one Fine chest roll. 20 min in a day: a stamp (one a day, never reset); 10 stamps: a card, a Precious roll; cards 1, 3, 5, 10 are titles, card 10 also the `Stammgast` hat. `--together-day-seconds N` shortens the day for checks. Writes are batched once a minute and flushed before every save (`AdventureService.Saving`). The Journal's Collection tab has a "Together" page (`TogetherPage`).

## Checks

- Tier 0: `TogetherRulesTests`, `TogetherProgressTests`, `CrevassesTests`, and the others named in the other notes.
- Quick: `--togethercheck --world fixture --systems ui,terrain,physics,adventure --rewardspots res://tools/lib/together_spots.txt` and `--fonduecheck ...` (see `links`, `fondue`).
- Net: `tools/togethernetcheck.sh` (Lea and Tom): the party, hands seen on the remote peer, the follow with no input, a fondue at a campfire, the forfeit line on the other peer, a seeded mentor, and a Battle Royale lobby breaking the link. `--together-test` on that server opens `/rope test veteran` and `/rope test <milestone>`.
