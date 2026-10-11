# Knowledge graph integration fixture

The optional graph projection uses Neo4j Community 5.26.0 and Neo4j.Driver 5.28.0. SQLite remains authoritative. No production backend is enabled by running these checks.

Without Docker, the Maven harness starts a real isolated Neo4j database using Java 17. It binds Bolt to a random loopback port; the fixture disables HTTP and is for local development only. It is not a production authentication/deployment configuration.

```powershell
& .cache/knowledge-baseline/apache-maven-3.9.9/bin/mvn.cmd -f deploy/knowledge-graph/fixture/pom.xml -q compile
& .cache/knowledge-baseline/apache-maven-3.9.9/bin/mvn.cmd -f deploy/knowledge-graph/fixture/pom.xml -q exec:java '-Dexec.mainClass=local.hutao.GraphFixture' '-Dexec.args=E:/tomorrow/AIGC/hutao-companion/.cache/knowledge-baseline/graph-fixture-p10'
```

Pass a second argument when a frozen evaluation campaign requires a stable Bolt URI, for example `-Dexec.args="E:/tomorrow/AIGC/hutao-companion/.cache/knowledge-baseline/graph-fixture-p10 63800"`. The fixture validates that the requested port is in the non-privileged TCP range and still binds loopback only.

The Maven executable above is the locally downloaded 3.9.9 baseline runtime. A compatible installed Maven can be used instead. The second command stays running; use a separate terminal for checks:

```powershell
dotnet tests/HuTao.StoryRag.Eval/bin/Debug/net9.0/HuTao.StoryRag.Eval.dll --knowledge-graph-checks --graph-uri (Get-Content .cache/knowledge-baseline/graph-fixture-p10/bolt-uri.txt)
```

Missing `--graph-uri` reports SKIP and returns 2, not PASS. Tests create a unique namespace and remove only that namespace's nodes in `finally`; they do not clear a shared database. Stop the owning Maven execution with Ctrl+C, never terminate processes by Java/Neo4j name.

Implemented projection boundary: parameterized fixed Cypher, composite namespace/ID/generation uniqueness, revision comparison, per-event receipts, and SQL acknowledgment only after graph transaction success. Only IDs, entity types, predicate/status/time/polarity are projected; raw evidence and literal values remain in SQLite.

Outbox delivery uses one ordered claim per namespace, finite lease ownership, exponential retry delay, and an explicit diagnostic state. A diagnostic item blocks checkpoint advancement until resolved; SQL remains available. This is at-least-once delivery, not a distributed transaction.

Generation rebuild, bounded graph retrieval and runtime host lifecycle integration still require their plan steps. Do not interpret projection tests as retrieval quality approval.
