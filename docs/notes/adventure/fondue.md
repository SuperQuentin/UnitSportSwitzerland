# The shared pot: fondue and raclette (#1266)

`PotBook` (server memory, forgotten on a restart), `FondueRules`, `StirRules` (pure, tier 0). Client: `FondueTable` (caquelon, forks, the cheese shader with `split` and `bubble`), `FonduePanel`, `TogetherSounds`.

- **Spots** (`TogetherService.SpotNear`): a burning campfire (bring a `FonduePot`, used there: `TryStartAtFire`), a hut, a dining venue. The server reads the campfire from `PlacedSource` (`ServerWorld` hands it its `PlacedObjects`; `PlacedObjects.Instance` is client-only).
- **Seats**: the starter's party within 6 m (offered, E to sit), 2 to 8. `/fondue [start | raclette | sit | invite <p> | leave | forfeit 1|2|3]`.
- **Ingredients**: cheese (6 dips each), bread (your own, the one you can lose), `WhiteWine` 480 (Warm), `Kirsch` 481 (Hearty), potatoes for raclette. `AskAdd` takes the item first and the server gives it back on a refusal.
- **Stirring**: la huit. The panel samples the mouse, right stick or VR hand into `StirRules`; one `AskStir(q)` per eight; the server integrates the split meter S (1: the cheese splits, quality capped).
- **Dips**: `AskDip`, 2 s apart; a dip may lose your bread (`FondueRules.BreadLossChance`, `--fondueseed N` fixes the rolls). The loser gets a popup, 20 s: a line to the table, buy a round (CHF 5 each, `Bank.Charge`), or a yodel. `--forfeits off|mild|all`.
- **End**: everyone seated gets `Fed` (and Warm, Hearty) by `TellFeast` into `FootPlayer.BuffStamina`; leaving early gets nothing.
- Offline the same code runs with a local partner (`TogetherService.Pair`, `--fonduecheck`).
