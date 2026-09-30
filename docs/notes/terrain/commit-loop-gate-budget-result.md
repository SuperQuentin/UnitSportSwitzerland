# The commit loop must gate on the budget the result actually needs

- **The commit loop must gate on the budget the result actually needs.** It read
  `while (meshBudget > 0 && collisionBudget > 0 ...)`, so the single allowed collision commit
  ended the loop for that frame with the mesh budget untouched — throttling commits to about one
  tile per frame exactly when tiles arrived fastest. Peek first, and break only on the budget
  that result requires.
