# Story RAG 离线评估

独立问题族：103；执行：151；模式：lexical；冷索引：6067 ms。

指标只验证本地检索与工程结构，不代表 DeepSeek 的事实正确率、自然度或语音延迟。

| 分组 | 路由准确率 | Anchor R@1 | Anchor R@5 | Context 全事实 | MRR | 热查 P95 ms |
|---|---:|---:|---:|---:|---:|---:|
| regression | 100.0% | 55.1% | 61.8% | 100.0% | 0.581 | 28.3 |
| challenge | 100.0% | 52.9% | 58.8% | 94.1% | 0.549 | 22.2 |

缓存命中 P50/P95：0.02/0.02 ms。

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
