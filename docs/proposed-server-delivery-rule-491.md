# Proposed server-delivery rule (#491)

**Status:** proposal for the lead to adopt as a prompt amendment. Prompt changes go through the
amendment process (`docs/lead-master-prompt.md`, "This prompt"): the lead files an amendment issue
carrying the replacement text below, works it like any other issue, and operates under it from the
next tick. This file is the source text for that issue; it is not itself the rule.

**Why:** on 2026-10-27 the owner reproduced two bugs closed that day as fixed (#450, #452) and found
both unchanged. Both fixes were substantially server-side. CI had built and published new server
images ("Release images SUCCESS"), but nothing in the delivery definition verified the server
actually RUNNING on the owner's PC — the only server he has — was on the new code. Worse, the
deployer had been silently refusing every deploy for hours (an attestation-selection bug, fixed in
the same PR as this proposal), and with no heartbeat URL set the only alert was a local log line.
"Release images SUCCESS" proves an image exists in a registry. It does not prove the owner's
server restarted onto it.

## Replacement text

In `docs/lead-master-prompt.md`, "How work gets done", replace this bullet:

> - Server work: ALSO the "Release images" run for the merge commit must conclude SUCCESS.

with:

> - Server work (any merge that changes what the server runs: `src/**`, `deploy/**`,
>   `contracts/**`, `.github/workflows/release.yml`): ALSO the "Release images" run for the merge
>   commit must conclude SUCCESS, AND the owner's running server must report the merge commit (or a
>   newer one) before the issue closes. "Release images SUCCESS" proves an image exists in a
>   registry; it does not prove his server restarted onto it. Until the running server reports the
>   commit, the issue stays open with a "merged, awaiting deploy" comment — merged is not delivered.
>   If the server does not pick the commit up, file or claim the delivery-blocker bug immediately
>   (rule 2) and link it on the issue. Docs-only server changes (no runnable file touched) are
>   exempt from the running-server half of this rule.

And append to that section:

> ### Running-server evidence
>
> - Read the commit the server reports: `Invoke-RestMethod http://127.0.0.1:5480/health/version`
>   (`sha` and `shortSha`; anonymous, outside the OpenAPI document). It must equal the merge commit
>   or a descendant of it (`git merge-base --is-ancestor <merge> <reported>`).
> - Allow the pipeline its time: "Release images" takes about 4-5 minutes after the merge, then the
>   PC's deployer polls every 2 minutes. Inside about 10 minutes of CI success the endpoint should
>   report the commit. Waiting on this evidence is a zero-token watch, never a subagent.
> - The issue closes on this evidence: the closing comment quotes the reported `sha`/`shortSha` and
>   when the deployer logged the swap (`deployed <12 hex>` in `C:\CollectionMaxxing\logs\deployer.log`).
> - When the endpoint keeps reporting an older commit past that window, the delivery path is broken:
>   read the tail of `deployer.log` for the refusal reason (`ALERT deploy ...`), quote it on the
>   issue, and treat it as a delivery blocker (Order of work, rule 2). A refusal is never closed as
>   "merged, awaiting deploy" — it is worked until the server reports the commit.
> - The deployer's own running-version check (it pins the previous good version when the started api
>   reports the wrong build) is a safety net, not the evidence: the evidence is always a fresh read
>   of the endpoint above.

## Notes for the amendment issue

- Follow-up the lead should file separately: set `CM_HEARTBEAT_URL` in the PC's `.env` so deploy
  refusals email instead of sitting in a local log (needs the owner's healthchecks.io account, so
  it may carry `needs:cory` with exactly what he must do).
- The retroactive check (#491, point 4) is posted on the issue itself, not here.
