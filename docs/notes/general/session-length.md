# Session length (token cost)

Every turn re-sends the whole conversation, so a turn costs in proportion to the context it
carries. Measured on this project (Jul-Nov 2026, 15.2 B tokens, ~$6,000 at API prices): requests
above 400k context were 53% of the cost, requests under 200k only 15%; the five longest sessions
(550k-970k context, 1,300-4,000 requests each) were 36%. Cache reads alone were 56% of the bill.

- **One session per task.** When a feature is merged or the subject changes, start a new session
  instead of continuing. What the next task needs belongs in the issue, the PR or a note, not in
  the old conversation.
- **Compact around 200k.** A task that genuinely needs a long session runs `/compact` before the
  context passes ~200-300k, never lets it run toward 1M: a turn at 800k costs about 4x one at 200k.
