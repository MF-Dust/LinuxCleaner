# 规则包与人工审核

## 信任边界

`rules/builtin.json` 是**随源代码审核、构建时嵌入**的规则包，不是可任意加载的用户配置。`CleanerMlImporter` 的报告是另一种格式，扫描器不会读取它；没有 `--catalog`、`--enable` 或 `Reviewed=true` 开关。不能靠修改报告让 HOME、密钥或配置目录成为可删除范围。

内置规则校验检查结构和路径模板，不是安全沙箱：开发者修改源码规则包仍然能够扩大删除范围，必须审查每个 diff 并补充数据保护测试。文件类型、所有者、挂载点、链接与预览后变化检查仍由原有 `CleaningEngine` / `NativeDirectory` 实施；JSON 不能关闭这些检查。

## 内置 JSON v1

顶层字段：

| 字段 | 含义 |
| --- | --- |
| `SchemaVersion` | 仅接受整数 `1` |
| `Provenance` | 来源说明；逐项依据另见 RULE_SOURCES.md |
| `Policies` | 命名的包含/排除策略 |
| `Rules` | ID 唯一的规则列表 |

规则字段：`Id`（小写字母、数字、连字符）、`Description`、`Default`、`Roots`（非空路径列表）、`Policy`（策略名或 null）。未声明字段、缺失必需构造字段及无效策略引用会使加载失败。

路径仅允许 `${HOME}/明确路径`、`${CACHE}/明确路径` 或 `${CACHE}`，不能有 `.` / `..`、空路径段、通配符、其他变量或控制字符。`${CACHE}` 根据经过原有 HOME/敏感目录边界检查的 `XDG_CACHE_HOME` 解析，未设置时为 `~/.cache`。`${HOME}` 自身不允许作为根目录。

例如（结构片段，完整内容见内置文件）：

```json
{
  "Id": "pip",
  "Description": "pip 下载和 wheel 缓存",
  "Default": false,
  "Roots": ["${CACHE}/pip"],
  "Policy": null
}
```

### 策略语义

每个策略具有 `Include` 和 `Exclude`，路径都相对于规则根目录，比较区分大小写。

- `Exclude` 优先，匹配指定路径及整个子树。每个路径段可为字面值、`*`（任意一个路径段）或末尾带 `*` 的前缀；不支持 `**`、正则或 shell glob。`Google/AndroidStudio*` 只检查对应的两层，不是全树查找。
- `Include=[]` 表示没有额外的包含限制。否则按顺序选择第一条 `WhenFirstSegment` 与首路径段相等、或该字段为 null 的选择器。null 是默认分支，只能放最后。没有匹配分支则不进入。
- `Index` 从 0 起，指定哪一层必须是 `Names` 中的缓存目录。更浅层只能作为**目录**遍历，匹配到的缓存名称本身也必须是**目录**；之后的普通文件才有机会进入候选。不能把同名普通文件误当缓存目录删除。

例如 VS Code 只进入根下指定缓存：`{"WhenFirstSegment":null,"Index":0,"Names":["Cache","GPUCache"]}`。Thunderbird 对 `Profiles` 使用 Index=2，否则 Index=1；Opera 对 `Default` 使用 Index=1，否则 Index=0。无需在 C# 中按应用 ID 添加新分支。

## CleanerML 受限转换

```sh
linuxcleaner rules import ./cleaner.xml \
  --source https://example.org/repository/COMMIT/cleaner.xml \
  --revision FULL_COMMIT_ID --license GPL-3.0-or-later > review.json
```

这里的 URL/版本只是格式示例，不是内置可信来源。下载操作由你完成；应固定真实提交，检查版权和许可，再运行导入。导入不联网，也不会验证 URL、提交和本地文件确实对应。

### 接受的子集

- 无命名空间的 `cleaner`，其中包含 label/description、var/value、option、action。
- `os` 缺省、`linux` 或 `unix` 参与转换；`windows`、`macos` 跳过；其他值报告不支持。
- 变量使用 CleanerML 的 `$$name$$` 语法，支持列表和有限递归；只选适用平台的值。未定义、循环、重复变量或过量展开被阻止。
- 动作必须是 `command="delete"`、`search="walk.all"` 或 `walk.files`；输出仅描述普通文件清理，不移植删除空目录的行为。
- 路径必须以 `$XDG_CACHE_HOME/`、`${XDG_CACHE_HOME}/`、`~/.cache/`、`$HOME/.cache/` 或 `${HOME}/.cache/` 开头，且是规范的明确目录，无通配符/未解析变量。
- 路径范围必须落在内置规则中**没有额外 Policy** 的 `${CACHE}/…` 根或其后代目录内。整个通用 cache、需要 Policy 的 ccache/JetBrains 等都不能借导入丢掉原有保护。新应用即使位于 `.cache`，也要先独立审核，不能自动进白名单。
- XDG 路径转换为 `${CACHE}/…`；字面的 `~/.cache` 路径转换为 `${HOME}/.cache/…`，不错误地把它改成自定义 XDG 路径。不会展开当前机器的任意环境变量。

`running` 等上游条件目前不移植，会明确报告不支持并阻止该 cleaner，而不是静默丢掉条件。配置内缓存、旧式非 `.cache` 路径、Flatpak/Snap 的新路径、glob、file 搜索、SQLite、JSON 修改、截断、shell 或包管理动作也不会被猜测转换。

### 报告与阻止策略

报告包括：

- `SchemaVersion=1`、`Status="PendingReview"`、`AutoEnabled=false`。
- `Source`：声明的 URL、Revision、DeclaredLicense，以及**实际输入字节**的 SHA-256。
- `CleanerId` 和 `SourceNotices`（原始 XML 注释，保留版权/许可证信息）。
- 全局 `Issues`、各 option 的 label/description、显式 warnings、问题和动作记录。
- `Status` 为 `PendingReview` / `Blocked` / `SkippedPlatform`；动作保留原始 Command/Search/Path，方便人工核对未转换部分。

全局未知条件阻止所有相关 option；option 的 warning、未知条件或任一不支持动作会阻止**整个 option**，其 `CandidateRoots` 为空。action 内的路径建议仅供解释转换结果，父 option 被阻止时绝不能单独拣选启用。

显式 `<warning>` 无条件阻止相应范围。自然语言 description 中的风险不做关键词猜测，必须人工阅读；没有 warning 绝不代表安全。

退出码 `0` 只是无阻止项（可能只有跳过项），`1` 是成功生成含阻止项的报告，`2` 是参数/XML/资源限制错误，`130` 是中断。生成报告不会修改输入或缓存文件。使用 shell 重定向时由 shell 创建/覆盖输出文件；不要把输出重定向到输入 XML。

### 资源与解析限制

- 输入最多 1 MiB，先读取有界快照，再以同一快照解析和计算哈希。
- DTD 禁止、XmlResolver=null；不接受内部/外部实体定义，不读取引用的本地文件或网络资源。
- XML 深度最多 32、读取节点最多 20,000；最多 128 个 option、256 个 action、64 个 var。
- 路径/变量展开中间字符串最多 4096 字符、递归最多 8 层；每个动作最多 128 个展开结果，整个文件最多 4096 个展开结果（去重前计数）。

## 真正启用：只能经过源码审核

1. 对照固定提交的原始 XML、官方应用文档和报告核实路径语义、版权/许可证。审查所有全局/option/action 问题，不能仅凭 `PendingReview` 放行。
2. 明确哪些缓存可再生、哪些邻近路径有环境、账号、离线数据或本地历史；不要直接将整个上游 option 原样复制进规则包。
3. 手动修改 `rules/builtin.json` 并更新 RULE_SOURCES.md。需要时新增明确的 Include/Exclude，不引入脚本或动态条件执行。新规则默认建议 `Default=false`；它仍会被 `--all` 选择，因此同样必须安全审核。
4. 为每条规则添加临时 HOME 测试：一个应删缓存、相邻重要文件必须保留；涉及 Policy 时测试祖先目录和同名普通文件。
5. `dotnet run --project tests/LinuxCleaner.Tests.csproj -c Release`，审查 diff 后重新构建/发布。先在隔离数据中 `scan --verbose` / `clean --dry-run` 验证，再由用户显式执行清理。

不导入 xdg-ninja/antidot 的 dotfile 迁移建议：迁移目标不是垃圾。不部分复刻 systemd-tmpfiles 的 x/X 等排除语义，系统临时文件交由原生工具管理。本项目没有上游自动同步、远程下载、插件执行或自动激活功能。
