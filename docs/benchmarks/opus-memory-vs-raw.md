# Opus 5.5: updated memory versus raw-log search

SessionMemory and ordinary raw-log search each answered ten previously discussed questions using Claude Opus 5.5.
Both workflows got eight core answers correct and seven fully supported. Memory used less time and fewer tokens on
the nine pairs where both methods finished with complete usage records.

These measurements use the frozen build from the study. Later code-review fixes and cleanup have not repeated this model benchmark.

## Accuracy

| Measure | Raw-log search | Updated memory |
| --- | ---: | ---: |
| Core answer correct | 8/10 | 8/10 |
| Fully supported answer | 7/10 | 7/10 |
| Completed answers | 9/10 | 10/10 |
| Timed out at 900 seconds | 1/10 | 0/10 |

Core correctness asks whether the requested material facts are right. Full support also requires grounded evidence,
valid citations and no unsupported extra claims. Missing ancillary details alone do not fail full support. A scoped
statement about what a search found differs from a claim that no relevant record exists.

Independent primary and secondary reviewers checked anonymous answers against the original records before methods
and usage were revealed. Three grading differences were resolved under these definitions: two supported status
answers retained full support, and a correct main answer retained core credit while its false ancillary claims failed
full support. Original reviews and reasons for the resolutions remain in the private study artifacts.

Both methods missed later technical RabbitMQ investigation. Memory also answered one question about a different pull
request; raw search produced no answer for one question within the time limit. These results do not establish an
accuracy advantage for either method.

## Time and tokens

These means use the identical nine questions where both methods completed with full native usage. Changes are memory
relative to raw-log search.

| Measure | Raw-log search | Updated memory | Change |
| --- | ---: | ---: | ---: |
| Mean CLI wall time | 198.56 s | 61.56 s | 69.0% less |
| Mean total tokens | 937,171 | 242,837 | 74.1% fewer |
| Mean uncached tokens | 81,732 | 39,744 | 51.4% fewer |

The raw timeout remains in the accuracy denominator. Its 900-second limit and missing token usage are reported
separately, rather than counted as zero or compared with a different set of memory questions. Memory's completed
answer for that question is also excluded from the paired means.

Wall time includes CLI startup and shutdown, but excludes setup, index construction, harness checks, the infrastructure
pause and grading. The index was ready. Native token counts are usage measurements, not bills. Caches were not reset
and other interactive applications remained running. No failed tool calls, permission denials or isolation failures
were recorded in the twenty attempts.

## Comparison with the earlier Sonnet run

| Model study | Raw core correct | Memory core correct | Raw fully supported | Memory fully supported |
| --- | ---: | ---: | ---: | ---: |
| Sonnet 5.5 | 8/10 | 7/10 | 6/10 | 7/10 |
| Opus 5.5 | 8/10 | 8/10 | 7/10 | 7/10 |

Memory's core score rose by one answer, or ten percentage points. Its fully supported score stayed the same. These
were separate single-run studies of the same selected questions, so the difference cannot establish a general model
effect. Sonnet's time and usage means cover ten completed pairs; Opus's cover nine. Do not compare those means as if
they used the same question mix. The [Sonnet report](updated-memory-vs-raw.md) preserves its measurements and grading.

## Method and limits

Both arms used Opus 5.5 with high reasoning effort and long context through Copilot CLI 1.0.105 on Windows. The frozen
tool, guide, questions, CLI, private configuration, embedding model and index were pinned. Each method received one
fresh attempt per question with the same 900-second cap, in two lanes with alternating method order. No launched
question was retried or replaced.

Both arms covered the same 1,240-source history. Raw search used original exports with richer tool outputs than the
normalized memory corpus. This compares complete workflows, including their documented guidance, rather than the
retrieval algorithm alone. The ten questions had been seen in earlier tests; they are not an unseen or random sample.

The first workers stopped after fifteen attempts when an unrelated global trusted-folder entry changed. The five
never-launched attempts ran later under an approved continuation using the unchanged frozen private configuration.
Original artifacts were retained and merged by their scheduled IDs. No answer or score was used to decide which
attempts continued. This was split execution, not an uninterrupted run.

Final input, exclusion, configuration and session-isolation checks passed. Recorded native starts and shutdowns
verified the requested model settings for nineteen completed attempts. The timeout's start selected those settings,
but its missing shutdown leaves completion identity unverified; it is not an observed model mismatch.

A separate review examined 197 raw tool-start events and all 266 flagged events across both methods. It found no active
cross-arm source access, source writes or network requests in the recorded calls. These prompt restrictions and audits
are not an operating-system sandbox and cannot exclude every implicit read or indirect effect.

No live installation, update or push was performed. Private source excerpts, answers, traces and detailed grades remain
local. The [V3 tool-to-tool report](general-retrieval-v3.md) covers a different comparison and should not be pooled with
this one.
