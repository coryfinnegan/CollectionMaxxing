# Requesting work: feature requests and bugs as GitHub issues

This page is for an assistant that files work on the owner's behalf (or the owner, filing by hand). A GitHub issue written the way this page describes is picked up by the background issue agent, built, tested, reviewed by a second agent, merged and delivered to the phone or the PC, with no session needing to be open. It is the dependable path when Remote Control isn't. The agent runs on the owner's PC, so the PC has to be on with the owner signed in to Windows; an issue filed while it is off waits in the queue.

## The two rules that decide whether anything happens

1. **The issue must be opened by the owner's GitHub account** (`coryfinnegan`), in `coryfinnegan/CollectionMaxxing`. The repository is public, so issues from any other account are closed automatically and never worked. An assistant files issues with the owner's own GitHub login (the GitHub app, `gh`, or a connector signed in as the owner).
2. **It must carry the label `agent-task`.** That label is the queue. Without it the issue is a note, not work.

## Labels

| Label | Required | Meaning |
|---|---|---|
| `agent-task` | yes | Queue it for the background agent. |
| `type:feature`, `type:bug`, `type:infra` or `type:spike` | one of them | A new capability or a change to how something works; something that doesn't do what it should; tooling and machinery; a question to research and answer with a recommendation. |
| `p1` … `p5` | one of them (`p3` if unsure) | `p1` drop everything (the app is unusable or data is at risk), `p2` a real breakage or something needed soon, `p3` normal, `p4` polish, `p5` someday. The agent takes the lowest number first, then the oldest. |
| `tier:mechanical`, `tier:standard` or `tier:critical` | optional (`tier:standard` if left off) | How much model the work needs: a small, obvious change; normal work; or something risky or intricate (scanning and matching, data, security). |
| `sev:blocker` … `sev:trivial` | bugs only, optional | How bad the bug is. |

Never add `agent-working`, `agent-review`, `agent-blocked`, `needs:cory` or `status:blocked` yourself. The agent sets those, and some of them keep an issue out of the queue.

## Writing the issue

**Title:** the outcome, in a few plain words, starting with the area: `Library: sort by date added`, `Scanner: a game box with two discs adds both`, `Bug: the Wishlist pill shows on an empty wishlist`.

**Body:** the agent treats the issue text as the owner's own request and its brief. Keep it in the owner's words, and say what should be true, not how to build it:

```markdown
## What I want
One or two sentences, in the owner's words.

## Why
What it's for, or what goes wrong today.

## Where
The screen or flow: Library, an item, Scan, Review, Me, the web table…

## Done when
- Observable behaviour, one per line: "Tapping Sort shows Title, Date added, Year", "A wishlist item never counts in the library total".

## Not this
Anything the request must not change or include (optional).
```

For a bug, replace "What I want" with what happened, what was expected, and the steps to make it happen (device, screen, what was tapped).

**One request per issue.** Split a list of wishes into separate issues; each one gets its own PR and its own place in the queue.

**The repository is public.** Never put passwords, keys, tokens, email addresses, real hostnames, addresses or photos of the home in an issue. Describe a screenshot in words instead of attaching it.

## Example

From the GitHub app or website, **New issue → Feature request** or **Bug report** fills in these sections and adds `agent-task`, the type and a default priority (`p3` for features, `p2` for bugs; change it if it's wrong). From the command line:

```
gh issue create --repo coryfinnegan/CollectionMaxxing \
  --title "Library: sort by date added" \
  --label agent-task --label type:feature --label p3 \
  --body-file request.md
```

## What happens next

- Within about 5 minutes the agent picks the issue up: it adds `agent-working` and comments "Picked up at …". One issue runs at a time, by priority and then age, up to 8 a day.
- It builds the change test first, opens a pull request that says `Closes #<the issue>`, has a second agent review it, merges it when CI is green, and follows it until it reaches the phone (a TestFlight build) or the PC (a deploy). It comments the PR link and the delivery on the issue, and the merge closes the issue.
- **If it needs the owner** (a product decision, something unclear, anything that needs the owner's phone or accounts), it comments the question, adds `agent-blocked` and `needs:cory`, and stops. Answer in a comment, then remove `agent-blocked` and `needs:cory` (keep `agent-task`), and the next pass picks it up again with the answer.
- **If it fails** for a reason of its own, it adds `agent-blocked` with the last lines of its log; the same answer-and-relabel step puts it back in the queue.
- **If it can't start at all** (it stops within 5 minutes, usually an expired sign-in or a usage limit), the issue goes back in the queue by itself with a comment, and the agent waits an hour before trying again (longer if it keeps happening).

## What the agent will push back on

The agent follows the repo's standing rules and blocks with a question rather than break one: no prices or market values; recognition only on the iPhone with Apple's own frameworks; no new paid services; no modes or toggles where the app can decide for itself; nothing that weakens sign-in, privacy or the deploy checks. A request that needs one of those changed is a decision for the owner, and the issue says so.

## Related

- How the background agent runs, its limits and how to pause it: [deploy/README.md](../deploy/README.md) ("The issue agent").
- How the lead and the agents work together: [13-agent-orchestration.md](13-agent-orchestration.md) and [lead-master-prompt.md](lead-master-prompt.md).
