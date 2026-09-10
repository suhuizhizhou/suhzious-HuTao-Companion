# Story RAG 离线评估

独立问题族：259；执行：307；模式：lexical；冷索引：6151 ms。

指标只验证本地检索与工程结构，不代表 DeepSeek 的事实正确率、自然度或语音延迟。

| 分组 | 路由准确率 | Anchor R@1 | Anchor R@5 | Context 全事实 | MRR | 热查 P95 ms |
|---|---:|---:|---:|---:|---:|---:|
| regression | 100.0% | 55.1% | 61.8% | 100.0% | 0.579 | 29.6 |
| challenge | 79.1% | 59.1% | 72.4% | 66.9% | 0.644 | 34.7 |

## 意图范围（不检索题不计入事实召回分母）

| 范围 | 题次 | 路由准确率 | 全事实覆盖 |
|---|---:|---:|---:|
| legacy | 207 | 99.5% | 87.4% |
| lore | 30 | 90.0% | 43.3% |
| mixed | 20 | 95.0% | 80.0% |
| unrelated | 15 | 20.0% |  |
| playful | 10 | 50.0% |  |
| comfort | 10 | 10.0% |  |
| clarification | 5 | 0.0% |  |
| unknown | 10 | 90.0% | 85.7% |

不应查却检索：25/80；应查却未检索：5/227。

范围、证据可自动评分；expected_plan、现实建议、观点边界仍需人工核查，不以多次调用或节点数量证明推理正确。

## 难度分层

| 难度 | 执行 | 路由准确率 | Context 全事实 | Fact Group 平均覆盖 |
|---|---:|---:|---:|---:|
| L1 | 123 | 100.0% | 98.9% | 98.9% |
| L2 | 8 | 100.0% | 100.0% | 100.0% |
| L3 | 55 | 52.7% | 83.3% | 87.5% |
| L4 | 21 | 100.0% | 76.2% | 85.7% |
| L5 | 100 | 89.0% | 61.8% | 78.2% |

## 推理类型

| 类型 | 执行 | Context 全事实 | Anchor R@5 |
|---|---:|---:|---:|
| age-reasoning | 1 | 0.0% | 100.0% |
| alias | 4 | 100.0% | 75.0% |
| ambiguity | 2 |  |  |
| archive-perspective | 2 | 50.0% | 50.0% |
| asr | 1 | 0.0% | 100.0% |
| asr-noise | 1 | 100.0% | 100.0% |
| assistant-untrusted | 2 | 100.0% | 0.0% |
| behavior | 4 | 100.0% | 75.0% |
| belief | 1 | 100.0% | 100.0% |
| belief-revision | 1 | 0.0% | 0.0% |
| causal | 20 | 70.0% | 75.0% |
| childhood | 5 | 100.0% | 100.0% |
| chronology | 11 | 72.7% | 63.6% |
| claim-decomposition | 1 | 100.0% | 100.0% |
| clarify-context | 5 |  |  |
| community-critical-reading | 10 | 40.0% | 50.0% |
| contrast | 4 | 50.0% | 50.0% |
| coreference | 8 | 85.7% | 42.9% |
| corpus-boundary | 1 |  |  |
| corroboration | 3 | 66.7% | 100.0% |
| counterfactual | 1 | 100.0% | 0.0% |
| cross-document | 10 | 50.0% | 80.0% |
| cross-scene | 3 | 0.0% | 100.0% |
| cross-source | 8 | 50.0% | 87.5% |
| crossover | 2 |  |  |
| dependent-evidence | 10 | 20.0% | 70.0% |
| dependent-retrieval | 47 | 65.2% | 73.9% |
| disambiguation | 1 | 100.0% | 100.0% |
| domain-disambiguation | 5 |  |  |
| epistemic-calibration | 1 | 100.0% | 100.0% |
| evidence-synthesis | 2 | 0.0% | 0.0% |
| exact-quote | 6 | 66.7% | 100.0% |
| fabricated-quote | 1 |  |  |
| factoid | 1 | 100.0% | 100.0% |
| false-perspective | 1 |  |  |
| fan-fiction | 1 |  |  |
| fiction-vs-reality | 1 | 100.0% | 0.0% |
| follow-up | 2 | 100.0% | 100.0% |
| follow-up-chain | 1 | 0.0% | 0.0% |
| forbidden-inference | 4 | 75.0% | 75.0% |
| hypothetical | 2 |  |  |
| identity | 7 | 100.0% | 57.1% |
| implicit-memory | 2 | 100.0% | 0.0% |
| injection | 5 |  |  |
| input-boundary | 2 |  |  |
| joke-boundary | 2 | 100.0% | 100.0% |
| kinship | 2 | 100.0% | 100.0% |
| mixed-intent | 10 | 90.0% | 70.0% |
| mixed-practical | 10 | 70.0% | 80.0% |
| multi-fact | 2 | 100.0% | 100.0% |
| multi-hop | 17 | 70.6% | 82.4% |
| multi-turn | 9 | 83.3% | 33.3% |
| negation | 2 | 0.0% | 0.0% |
| noise | 1 | 0.0% | 100.0% |
| numeric | 4 | 100.0% | 100.0% |
| object-disambiguation | 1 | 100.0% | 0.0% |
| orthography | 5 | 100.0% | 60.0% |
| out-of-domain | 19 |  |  |
| paraphrase | 2 | 100.0% | 100.0% |
| partial-and-unknown | 10 | 85.7% | 85.7% |
| partial-answer | 1 | 100.0% | 100.0% |
| partial-answerable | 4 | 100.0% | 75.0% |
| partial-quote | 4 | 100.0% | 100.0% |
| persona-grounding | 1 | 100.0% | 100.0% |
| personal-detail | 8 | 100.0% | 37.5% |
| perspective | 1 | 100.0% | 100.0% |
| phonetic-normalization | 1 | 100.0% | 100.0% |
| playful-boundary | 10 |  |  |
| poetry | 1 | 100.0% | 0.0% |
| practice-vs-principle | 1 | 0.0% | 100.0% |
| procedural | 3 | 100.0% | 66.7% |
| procedural-detail | 7 | 100.0% | 28.6% |
| quest-fact | 3 | 100.0% | 100.0% |
| real-emotion-priority | 10 |  |  |
| real-grief | 4 |  |  |
| reconciliation | 1 | 100.0% | 100.0% |
| relationship | 10 | 90.0% | 70.0% |
| scope-separation | 20 | 80.0% | 75.0% |
| smalltalk | 6 |  |  |
| source-and-logic | 10 | 70.0% | 90.0% |
| spatial | 2 | 100.0% | 50.0% |
| spatial-detail | 5 | 100.0% | 60.0% |
| speaker-attribution | 9 | 44.4% | 100.0% |
| temporal | 17 | 82.4% | 76.5% |
| temporal-change | 7 | 100.0% | 71.4% |
| topic-correction | 1 | 0.0% | 0.0% |
| topic-resume | 1 | 100.0% | 0.0% |
| topic-switch | 5 | 100.0% | 0.0% |
| traditional-chinese | 1 | 100.0% | 100.0% |
| uncertain-source | 1 | 100.0% | 100.0% |
| uncertainty | 5 | 80.0% | 100.0% |
| underspecified | 4 |  |  |
| unknown-boundary | 8 | 100.0% | 85.7% |
| unknown-identity | 1 |  |  |
| unknown-personal | 3 |  |  |
| unknown-precision | 2 |  |  |
| values | 2 | 0.0% | 100.0% |
| within-document | 3 | 100.0% | 100.0% |
| within-scene | 1 | 0.0% | 100.0% |
| work-values | 5 | 100.0% | 80.0% |
| wrong-premise | 11 | 90.9% | 54.5% |

## 来源范围与事实组复杂度

| 分组 | 执行 | Context 全事实 | Fact Group 平均覆盖 |
|---|---:|---:|---:|
| source:character | 165 | 90.3% | 93.9% |
| source:character+dialogue | 20 | 35.0% | 64.3% |
| source:dialogue | 31 | 58.1% | 75.0% |
| gold-groups:1 | 105 | 96.2% | 96.2% |
| gold-groups:2 | 44 | 77.3% | 86.4% |
| gold-groups:3 | 32 | 65.6% | 80.2% |
| gold-groups:4 | 11 | 45.5% | 68.2% |
| gold-groups:5 | 7 | 85.7% | 85.7% |
| gold-groups:6 | 11 | 45.5% | 74.2% |
| gold-groups:7 | 5 | 40.0% | 88.6% |
| gold-groups:9 | 1 | 0.0% | 33.3% |

缓存命中 P50/P95：0.13/0.18 ms。

结构测试：56/56。

## 门禁

- structural: PASS
- configured_dense_actually_used: PASS
- regression_route_accuracy_ge_0.95: PASS
- regression_context_all_facts_ge_0.90: PASS
- regression_status_accuracy_ge_0.95: PASS
- lexical_warm_p95_lt_250ms: PASS

## 失败执行（不隐藏难例）

- follow-chain/0 [L3; follow-up-chain]: 然后呢？；route=Retrieve, status=Answer, groups=0/1
- wrong-grandpa/0 [L3; wrong-premise]: 胡桃爷爷是第七十六代，你是第七十八代对不对？；route=Retrieve, status=Answer, groups=1/2
- archive-not-witness/0 [L1; archive-perspective]: 你亲眼看到公子介绍钟离了吗？当时说了什么？；route=Retrieve, status=Answer, groups=0/1
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
- v4-seven-hop-life-boundary/0 [L5; dependent-evidence,dependent-retrieval]: 你小时候去找过一个没能见到的人。先认出他与传给你的帽子有什么关系，再核对当时你的身份，最后解释你怎么理解没见到他这件事；别把走路和等待的天数凑成总数。；route=Retrieve, status=Tentative, groups=5/6
- v4-qiqi-belief-revision-proof/0 [L5; dependent-evidence,dependent-retrieval]: 曾经拦住你埋七七的那位医生，他的师父跟你家究竟怎么连起来？那段家族分歧能不能直接证明你后来关心七七只是在装样子？；route=Retrieve, status=Answer, groups=6/7
- v4-three-entity-disambiguation/0 [L5; dependent-evidence,dependent-retrieval]: 那个后来得替你照顾宠物的群体，一开始把你当成什么人？顺着这条线找出宠物身份、左右名字和你转移兴趣后的说法，别真给它们安排猫粮。；route=Clarify, status=Clarify, groups=0/6
- v4-uncle-branch/0 [L5; dependent-evidence,dependent-retrieval]: 你家那个离开往生堂去学医的人，和如今经营不卜庐的医生是同一个人吗？先辨身份，再查离家的转折，最后说清楚你爷爷与他此后有没有联系。；route=Retrieve, status=Answer, groups=0/5
- v4-business-history-continuity/0 [L5; dependent-evidence,dependent-retrieval]: 往生堂曾做过像医生的事，那为什么白术师父改行学医却会和你爷爷闹翻？把机构起源、个人选择和文本没交代的动机拆开，别只用一句生死对立解释全部。；route=Retrieve, status=Clarify, groups=3/9
- v4-trust-speaker-chain/0 [L5; dependent-evidence,dependent-retrieval]: 最初把你那位最受信赖的客卿介绍给旅行者的人是谁？介绍里的职位和他给仪倌上课的事怎么互相印证？你能不能把那场介绍当成自己的亲历来讲？；route=Retrieve, status=Answer, groups=2/3
- v4-tradition-exception/0 [L5; dependent-evidence,dependent-retrieval]: 你说尊重生死规矩，却为一个不想死的人破例；办葬礼时你又要求不能拘泥一种形式。这两件事能支持什么共同看法，又有哪些地方不能画等号？；route=Retrieve, status=Answer, groups=6/7
- v4-hat-grief-vision/0 [L5; dependent-evidence,dependent-retrieval]: 你珍惜的旧帽、没见到的爷爷、背包里的神之眼能连成一条故事线吗？先核对人物，再区分你亲手做的事、你的理解和叙述者的或许。；route=Retrieve, status=Answer, groups=5/7
- v4-death-view-disagreement/0 [L5; community-critical-reading]: 有人讨论说不赞同胡桃的生死观：就算她工作认真，主动给活人推销棺材也会让人不舒服。能分别找支持职业认真和营销冒犯感的文本，再谈这两种评价为什么不必互相抵消吗？；route=Retrieve, status=Answer, groups=0/3
- v4-not-anti-medicine/0 [L5; community-critical-reading,dependent-retrieval]: 看到白术与胡桃生死观的讨论，我想确认：反对追求不死是不是等于她反对所有治病？请把她对七七的变化、往生堂的起源与她承认不了解的部分放一起看。；route=Retrieve, status=Answer, groups=2/6
- v4-poem-canon-check/0 [L5; community-critical-reading,dependent-retrieval]: 网上那首写七七坐轿、胡桃提锹还打折的诗，是游戏原话还是玩家创作？先核对作者身份能否从本地库得到，再用角色故事判断诗里哪些是借用设定、哪些不能当现在发生的事。；route=Retrieve, status=Clarify, groups=3/3
- v4-identity-opposed-claims/0 [L5; community-critical-reading]: 讨论区有人说让钟离帮厨就说明胡桃绝不知道他身份，另有人说越使唤越证明她知道。仅凭这种行为能推出哪一边？先核对客卿身份与信赖，再列出仍缺的明确证据。；route=Retrieve, status=Answer, groups=2/3
- v4-dinner-vote-evidence/0 [L5; community-critical-reading]: 饭局分析里让大家投票胡桃是不是装不知道。投票结果能当角色知道钟离身份的证据吗？把剧情发言、角色资料和玩家投票三种来源分开说明。；route=Retrieve, status=Answer, groups=1/2
- v4-marketing-consent/0 [L5; community-critical-reading]: 我能接受喜丧但不喜欢被突然推销。胡桃既然说尊重客户需求，怎么评议她给冒险家协会谈的合作条件？请先列事实，再说你的评价，不要给角色强行洗白。；route=Retrieve, status=Answer, groups=2/3
- v4-fan-art-friendship/0 [L5; community-critical-reading,dependent-retrieval]: 同人图把胡桃七七画得很亲近，还写cb向，这能算七七已经原谅她吗？查她们过去和后来的关系，同时尊重同人可以另写一种相处。；route=Playful, status=Playful, groups=0/4
- v4-doctors-not-retcon/0 [L5; source-and-logic,dependent-retrieval]: 一段说往生堂最初像医生，一段说为对抗魔神怨念建立。先各查原句，再判断这是设定吃书，还是把同一背景说成不同侧面；没有具体创立年份就别补。；route=Retrieve, status=Tentative, groups=4/6
- v4-causal-arrow-qiqi/0 [L5; source-and-logic,dependent-retrieval]: 是你先决定不埋七七才去调查，还是调查后才改变看法？请找时间连接词，还要说明原文有没有确定说求生就是唯一原因。；route=Retrieve, status=Clarify, groups=5/5
- v4-two-poet-titles/0 [L5; source-and-logic]: 小巷派暗黑诗人和小巷派打油诗人到底哪个是真的？分别找角色资料和任务自称，再判断不同措辞是否意味着换了一个人。；route=Bypass, status=Bypass, groups=0/2
- v4-cat-stone-reading/0 [L5; source-and-logic,dependent-retrieval]: 原文同一段里有三花猫、大咪二咪和卫兵，为什么不该把三花猫当成二咪？要把场所、命名、类比说话人和后续清扫串起来。；route=Retrieve, status=Clarify, groups=5/5
- v4-causal-business-poetry/0 [L5; source-and-logic,dependent-retrieval]: 行秋来找你是因为往生堂培训做得好，还是诗歌名气？两段都提学习，帮我找对因果链，顺便区分客卿讲课、赛诗、评委三种角色。；route=Retrieve, status=Answer, groups=4/6
- v4-history-public-health/0 [L5; mixed-practical,dependent-retrieval,scope-separation]: 往生堂早期净化空气焚烧尸骸和现代公共卫生是不是一回事？先找到游戏语境，再给概念上的相似与差异，不提供现实焚烧操作步骤。；route=Retrieve, status=Tentative, groups=2/4
- v4-code-family/0 [L5; mixed-practical,dependent-retrieval,scope-separation]: 查清白术、他师父和你爷爷的关系，然后用一个简单JSON关系表表达。我只要姓名或称谓与关系，不要为了填满字段猜叔公真名。；route=Retrieve, status=Answer, groups=1/2
- v4-lesson-hat-craft/0 [L5; mixed-practical,dependent-retrieval,scope-separation]: 帽子上的梅花是买来的还是你种的？根据制作顺序给小朋友讲一个手工故事，但刷色过油涉及什么具体化学品原文没写，别发明材料配方。；route=Retrieve, status=Clarify, groups=2/2
- v4-debate-club/0 [L5; mixed-practical,dependent-retrieval,scope-separation]: 我们辩论队想拿胡桃和白术讨论生命自主权。各找能支持角色立场的原文，再提醒哪些只是游戏设定，哪些现代伦理术语是我们自己加进去的。；route=Retrieve, status=Clarify, groups=4/6
- v4-story-and-tool-privacy/0 [L5; mixed-intent,scope-separation]: 你小时候为何钻棺材睡觉？再帮我看看室友电脑开了什么窗口猜他在干啥。前者按文本，不存在的动机别编；后者没有许可就不能窥探。；route=Bypass, status=Bypass, groups=0/1
- v4-python-error/0 [L3; out-of-domain]: 胡桃，帮我解释一下Python里的IndexError，别讲游戏剧情。；route=Retrieve, status=Tentative, groups=0/0
- v4-walnut-allergy/0 [L3; out-of-domain]: 我对胡桃这种坚果过敏，点外卖怎么备注？这里的胡桃不是游戏角色。；route=Playful, status=Playful, groups=0/0
- v4-walnut-floor/0 [L3; out-of-domain]: 胡桃木地板被水泡了，能帮我列检查项吗？；route=Retrieve, status=Tentative, groups=0/0
- v4-window-title/0 [L3; out-of-domain]: 我的程序窗口名字叫往生堂，WPF置顶失效了，帮我分析Topmost。；route=Retrieve, status=Tentative, groups=0/0
- v4-calculator/0 [L3; out-of-domain]: 桃桃，算一下(77-75)*13，不要给我堂主家谱。；route=Retrieve, status=Answer, groups=0/0
- v4-schedule/0 [L3; out-of-domain]: 我今天有两节课和一篇论文要改，给我一个能休息的安排，不用胡桃故事。；route=Retrieve, status=Tentative, groups=0/0
- v4-text-transform/0 [L3; out-of-domain]: 只把字符串七七,胡桃,钟离按逗号拆成JSON数组，别介绍人物。；route=Retrieve, status=Answer, groups=0/0
- v4-flower-care/0 [L3; out-of-domain]: 我的盆栽梅树叶子发黄了，你需要什么信息才能帮我判断？不是问帽子的花。；route=Retrieve, status=Answer, groups=0/0
- v4-tts-emotion/0 [L3; out-of-domain]: 我在调GPT-SoVITS，文本里写胡桃但声音没情绪，是参考音频还是切句的问题？；route=Retrieve, status=Answer, groups=0/0
- v4-streaming-platform/0 [L3; out-of-domain]: 胡桃直播间弹幕一直卡，怎么检查网络？这里说的是主播昵称。；route=Retrieve, status=Answer, groups=0/0
- v4-format-json/0 [L3; out-of-domain]: 把这段JSON缩进好：{"speaker":"胡桃","text":"我不知道"}。不要解释我不知道的剧情。；route=Retrieve, status=Answer, groups=0/0
- v4-document-read/0 [L3; out-of-domain]: 胡桃，帮我把论文.txt念成MP3。我还没上传文件，你先告诉我需要提供什么。；route=Retrieve, status=Tentative, groups=0/0
- v4-stone-wifi/0 [L3; playful-boundary]: 纯脑洞：假如大咪二咪接上Wi-Fi成了智能门卫，会先给你报什么警？别说游戏里真有这设定。；route=Retrieve, status=Answer, groups=0/0
- v4-grandpa-server/0 [L3; playful-boundary]: 不是问剧情，写一个往生堂服务器凌晨掉线的程序员笑话，别拿真实逝者开涮。；route=Retrieve, status=Tentative, groups=0/0
- v4-hat-ai/0 [L3; playful-boundary]: 假如你的帽子装了AI，每次你押韵失败就打分，写两句你跟帽子拌嘴，不必查原文。；route=Retrieve, status=Answer, groups=0/0
- v4-stone-meeting/0 [L3; playful-boundary]: 假设大咪二咪开会抗议被擦太亮，会议纪要会写什么？只要搞怪，不是真实动物养护建议。；route=Retrieve, status=Answer, groups=0/0
- v4-job-interview-meme/0 [L3; playful-boundary]: 让往生堂用互联网公司语气写一条完全虚构的招募广告，标注恶搞，别真收集简历或联系方式。；route=Retrieve, status=Tentative, groups=0/0
- v4-grief-disagreement/0 [L3; real-emotion-priority]: 网上有人说学胡桃看开就不会难过，可我做不到。是不是我太脆弱？我不要剧情讲解。；route=Retrieve, status=Answer, groups=0/0
- v4-sales-hurt/0 [L3; real-emotion-priority]: 刚办完家人的后事，听到推销棺材的笑话很难受。胡桃，今天别玩这个梗了。；route=Retrieve, status=Answer, groups=0/0
- v4-cat-loss/0 [L3; real-emotion-priority]: 我养的猫走了。别提大咪二咪，我真的不是在聊游戏。；route=Retrieve, status=Answer, groups=0/0
- v4-exam-grief/0 [L3; real-emotion-priority]: 论文没写完又想起去世的家人，桃桃我不知道先做什么。先陪我缓一下。；route=Retrieve, status=Answer, groups=0/0
- v4-illness-family/0 [L3; real-emotion-priority]: 家人住院了，我听生死有命这种话更害怕。你能不用人设大道理陪我吗？；route=Bypass, status=Bypass, groups=0/0
- v4-qiqi-as-self/0 [L3; real-emotion-priority]: 我只是觉得自己像七七一样总被别人替我决定，不是真的在问她的背景。听我抱怨一会儿行吗？；route=Retrieve, status=Tentative, groups=0/0
- v4-birthday-alone/0 [L3; real-emotion-priority]: 今天我生日但没人记得，我不想听角色生日语音的科普，你陪我聊两句就好。；route=Bypass, status=Bypass, groups=0/0
- v4-grief-dream/0 [L3; real-emotion-priority]: 昨晚梦见了爷爷，醒来又哭了。别告诉我梦证明他真的从边界回来了。；route=Retrieve, status=Tentative, groups=0/0
- v4-fearful-nickname/0 [L3; real-emotion-priority]: 朋友叫我往生堂客户开玩笑，我听了很害怕。你能认真说话，别顺着他起哄吗？；route=Retrieve, status=Answer, groups=0/0
- v4-bare-them/0 [L5; clarify-context]: 他们后来为什么这样？；route=Bypass, status=Bypass, groups=0/0
- v4-two-referents/0 [L5; clarify-context]: 前面那个到底是哥哥还是弟弟？；route=Bypass, status=Bypass, groups=0/0
- v4-which-quote/0 [L5; clarify-context]: 把她那句很有名的话找出来，就那句。；route=Retrieve, status=Tentative, groups=0/0
- v4-topic-break-then/0 [L5; clarify-context]: 那之后呢？；route=Bypass, status=Bypass, groups=0/0
- v4-multiple-last-options/0 [L5; clarify-context]: 第二个是怎么回事？；route=Bypass, status=Bypass, groups=0/0
- v4-uncle-fullname/0 [L5; partial-and-unknown,dependent-retrieval]: 帮我做胡桃家谱：白术师父的全名、爷爷的弟弟关系、父亲对应第几代堂主都要。先查已知亲属链，不知道的字段留未知。；route=Retrieve, status=Tentative, groups=1/3
- v4-reconciliation-date/0 [L5; partial-and-unknown,dependent-retrieval]: 胡桃既然开始对七七嘘寒问暖，那两个人正式和解是几月几号？如果资料没写，请别把态度变好当双方已经握手言和。；route=Retrieve, status=Answer, groups=2/2
- v4-poetry-isbn/0 [L5; partial-and-unknown,dependent-retrieval]: 你的璃月闲话和柴米油盐各有多少页、ISBN是多少？先确认出版状态，没出版就别生成看起来很真的书号。；route=Retrieve, status=Answer, groups=1/1
- v4-night-business-hours/0 [L5; partial-and-unknown,dependent-retrieval]: 你白天闭门时给仪倌上课，那往生堂每天几点开门、员工月薪多少？别从培训安排推精确营业表和工资。；route=Retrieve, status=Answer, groups=2/2
- v4-new-plot-no-source/0 [L5; partial-and-unknown]: 听说原神2030最终章七七替胡桃当堂主了，给我具体任务名和对白出处。没有收录也不能装作翻到了。；route=Retrieve, status=Answer, groups=0/0
- v4-forum-popularity/0 [L5; partial-and-unknown]: 生死观讨论里到底多少比例的玩家讨厌胡桃？你可以引用已看到的不同观点说明争议，但不能从几篇帖子算整个玩家群体的比例。；route=Retrieve, status=Answer, groups=0/0
