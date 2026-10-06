# Plans live on main, their status updates ride the branch

- A plan in `docs/plans/` is committed on `main`, even when it is the design document for a
  feature that will get its own issue, branch and worktree. Commit it as a docs-only commit in the
  main checkout, like any simple task.
- Why: step 1 of the feature workflow is to search issues and PRs for overlapping work. A plan
  sitting on an unmerged feature branch is invisible to that search, and it dies with the branch
  if the work is abandoned. With ~15 worktrees open at once, the up-front plan is the cheapest
  coordination point there is.
- Once the feature branch exists, **updates** to its plan ride that branch and arrive with the PR:
  phase status, what was built differently from the plan, follow-ups moved to another issue. That
  is already what happened for `generated-terrain-fill` (#27) and `urban-streets` (#119).
- Committing the plan on `main` is never a reason to put the code it describes there too. The plan
  is the simple task; the feature it describes still goes through issue -> branch -> worktree -> PR.
- Measured before this note existed (Oct 2026): 5 of 7 committed plans were added straight on
  `main`, always as the first commit for their issue, 2-3.5 h ahead of the first code commit
  (#185 plan 18:50 / code 22:20; #181 plan 19:47 / prototype 21:54). The two that arrived on a
  branch came inside the feature's own `:sparkles:` commit, written as built. No merge ever had the
  same plan file edited on both sides, so this has cost zero conflicts.
