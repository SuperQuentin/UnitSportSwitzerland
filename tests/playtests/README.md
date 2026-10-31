# Playtest ledger

One JSON file per playtest scenario (`<id>.json`): the last verdict a person gave by playing it, who, when, on
which commit, and the hash of the files the scenario covers. Written by the game's playtest panel; commit them
with the work they judged. `tools/playtest.sh pending` lists the scenarios due. See docs/notes/general/playtest.md.

The Adventure phase 3 set (`adv3-*`) lives in `src/Playtest/Scenarios/Adventure3Scenarios.cs`; see the end of the "Write a scenario" section of the playtest note.
