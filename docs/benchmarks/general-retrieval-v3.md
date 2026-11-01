# General retrieval v3 evaluation

This small study compared an earlier frozen SessionMemory baseline with V3, which combined an updated tool, clean
passage representation, and source-reading guide. It was not an incremental V2-to-V3 test. Primary grades were blind
to condition; selected disagreements and ambiguous cases received secondary review. The study did not isolate ranking
changes from the guide and representation.

## Adjudicated results

| Set | Arm | Core-correct | Fully grounded |
| --- | --- | ---: | ---: |
| Fresh questions (6) | Baseline | 6/6 | 4/6 |
| Fresh questions (6) | V3 candidate | 6/6 | 5/6 |
| Earlier questions (4) | Baseline | 1/4 | 1/4 |
| Earlier questions (4) | V3 candidate | 3/4 | 3/4 |
| All questions (10) | Baseline | 7/10 | 5/10 |
| All questions (10) | V3 candidate | 9/10 | 8/10 |

Core-correct means the answer's central grade was correct. Fully grounded also requires supported evidence, valid
citations, and no unsupported claims.

The candidate had two wins and no losses on the earlier four-question set, with no core-correct regressions overall.
It improved on finding a later documentation update and attributing an original local fix to its source session. One
scope question remained partly correct in both arms. All six fresh questions were core-correct in both arms, so that
small set had no room to show a core-correct gain. These results do not establish broad or reliable accuracy.

## Model and usage

Both arms used Claude Sonnet 5.5 with high reasoning effort and long context. Each arm completed 10 calls against the
same 1,240-source history with the same chat and embedding models. Each used its own frozen index: the baseline used
the legacy index and the candidate used the clean passage index. The candidate runtime and instructions remained
unchanged after the validation freeze. All 20 calls completed without tool errors, permission denials, or client-state
isolation failures.

| Measure | Baseline | V3 candidate |
| --- | ---: | ---: |
| Calls | 10 | 10 |
| Total tokens | 1,242,746 | 1,937,071 |
| Uncached tokens | 307,006 | 395,506 |
| Summed wall time | 394.20 s | 408.99 s |

The candidate used more tokens and slightly more summed wall time. Wall times add two concurrent lanes and are not
elapsed study time. Token counts are usage measurements, not a billing estimate.

## Limits

This was a combined tool-and-instructions comparison, not a ranking-only test or a broad accuracy evaluation. It used
six fresh questions (four chronology and two reference-navigation cases) and four earlier questions. It
does not demonstrate performance on unseen topics or guarantee complete current-state retrieval.

The corpus and models were frozen, and each arm's separate index stayed unchanged during the run. The report contains
aggregate results only; it does not publish source text, session identifiers, or internal URLs. A real Linux
installation was not run. Windows and Unix installer mock suites passed. The manual Windows directory-junction check
passed; file and directory symlink self-tests were unavailable in this account.
