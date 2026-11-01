# Linked evidence and claim verification smoke test

The new workflow is implemented, but this test did not establish an accuracy improvement. Independent review rated one of four answers fully correct and three partly correct, the same counts as the preceding dated-evidence smoke test.

## Changes tested

- Search follows up to eight exact ticket IDs, repository-qualified GitHub PR or issue URLs, and explicitly named branches across sessions. At most three result slots contain later linked sessions. Ordinary searches retain the strongest original source.
- Results label evaluation and memory-tool-development sessions using explicit title or opening-request markers. Ordinary questions demote these sessions; explicit questions about them can include them. These labels are heuristics and do not change saved exclusions.
- `verify <claims-json-file>` checks up to 12 cited claims against source messages and returns neighboring context within a shared 12,000-character source-text budget. Input is limited to 64 KiB. Quote matching accepts the console's text normalization.
- Both Windows and Unix assistant instructions require a separate meaning review after `verify`. A matching quote is not evidence that the proposed claim follows from it. The command reports `semantic_support: not_assessed` and `review_required: true`.

The command runs locally without a new model dependency. The calling assistant performs the meaning review. This adds an instruction and a tool step, not a guarantee that every client will follow them correctly.

## End-to-end results

Each question used one fresh Copilot CLI session with Claude Sonnet 5.5, high effort and long context, against the same frozen private history used by the earlier tests. All four completed, used `verify`, had full tool and path permissions, and recorded zero permission denials.

| Question | Runtime | Total tokens | Uncached input + output | Independent review |
| --- | ---: | ---: | ---: | --- |
| Latest project status | 53.7 s | 155,615 | 38,746 | Partly correct: missed later documentation and misordered merge/QA events |
| Original hosting bug fix | 60.5 s | 235,685 | 47,143 | Correct: distinguished the local fix, later branch evidence and unknown publication |
| Latest technical component work | 60.6 s | 181,700 | 42,833 | Partly correct: missed later eligible investigations |
| Latest component security work | 48.1 s | 136,379 | 34,238 | Partly correct: missed relevant October evidence and publication evidence |

Mean runtime was 55.7 seconds; mean total tokens were 177,345, including cached input; mean uncached input plus output was 40,740. These figures include three recovered attempts to use an unavailable file-creation tool. The assistant subsequently wrote its draft through PowerShell and completed verification. They describe this workflow's observed cost, not a controlled savings comparison or billing estimate. Four runs do not establish a general accuracy rate.

## Supplied-evidence diagnostic

A separate control supplied short source packets directly to four fresh sessions, with tools disabled. Independent review rated two answers correct and two partly correct. One answer wrongly dismissed an eligible technical audit; another attributed a separately supplied repository excerpt to a session message. These errors show that interpretation and citation attribution can fail even when relevant evidence is supplied.

The first project-status and security-work packets omitted required context. Those two initial runs were marked invalid and retained separately. The diagnostic counts use the corrected packets and do not mix the invalid runs into the result.

## Validation and limits

The main build and isolated release build passed self-tests. Checks cover exact identifier linking, repository-qualified references, source-purpose demotion, exclusions, malformed claim input, citation matching, neighboring context and rendered text limits. Corpus-file reparse points are rejected before reading. The symlink regression could not execute because this Windows environment did not permit creating a symlink; it reported that skip explicitly. PowerShell parsing, Unix installer syntax and `git diff --check` passed. No installer was run.

The four model trials preceded the final source-link and symbol-expansion hardening. The final isolated build passed deterministic regression tests after those changes; the four model calls were not repeated for them.

The test used a private server port and isolated client state. The installed app and live exclusion file were not changed. Private packets, answers, accounting and grades remain local; this document does not publish source transcripts.

Remaining weaknesses include source selection, ambiguous question scope, interpreting status and reconciling later updates. Identifier linking is one hop and cannot connect unrelated wording without a shared supported reference. A citation check can reject a bad quote but cannot establish factual support or completeness. The [earlier benchmark](2026-10-30-history-retrieval.md) and [dated-evidence smoke test](dated-evidence-smoke.md) remain separate measurements.
