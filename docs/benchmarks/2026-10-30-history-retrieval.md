# History-retrieval benchmark, October 30, 2026

SessionMemory produced responses faster and used fewer model tokens in this test. Factual accuracy was lower, so these measurements support a response-efficiency claim, not a claim that it produced correct answers faster.

## Results

We ran four historical-retrieval questions three times per condition: 24 fresh, sequential agent sessions and 12 matched pairs. Both conditions used GitHub Copilot CLI 1.0.105 with Claude Sonnet 5.5, high reasoning effort and long context. Both had full tool and path permissions. There were zero permission denials.

One direct-log trial reached the 15-minute cap without an answer or shutdown usage record. The following comparisons use the other **11 matched pairs where both runs completed**, including answers graded partly correct.

| Mean per completed paired trial | SessionMemory | Direct log search | Reduction |
| --- | ---: | ---: | ---: |
| Wall time, including client startup and shutdown | 29.1 seconds | 386.5 seconds | 92.5% |
| Total input plus output tokens | 101,955 | 1,750,269 | 94.2% |
| Uncached input plus output tokens | 21,877 | 113,836 | 80.8% |

Each reduction is calculated from the two condition means: `100 × (baseline mean - memory mean) / baseline mean`. It is not the mean of individual pair percentages.

Total tokens include cached input. Uncached tokens are `input - cache-read + output`, using native client accounting; cache writes remain included in input. Neither measure directly establishes billing savings.

### Completion and correctness

| All 12 trials per condition | SessionMemory | Direct log search |
| --- | ---: | ---: |
| Completed responses | 12 | 11 |
| Fully correct | 1 | 6 |
| Partly correct | 11 | 5 |
| Wrong or blank | 0 | 1 |
| Timeouts at 900 seconds | 0 | 1 |

The blank direct-log timeout is the wrong answer in this table, not an additional trial. Partly correct answers found relevant material but contained material recency errors or unsupported factual claims. Common problems included missing later work, confusing an earlier proposal with its eventual outcome, and inferring publication from source or merge evidence.

Answer-only reviewers received randomized labels without condition, timing or token figures. They verified claims against copied source conversations. Citations and wording could still suggest the retrieval method, so blinding was imperfect. Source verification corrected an incomplete initial reference and clarified ambiguous question scope while reviewers were blinded. Documentation and read-only technical investigation were eligible work; a code change was not required.

### Variation by question

These are medians across three trials per condition. The direct-log runtime median for component technical history includes the capped timeout; it is not a median time to a successful answer.

| Historical-retrieval question | Memory median runtime | Direct-log median runtime | Completed, memory / direct |
| --- | ---: | ---: | ---: |
| Recover project status and its latest session | 24.8 s | 254.0 s | 3 / 3 |
| Identify a hosting bug's fix session | 37.2 s | 761.6 s | 3 / 3 |
| Find recent technical work on a component | 30.6 s | 467.1 s | 3 / 2 |
| Find recent security-model work on that component | 23.7 s | 137.5 s | 3 / 3 |

Across all registered trials, memory runtime ranged from 19.9 to 42.2 seconds. Direct-log time to response or timeout ranged from 135.7 to 900 seconds. The large variation is a reason to retain every trial rather than report the fastest pair.

The [sanitized trial data](2026-10-30-history-retrieval.csv) contains all 24 timings, available token totals, statuses and grades. Timeout tokens are blank, not zero.

## Method

- One Windows machine and one developer's recorded history. The fixed dataset contained 1,240 allowed session records in both conditions, including subagent records and a validation canary. Raw records occupied 4,034,783,926 bytes.
- Existing exclusions were retained. Known benchmark and tool-development sessions were filtered from both inputs. Old source formats were copied or exported per session from their original stores; the baseline received their directory location.
- Memory used a private copy of an existing index and conversation corpus. Archive/recent state covered all 1,240 allowed documents with matching content signatures. The timed trials did not construct an index.
- The test app came from commit `046a133`; only its search-server port changed to isolate it from the running installation. The private server was prewarmed with the validation canary.
- The memory condition received adapted installed instructions and could use `search` and `show`. `find` was excluded because it would read live sources. This tests that command subset, not every feature of a normal installation.
- The baseline could use ordinary file/text searches and create parsing scripts in its own scratch directory. Both conditions had the same client/model settings, source scope, available local tools, full-path access and 900-second cap. Automatic loading of custom instruction files, MCP servers and the client's automatic text indexer were disabled.
- Trial order was counterbalanced across conditions and questions. Each trial used a fresh session ID, working folder, client state directory and temporary directory. Earlier permission-limited runs were retained separately and were not pooled into these figures.
- Native starts confirmed the configured model, effort and context. All 23 completed shutdowns reported only the registered model. The timeout's complete token usage was unavailable.
- Input hashes and corpus comparisons passed after the study. The live exclusion file was unchanged, and none of the 24 trial sessions appeared in the normal session files or database. Command traces showed no explicit access to original histories, benchmark outputs or external network sources. Full-path access and prompt instructions are not an operating-system sandbox.

Prompts, native traces, private source references, grades, integrity checks and detailed analysis remain local because they contain private session material. The CSV omits those contents and identifiers.

## What the figures mean

This is a small test of four overlapping history-retrieval questions, not a representative coding workload or a general savings guarantee. The questions had been discussed before; known benchmark records and artifact markers were filtered, but complete absence of historical benchmark references cannot be established. Some compressed older records were not scanned for artifact markers.

The experiment compares complete assistant workflows, including retrieval guidance, parsing strategy, model decisions and error recovery. It does not compare an index against a hand-optimized human search procedure or measure a bare search algorithm.

The index and server were ready before timing. Installation, model download, index construction, background upkeep, experiment preparation and grading were excluded. OS and provider caches were not reset, and earlier pilot runs preceded this study. Other applications remained running on the machine.

Use the measured time and token reductions with the correctness results. SessionMemory helps locate source conversations; an assistant's summary, particularly a claim about the latest state, still needs verification.
