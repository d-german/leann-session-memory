# Dated-evidence retrieval smoke test

This was a small verification of the new retrieval path, using one fresh agent session per question. It was not a repeat of the full benchmark and does not establish a new general accuracy rate.

The final build completed all four runs without permission denials. Independent source-based review rated one answer fully correct and three partly correct.

| Question type | Runtime | Total tokens | Uncached input + output | Review |
| --- | ---: | ---: | ---: | --- |
| Latest project status | 41.9 s | 69,428 | 20,794 | Partly correct: missed later work and a merged fix |
| Hosting bug's original fix | 35.8 s | 100,178 | 26,690 | Correct: separated the local fix, later merge and unknown publication |
| Latest technical component work | 30.0 s | 68,302 | 19,407 | Partly correct: found older work but missed later eligible investigations |
| Latest component security work | 25.5 s | 75,669 | 22,362 | Partly correct: relevant October evidence, with inaccurate coverage and recency claims |

Mean runtime was 33.3 seconds. Mean total tokens were 78,394; mean uncached input plus output was 22,313. These four runs are too few, and differ from the earlier benchmark setup, to support a controlled percentage comparison.

## What changed

- Searches consider more distinct sessions and locally inspect query-relevant original messages in the allowed corpus.
- `search --latest` selects by relevant-message dates, separately from session last activity. It balances strong semantic evidence with newer matching evidence.
- Ordinary searches retain semantic fallback and original-fix provenance.
- Source excerpts include message identifiers, roles and timestamps, with adjacent context. Human-readable search output has a 12,000-character budget; JSON contains additional metadata.
- Explicit exclusions in a query are handled conservatively. The parser supports bounded phrases, not arbitrary natural-language negation.
- Assistant instructions on Windows and Unix require dated citations, checking follow-ups, and distinguishing proposals, local patches, merges, publication and verification.

Regression checks cover newer corrections in the same session, irrelevant later activity, missing semantic candidates, paraphrases, negative clauses, source exclusions, diversity and text limits. Build and self-tests passed. Direct retrieval checks on the frozen corpus took roughly 3 seconds per query and returned eight sessions within the text budget.

## Scope and remaining limitations

The test used the same frozen private history, Copilot CLI, Claude Sonnet 5.5, high effort and long context as the earlier study. The client had full path/tool permission, isolated state and temporary folders, and no custom instruction-file loading or MCP tools. The test build used a private server port; the installed app and its exclusion file were not updated.

Four exploratory runs and one targeted follow-up preceded this final-build verification. Those records are retained separately; they were not substituted into this table.

The changes improve available dated evidence but did not make the assistant reliably accurate. Relevant-source selection, interpreting scope, reconciling later updates and avoiding unsupported conclusions remain problems. The rare-word and negative-clause heuristics can miss paraphrases or interpret ambiguous questions imperfectly. Verify a summary against the cited messages, particularly a latest-status claim.

The [earlier benchmark](2026-10-30-history-retrieval.md) describes the previous retrieval behavior. Its reported savings and accuracy figures are not measurements of this new path.
