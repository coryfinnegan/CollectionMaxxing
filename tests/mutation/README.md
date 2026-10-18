# Mutation sweep

How well do the Domain and Application tests catch mistakes? `scripts/mutate.ps1` answers that by
making small deliberate mistakes in the code (mutants) and checking that a test fails for each one.

## Run it

```powershell
pwsh scripts/mutate.ps1 -Project Domain -Sample 150 -Seed 1
pwsh scripts/mutate.ps1 -Project Domain -Path src/CollectionMaxxing.Domain/Matching          # one folder or file
pwsh scripts/mutate.ps1 -Project Application -Path src/CollectionMaxxing.Application/Items -Sample 40
```

- The git tree must be clean. The script edits source files one mutant at a time and puts every one back in a
  `finally` block (also on Ctrl+C). Do not edit or build the repo in the same worktree while it runs; use another worktree.
- It runs at below-normal priority, so it will not starve the PC. Each mutant is one `dotnet test` (build plus tests).
- Domain needs no Docker. Application's tests start a throwaway Postgres in Docker, so run it one folder at a
  time with a small `-Sample`. `Application/Evals` is never mutated.
- `-Seed S` makes the sample repeatable: the same seed on the same code picks the same mutants. Without it a
  random seed is chosen and printed on the `baseline green` line.
- `-TestFilter "FullyQualifiedName~Matching"` runs only part of the tests for each mutant (faster, but a survivor
  may then be a test that lives outside the filter).
- Output: one `SURVIVED file:line [operator]  before  =>  after` line per mutant the tests did not catch, then
  `killed / total = score`. A mutant that does not compile is "not a mutant" and is left out of the score; a
  mutant that times out counts as killed (the tests noticed something).

The Domain target is a score of 80% or more (`docs/09-testing-and-tdd.md`, "Quality gates").

## Operators

Line-level swaps in code only (never comments, strings, characters, attributes, `using`, `namespace` or `#` lines):

| Operator | Change |
|---|---|
| `eq` | `==` to `!=` and back |
| `rel` | `<` to `<=`, `<=` to `<`, `>` to `>=`, `>=` to `>` (only when written with spaces around) |
| `logic` | `&&` to `\|\|` and back |
| `arith` | binary `+` to `-` and back |
| `bool` | `true` to `false` and back |
| `return` | `return x;` to `return default;` (not when x already is false, null or zero: that would be the same code) |
| `guard` | `if (cond) return/throw ...;` on one line: the condition becomes `false` (the guard is removed) |
| `linq` | `.Any(` to `.All(` and back |
| `offbyone` | the number in a `.Length` / `.Count` comparison goes up by one, and down by one if it is above zero |

## Use it as a verifier

Instead of "break 3 rules by hand": run the sweep over the folder the PR changed and read the survivors.

```powershell
pwsh scripts/mutate.ps1 -Project Domain -Path src/CollectionMaxxing.Domain/<changed folder> -Sample 40
```

A survivor is a real change to the code that no test noticed. For each one in code the PR touched, either say
which behaviour is untested (send it back to the builder) or mark it equivalent (below). Put the score and the
survivors in the verifier report.

## Mark a mutant equivalent

Some mutants do not change behaviour (for example `i < n` to `i <= n` where the last step is harmless). Read the
survivor line, decide it truly cannot change any result, and add an entry to `tests/mutation/equivalents.json`:

```json
[
  { "file": "src/CollectionMaxxing.Domain/Identifiers/Gtin.cs", "line": 26, "operator": "rel", "reason": "why no input can tell the two apart" }
]
```

An entry matches by `file` + `line` + `operator` and hides every mutant of that operator on that line. The sweep skips
them and reports `N equivalent skipped`. If the line moves, the entry stops matching (and the mutant shows up again),
so update the line number in the same PR. Keep the reason honest: "the test is hard to write" is not a reason.

## The script's own tests

```powershell
pwsh -NoProfile -File scripts/mutation/tests.ps1
```

They cover the operators, skipping comments and strings, the seeded sample, equivalents, the build-error and timeout
accounting, and restoring files after a normal, a failing and a cancelled run (a real cancel of a running pipeline).

## Stryker.NET: checked, does not work here (2026-10-17)

Tried `dotnet-stryker` 5.0.0 as a local tool (`dotnet new tool-manifest`, `dotnet tool install --local dotnet-stryker`)
on `tests/CollectionMaxxing.Domain.Tests`, with `--project CollectionMaxxing.Domain.csproj`.

- The test projects are pinned to xUnit v3's VSTest flavour (`xunit.v3.mtp-off`), so Stryker does discover and start
  the tests (335 found).
- But it cannot attribute anything: the initial run reports `.`, the coverage capture logs "It looks like the test
  coverage capture failed" and "No mutant covered so far" for every test, and every mutant ends as Survived (score
  0.00% on a file the tests exercise heavily). The xUnit v3 test project is an executable that is not driven through
  Stryker's in-process mutant switch, so the mutated assembly is never exercised.
- Second run with the same result, so it was not a one-off. Switching xUnit to its Microsoft.Testing.Platform flavour is
  already known not to work on this SDK (`tests/Directory.Build.targets`).

So `scripts/mutate.ps1` is the tool, and there is no `-Engine stryker`. Retry when Stryker gets Microsoft.Testing.Platform /
xUnit v3 support or the repo can use `xunit.v3` (MTP) on the .NET 10 SDK.
