Run: 2026-10-30 01:48 UTC · chat model: `Mock/mock-extractive-v1` · embeddings: `Mock/mock-hashing-v1` · k = 6

| Metric | Result |
|---|---|
| Retrieval hit-rate@6 (gold page in top-6 chunks) | 94% (17/18) |
| Mean reciprocal rank of the gold chunk | 0.82 |
| Answered (not "not found") on answerable questions | 89% (16/18) |
| Citation correctness (cites gold document, page ±1) | 61% (11/18) |
| Correct "not found" on unanswerable questions | 100% (3/3) |
| Faithfulness (LLM-as-judge) | not run: no LLM API key configured |
