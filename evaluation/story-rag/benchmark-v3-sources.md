# v3 复杂题素材来源

## 金标来源

v3 的每个 `evidence_groups` 都指向仓库内可复现的逐句记录，评测启动时会校验 ID 存在：

- `character:*`：`data/story/character/hutao.json`，保留角色故事的完整段落。该文件记录了 GenshinData `TextMapCHS.json` 的 revision 与 SHA256。
- `textmap:*`：`data/story/dialogue/chapters/*.jsonl`，来自游戏本体文本抽取，保留任务、子任务、序号、说话人和原文。
- `archive:*`：`data/story/dialogue/pages/records/*.json` 的补充档案页面；只作为单行证据，不和任务对话混作同一场景。

v3 当前实际使用的主要任务证据包括：

| 主题 | 原文 ID 前缀 | 用途 |
|---|---|---|
| 胡桃/白术/叔公因果链 | `textmap:main-quest-11027:6:*` | 往生堂起源、魔神残渣、改行、兄弟关系与不确定性 |
| 往生堂历史与生死观 | `textmap:main-quest-11113:4:*`、`:5:*` | “类似医生”、魔神战争、历代堂主与无憾 |
| 公子—钟离介绍链 | `textmap:main-quest-1010:1:*`、`:2:*`、`:3:*` | 承诺、兑现、说话人和客卿身份 |
| 胡桃原话跨场景 | `textmap:main-quest-40222:12:402221211:0`、`textmap:main-quest-40014:1:400140202:0` | 俏皮台词与说话人归属 |
| 胡桃业务实践 | `textmap:main-quest-11110:2:111100406:0` | 冒险家协会合作条件 |

## 网络资料的边界

联网可以用来发现候选章节、角色别名或可能的难例，但不能把 Wiki/搜索摘要直接写进金标。本轮以本地逐句抽取为唯一事实来源；网络访问在当前环境不可稳定使用，因此没有把外部页面当作证据，也没有下载未审计剧情文本。若后续补充网页素材，应同时记录页面标题、URL、访问日期、版本，并在 `data/story` 找到对应原句后才加入 benchmark。

## 复现与审计

运行器会在评测前导出当前语料 ID 集，任何不存在的金标都会直接失败。`relation_chain`、`independent_claims`、`expected_corrections` 和 `forbidden_inferences` 只服务白盒诊断与人工盲评，不参与检索，避免测试答案泄漏到索引。
