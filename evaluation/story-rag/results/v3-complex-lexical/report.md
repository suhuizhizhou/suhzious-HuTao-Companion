# Story RAG 离线评估

独立问题族：56；执行：56；模式：lexical；冷索引：5690 ms。

指标只验证本地检索与工程结构，不代表 DeepSeek 的事实正确率、自然度或语音延迟。

| 分组 | 路由准确率 | Anchor R@1 | Anchor R@5 | Context 全事实 | MRR | 热查 P95 ms |
|---|---:|---:|---:|---:|---:|---:|
| challenge | 98.2% | 52.8% | 77.4% | 67.9% | 0.622 | 54.7 |

## 难度分层

| 难度 | 执行 | 路由准确率 | Context 全事实 | Fact Group 平均覆盖 |
|---|---:|---:|---:|---:|
| L4 | 21 | 100.0% | 76.2% | 85.7% |
| L5 | 35 | 97.1% | 62.5% | 77.6% |

## 推理类型

| 类型 | 执行 | Context 全事实 | Anchor R@5 |
|---|---:|---:|---:|
| age-reasoning | 1 | 0.0% | 100.0% |
| alias | 2 | 100.0% | 100.0% |
| ambiguity | 2 |  |  |
| asr | 1 | 0.0% | 100.0% |
| asr-noise | 1 | 100.0% | 100.0% |
| assistant-untrusted | 2 | 100.0% | 0.0% |
| belief | 1 | 100.0% | 100.0% |
| belief-revision | 1 | 0.0% | 0.0% |
| causal | 15 | 60.0% | 86.7% |
| chronology | 4 | 25.0% | 100.0% |
| claim-decomposition | 1 | 100.0% | 100.0% |
| contrast | 4 | 50.0% | 50.0% |
| coreference | 8 | 85.7% | 42.9% |
| corroboration | 3 | 66.7% | 100.0% |
| counterfactual | 1 | 100.0% | 0.0% |
| cross-document | 10 | 50.0% | 80.0% |
| cross-scene | 3 | 0.0% | 100.0% |
| cross-source | 8 | 50.0% | 87.5% |
| disambiguation | 1 | 100.0% | 100.0% |
| epistemic-calibration | 1 | 100.0% | 100.0% |
| evidence-synthesis | 2 | 0.0% | 0.0% |
| exact-quote | 3 | 33.3% | 100.0% |
| factoid | 1 | 100.0% | 100.0% |
| forbidden-inference | 4 | 75.0% | 75.0% |
| identity | 2 | 100.0% | 100.0% |
| injection | 1 |  |  |
| joke-boundary | 1 | 100.0% | 100.0% |
| kinship | 2 | 100.0% | 100.0% |
| multi-hop | 17 | 70.6% | 82.4% |
| multi-turn | 9 | 83.3% | 33.3% |
| negation | 2 | 0.0% | 0.0% |
| noise | 1 | 0.0% | 100.0% |
| numeric | 4 | 100.0% | 100.0% |
| object-disambiguation | 1 | 100.0% | 0.0% |
| partial-answer | 1 | 100.0% | 100.0% |
| partial-answerable | 4 | 100.0% | 75.0% |
| persona-grounding | 1 | 100.0% | 100.0% |
| perspective | 1 | 100.0% | 100.0% |
| phonetic-normalization | 1 | 100.0% | 100.0% |
| practice-vs-principle | 1 | 0.0% | 100.0% |
| procedural | 3 | 100.0% | 66.7% |
| reconciliation | 1 | 100.0% | 100.0% |
| relationship | 2 | 50.0% | 100.0% |
| spatial | 2 | 100.0% | 50.0% |
| speaker-attribution | 8 | 37.5% | 100.0% |
| temporal | 17 | 82.4% | 76.5% |
| topic-correction | 1 | 0.0% | 0.0% |
| topic-resume | 1 | 100.0% | 0.0% |
| topic-switch | 3 | 100.0% | 0.0% |
| traditional-chinese | 1 | 100.0% | 100.0% |
| uncertainty | 5 | 80.0% | 100.0% |
| unknown-boundary | 8 | 100.0% | 85.7% |
| values | 2 | 0.0% | 100.0% |
| within-document | 3 | 100.0% | 100.0% |
| within-scene | 1 | 0.0% | 100.0% |
| wrong-premise | 4 | 100.0% | 75.0% |

## 来源范围与事实组复杂度

| 分组 | 执行 | Context 全事实 | Fact Group 平均覆盖 |
|---|---:|---:|---:|
| source:character | 35 | 80.0% | 86.9% |
| source:character+dialogue | 8 | 62.5% | 77.1% |
| source:dialogue | 10 | 30.0% | 62.5% |
| gold-groups:1 | 3 | 66.7% | 66.7% |
| gold-groups:2 | 21 | 71.4% | 83.3% |
| gold-groups:3 | 17 | 64.7% | 78.4% |
| gold-groups:4 | 8 | 50.0% | 75.0% |
| gold-groups:6 | 2 | 100.0% | 100.0% |
| gold-groups:7 | 2 | 100.0% | 100.0% |

缓存命中 P50/P95：0.03/0.03 ms。

结构测试：56/56。

## 门禁

- structural: PASS
- configured_dense_actually_used: PASS
- regression_route_accuracy_ge_0.95: PASS
- regression_context_all_facts_ge_0.90: PASS
- regression_status_accuracy_ge_0.95: PASS
- lexical_warm_p95_lt_250ms: PASS

## 失败执行（不隐藏难例）

- v3-followup-memory-01/0 [L5; multi-hop,coreference,temporal]: 那时候你多大？包里具体装了什么？；route=Clarify, status=Clarify, groups=0/2
- v3-asr-homophone/0 [L4; asr-noise,phonetic-normalization,factoid]: 那顶帽子是七十五代堂主传下来的吧？我听成‘七十五代堂主’还是‘七十五袋糖主’了。；route=Retrieve, status=Tentative, groups=2/2
- v3-serious-and-playful/0 [L5; cross-source,contrast,evidence-synthesis]: 有人说胡桃只会胡闹。找两条她认真管往生堂的证据，再从剧情档案找一句她很跳脱的原话，说明这两面能不能同时存在。；route=Retrieve, status=Clarify, groups=0/3
- v3-child-to-director/0 [L5; cross-document,temporal,age-reasoning]: 按年龄排一下：三岁、六岁、八岁、十三岁和十多岁时你分别做了什么？注意十三岁那场葬礼时你已经是堂主了吗，后来又是第几代？；route=Retrieve, status=Answer, groups=3/4
- v3-death-view-two-events/0 [L5; cross-document,causal,belief-revision,contrast]: 爷爷没在边界出现和七七拼命想活，这两件事分别怎样影响了你对离去与求生的判断？这能否说明你不是见到僵尸就一律埋掉？；route=Retrieve, status=Answer, groups=1/3
- v3-zhongli-role-trust/0 [L5; cross-source,relationship,speaker-attribution]: 翻档案确认钟离在往生堂是什么身份、是谁介绍的；再结合你的角色资料说，他在堂里做什么，你嘴上嫌他古板为什么还不等于不信任。；route=Retrieve, status=Answer, groups=2/3
- v3-tradition-customer-choice/0 [L4; cross-document,causal,values]: 你爷爷那句‘遵从自心，尽人之事’是什么原话？你经营往生堂时尊重不同客人的做法，具体怎么体现，而不是套同一种葬礼？；route=Retrieve, status=Answer, groups=1/3
- v3-wangsheng-origin-chain/0 [L5; multi-hop,causal,within-scene,chronology]: 翻《医心》那段档案：往生堂最初为什么建立，先人怎么应对，到了现在什么业务没了、什么本事还传着？；route=Retrieve, status=Answer, groups=1/3
- v3-baizhu-uncertainty-chain/0 [L5; uncertainty,causal,forbidden-inference,multi-hop]: 你是不是已经查明白术为什么追求不死了？把药师的疠气、沉玉谷医法、你掌握详情的程度和对白术的判断分别说清楚。；route=Retrieve, status=Answer, groups=3/4
- v3-wangsheng-origin-cross-quest/0 [L5; cross-source,corroboration,temporal,causal]: 跨两段任务档案核对：为什么一处说往生堂为对抗魔神怨念建立，另一处又说最初像医生？把两处原话的共同背景和侧重点说出来。；route=Retrieve, status=Tentative, groups=1/4
- v3-archive-1010-chain/0 [L5; multi-hop,chronology,speaker-attribution,cross-scene]: 翻第一章第二幕档案，按出现先后告诉我：谁拿‘冰层下的水’比喻凡人，谁夸派蒙像好向导，公子兑现承诺后说找到了什么人，最后怎样介绍钟离？；route=Retrieve, status=Tentative, groups=1/4
- v3-archive-childe-promise-result/0 [L4; causal,chronology,speaker-attribution]: 叶卡捷琳娜说公子答应找人，后面公子怎么证明约定完成，那个能破局的人最后是谁、什么身份？；route=Retrieve, status=Tentative, groups=2/3
- v3-two-greetings/0 [L4; cross-scene,speaker-attribution,exact-quote]: 档案里胡桃有两次俏皮开场：一次叫旅行者他们‘两位大忙人’，另一次听见别人提名字就冒出来。把两句分别找出来，别拼成一句。；route=Retrieve, status=Clarify, groups=1/2
- v3-two-hutao-quotes/0 [L4; cross-scene,speaker-attribution,exact-quote]: ‘太阳出来我晒太阳’和‘两位大忙人，找本堂主有何贵干’是不是同一个人说的？给出两处档案，不要说成钟离或派蒙。；route=Retrieve, status=Answer, groups=1/2
- v3-business-practice-cross-source/0 [L5; cross-source,values,practice-vs-principle]: 从原则和实际推销各举证：角色资料里你怎样要求按客户需求办事，传说任务里又给冒险家协会开了什么首批条件？；route=Retrieve, status=Answer, groups=1/2
- v3-user-topic-correction/0 [L4; multi-turn,negation,topic-correction]: 不是继续问你爷爷，我问的是帽子原主人，以及它为什么不合你的头。；route=Retrieve, status=Tentative, groups=0/1
- v3-asr-mixed-three/0 [L5; noise,asr,cross-document,multi-hop]: 往声堂桃桃那俩石师子叫啥，左 you 右 you？还有头上那顶是不是七十五代爷爷传的👻；route=Retrieve, status=Answer, groups=1/2
- v3-double-negation-serious/0 [L5; negation,cross-document,evidence-synthesis]: 不能说胡桃并不是不认真吧？别只回答‘对’，拿她十三岁办葬礼和给大咪二咪洗澡各举一个具体证据。；route=Retrieve, status=Answer, groups=1/2
- v3-uncertain-causal-language/0 [L5; uncertainty,causal,epistemic-calibration]: 资料是不是确定说七七哪一个动作打动了你、那场事故具体是谁造成的？把原文能确定、‘或许’和只概括没展开的部分分层说。；route=Retrieve, status=Clarify, groups=3/3
- v3-archive-personal-perspective/0 [L5; cross-source,perspective,speaker-attribution]: 你去边界找爷爷这件事可以当自己的回忆讲；那公子介绍钟离时你也在场亲眼看见了吗？把第二件事按档案原话转述。；route=Retrieve, status=Clarify, groups=2/2
- v3-composed-user-demand/0 [L5; multi-hop,wrong-premise,cross-document,claim-decomposition]: 一句话核对三件事：帽子是七十五代传的、你是七十七代、所以你爸就是七十六代。哪些对，哪一项没证据？；route=Retrieve, status=Clarify, groups=2/2
