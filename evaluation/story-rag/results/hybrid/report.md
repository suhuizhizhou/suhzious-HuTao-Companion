# Story RAG 离线评估

独立问题族：103；执行：151；模式：hybrid；冷索引：6366 ms。

指标只验证本地检索与工程结构，不代表 DeepSeek 的事实正确率、自然度或语音延迟。

| 分组 | 路由准确率 | Anchor R@1 | Anchor R@5 | Context 全事实 | MRR | 热查 P95 ms |
|---|---:|---:|---:|---:|---:|---:|
| regression | 100.0% | 58.4% | 67.4% | 100.0% | 0.622 | 69.7 |
| challenge | 100.0% | 64.7% | 70.6% | 94.1% | 0.659 | 66.1 |

缓存命中 P50/P95：0.01/0.02 ms。

结构测试：56/56。

## 门禁

- structural: PASS
- configured_dense_actually_used: PASS
- regression_route_accuracy_ge_0.95: PASS
- regression_context_all_facts_ge_0.90: PASS
- regression_status_accuracy_ge_0.95: PASS
- lexical_warm_p95_lt_250ms: PASS

## 失败执行（不隐藏难例）

- wrong-grandpa/0: 胡桃爷爷是第七十六代，你是第七十八代对不对？；route=Retrieve, status=Answer, context=50%
