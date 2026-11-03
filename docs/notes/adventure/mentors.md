# Mentors (#1266)

`MentorRules` (pure, tier 0): four milestones, `summit` (a first Peak or SummitCross), `glacier` (200 m roped on glacier cover), `ride` (2 km on a mount or vehicle of one's own), `build` (a structure of 10 pieces, `StructureBuilt`).

- Newcomer: never did it. Veteran: did it 3 times.
- It counts when the newcomer does it linked to the veteran or in their party within 60 m (`TogetherService.Milestone`, server). Both sections are written, then granted: the newcomer's first-time cosmetic (ItemId 482-485), the veteran's guide version the first time (486-489); titles "Bergführer" at 5 and "Bergführer mit eidg. Fachausweis" at 20 mentorships. All eight are `GarmentStyle.Together`, earned only.
- Once per pair and milestone; a newcomer is mentored once per milestone ever; session keys (`peer:`, `local`) never count on either side.
- Raises `Mentored(peer, key, milestone, role)`.
- Item ids of this plan are 480-499 (checked in `FondueRulesTests` and `TogetherProgressTests`).
- The net check seeds a veteran with `/rope test veteran` and triggers a milestone with `/rope test summit` (server flag `--together-test`).
