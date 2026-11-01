# Updated memory versus raw-log search

This blinded comparison tested the updated SessionMemory tool and documented guide against ordinary search of the
original raw logs. The raw exports included richer tool output than the normalized memory records. This is a comparison
of complete workflows, not a search-algorithm-only test.

## Accuracy

Ten previously discussed questions were run once per condition in fresh model sessions.

| Measure | Raw-log search | Updated memory |
| --- | ---: | ---: |
| Core answer correct | 8/10 | 7/10 |
| Fully supported, primary review | 6/10 | 6/10 |
| Fully supported, after secondary review | 6/10 | 7/10 |

Core correctness asks whether the answer got the requested material fact right. Fully supported also requires its
additional claims to follow from the evidence and its citations to be valid; minor locator errors count against it.
Primary reviewers gave both arms 6/10 fully supported answers. Secondary review accepted one memory answer's qualified
search-limit statement as consistent with its described search scope and another answer. That moved the memory total to 7/10.
This judgment is sensitive to how a qualified “not found” answer is scored. The accuracy results are mixed, with no
clear overall advantage. No core grades changed.

## Runtime and usage

| Measure | Raw-log search | Updated memory |
| --- | ---: | ---: |
| Mean wall time per call | 235.5183 s | 39.3996 s |
| Mean total tokens per call | 1,085,855.3 | 148,164.6 |
| Mean uncached tokens per call | 77,832.9 | 34,338.4 |
| Recoverable failed tool calls (total) | 10 | 0 |

In this run, memory took about one-sixth the mean time and used fewer measured tokens. The raw workflow recovered from
ten failed tool calls; memory had none. That is part of the normal workflow comparison, not a pure retrieval-algorithm
effect. These measurements are not billing estimates or guarantees.

Against raw-log search in this sample, the memory workflow had 83.3% lower mean wall time, 86.4% fewer total tokens,
and 55.9% fewer uncached tokens. These are observed workflow differences, not projections.

Wall time includes CLI startup and shutdown. Setup, index construction and grading were excluded; the index was ready.
Caches were not reset, and eleven pre-existing interactive Copilot processes were left running.

## Method and limits

The study used Claude Sonnet 5.5 with high reasoning effort and long context through Copilot CLI 1.0.105 on Windows.
The provider configuration, CLI version and executable hash were pinned. Ten previously discussed questions were run
once in each condition (20 calls), with no missing usage, invalid models or permission denials. Both arms used the same
chat model and 1,240-source history. The memory arm also used the updated tool, guide, embedding model and clean index;
the raw arm used no memory or embedding model and searched original exports with richer tool outputs.

The questions had been seen before, so this is not an unseen or randomized accuracy test. Input hashes, exclusions and
session isolation checks passed. A scope audit of 207 raw commands and 248 flags found no active out-of-scope commands
or source writes. Prompt and audit controls are not an operating-system sandbox.

No live install, update or push was performed. Windows and Unix installer mock suites passed. A manual Windows directory
junction check passed; symlink self-tests were unavailable in this account.

This is distinct from the [earlier V3 tool-to-tool comparison](general-retrieval-v3.md). Neither study establishes
general accuracy or cost savings.
