# Chronological retrieval smoke test

The chronological search and passage-level qualification changes are implemented. Targeted regression tests pass, but the four-question agent test still produced one fully correct answer and three partly correct answers. This test does not establish an accuracy improvement.

## What changed

`search --latest` checks the readable, allowed corpus for matching messages. Qualifying evidence competes by source-message date, without reserved slots for older semantic matches or linked sessions. Ordinary searches retain their relevance ranking and original-fix behavior. Evaluation sessions, explicit brief mentions, CLI query examples and standard historical conversation summaries rank lower and remain available when slots remain. Chronology applies within those priorities.

Topic matching is separate from activity qualification. A multi-term lexical match needs the topic and another positive query term in the same sentence or paragraph. The topic heuristic prefers the first acronym or CamelCase identifier in the question, or the first positive term present in the corpus. Source-matched LEANN excerpts can also qualify with topic support or as paraphrases with no literal query overlap. Generic qualifier-only matches cannot qualify through that semantic path.

An excluded concept such as "not threat modeling" is checked per sentence or paragraph. A separate technical audit in the same message can remain eligible. Wrapped lines stay together, and an activity explicitly saying it was not threat modeling is retained. Omitted passages have visible ellipses, the JSON field `excluded_passages_omitted`, and a text notice directing the assistant to the full source. These rules are lexical heuristics, not proof of meaning or completeness.

Windows and Unix assistant instructions and the README describe the new behavior. These changes introduce no dependency, model call or index rebuild in the search implementation. The existing assistant verification workflow can still make additional searches and model calls.

## Four-question results

The final isolated build ran each question once in a fresh Copilot CLI session, using Claude Sonnet 5.5, high effort, long context and the same frozen private history as the earlier tests. Each run called `verify`. All four completed with accounting, zero permission denials and no client-state isolation failures.

| Question | Runtime | Total tokens | Uncached input + output | Final source review |
| --- | ---: | ---: | ---: | --- |
| Latest project status | 62.5 s | 246,514 | 34,511 | Partly correct: useful cleanup status, but missed later documentation and a merged fix |
| Original hosting bug fix | 61.8 s | 270,374 | 47,847 | Correct: separated the original local fix, later branch evidence and unknown publication |
| Latest technical component work | 62.5 s | 185,379 | 40,811 | Partly correct: central latest-work citation came from an explicitly excluded comparison report |
| Latest component security work | 66.2 s | 315,129 | 40,795 | Partly correct: relevant model work, but missed a later documentation publication and reply |

Mean runtime was 63.3 seconds. Mean total tokens were 254,349; mean uncached input plus output was 40,991. Total tokens include cached input. Uncached tokens are input minus cache reads plus output; they are not a billing estimate.

Three runs recovered from an attempted call to an unavailable file-creation tool. One also retried verification after first referencing a missing draft. These attempts remain in the reported runtime and usage. Every run subsequently produced verification output. Quote presence checks do not establish semantic support.

The initial review accepted the two component answers. An independent review checked the explicit benchmark exclusion and later dated source messages and found material omissions. The first reviewer then revised those grades to partly correct. Both original and supplemental reviews are retained. The comparison-session issue does not mean that every separate diagnostic session on that date is excluded.

## Validation and limits

The main build and final isolated release build passed self-tests. New regressions cover newer qualifying evidence beating older semantic rank and rare secondary terms, semantic paraphrases alongside literal candidates, original-fix preservation, mixed-topic exclusions, wrapped negative phrases, source negation, positive same-passage qualification, short actual updates, historical summaries, query examples, linked updates with one result slot, retained demoted results, citation identity and rendered output limits. The existing exclusion and source-path checks remain in place.

The symlink regression reported a skip because Windows did not permit creating a symlink. PowerShell parsing, Git Bash installer syntax and `git diff --check` passed. No installer was run. The four direct search outputs were between 7,832 and 8,049 characters, below the 12,000-character limit. Earlier direct-search snapshots are retained separately; only the final source snapshot supplied the four agent trials.

The test build used a private server port and isolated Copilot state. Its four session IDs were absent from the default Copilot database and session folders. Live exclusions were unchanged, and the installed app was not updated. Source packets, traces and grades remain private local artifacts.

These are targeted questions used during development, not a held-out evaluation. The unchanged fully-correct count is evidence against claiming a demonstrated accuracy gain. Time and total token usage were higher than in the preceding smoke test; this small unpaired comparison cannot isolate the cause or establish a general cost change. Source selection, indirect project references, question scope and reconciling later updates remain unresolved.

See the separate [verification smoke test](evidence-verification-smoke.md), [dated-evidence smoke test](dated-evidence-smoke.md) and [earlier benchmark](2026-10-30-history-retrieval.md). Their measurements describe their own retrieval versions.
