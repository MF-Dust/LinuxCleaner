# LinuxCleaner

C# / .NET 10 编写的 Linux 清理 CLI，无 GUI、无第三方 NuGet 依赖。

借鉴 [DeepClean](https://github.com/Cotton059/Light-Help/tree/main/light/DeepClean) 的“扫描 → 确认 → 清理”和 [BleachBit](https://docs.bleachbit.org/doc/command-line.html) 的规则化预览方式。独立实现，不下载执行远程脚本，不按目录名称全盘猜测垃圾。

## 构建与快速使用

需要 Linux x64 / arm64、.NET 10 SDK；用户缓存的安全文件操作需要 glibc 提供 `statx`、Linux 5.8+（挂载点 ID）和已挂载的 `/proc`。实际运行验证环境为 Linux x64，ARM64 尚未实机验证。

```sh
dotnet build -c Release
dotnet run -c Release -- list
dotnet run -c Release -- scan
dotnet run -c Release -- scan --all --older-than 14 --verbose
dotnet run -c Release -- clean --all --dry-run
dotnet run -c Release -- clean                 # 预览后输入 DELETE 才删除
dotnet run -c Release -- clean cargo gradle flatpak --older-than 30
```

用户命令默认选择 `cache thumbnails browsers`，只处理 **7 天前最后修改**的文件。指定 ID 时只选指定规则；`--all` 显式选择全部 31 条用户规则，不能与 ID 混用，**绝不包含系统清理**。

`--older-than 0` 选择扫描开始前的所有候选文件，不代表文件未在使用。清理前请关闭浏览器、构建进程和相关应用；清缓存可能导致重新下载、重新编译，离线时尤其需要谨慎。

### 发布独立可执行文件

```sh
dotnet publish LinuxCleaner.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
./artifacts/linux-x64/linuxcleaner --help
```

arm64 改用 `-r linux-arm64`。使用 `--self-contained false` 时，目标机需要 .NET 10 Runtime。用户清理可以直接运行发布文件或放入 `~/.local/bin`，无需 root。

## 用户清理规则

表中“缓存目录”为 `$XDG_CACHE_HOME` 或默认 `~/.cache`；缺失的规则目录正常跳过，重叠规则中的同一路径只计数一次。

| ID | 处理范围 | 默认 |
| --- | --- | --- |
| `cache` | 其他应用缓存，排除浏览器、缩略图及已知含环境/历史/配置/离线媒体的缓存树 | 是 |
| `thumbnails` | 缓存目录下的 `thumbnails` | 是 |
| `browsers` | 缓存目录下的 `mozilla`、`google-chrome`、`google-chrome-beta`、`chromium`、`microsoft-edge`、`BraveSoftware`、`librewolf`、`vivaldi`/`vivaldi-snapshot`、`opera`/`opera-beta`/`opera-developer` | 是 |
| `npm` | `~/.npm/_cacache` | 否 |
| `nuget` | `~/.local/share/NuGet/http-cache` | 否 |
| `pip` | 缓存目录下的 `pip`，便于单独选择 | 否 |
| `yarn` | 缓存目录下的 `yarn`、`~/.yarn/berry/cache` | 否 |
| `go` | 缓存目录下的 `go-build`，不删除模块源码 | 否 |
| `cargo` | `~/.cargo/registry/cache` 下载归档，不删除 registry/src、git checkout、工具链或二进制 | 否 |
| `gradle` | `~/.gradle/caches` 构建/依赖缓存，不删除项目或 wrapper | 否 |
| `npm-logs` | `~/.npm/_logs` 历史调试日志 | 否 |
| `browser-profiles` | 默认浏览器配置路径内各 profile 的指定缓存子目录，详见下文 | 否 |
| `flatpak` | `~/.var/app/<应用 ID>/cache/**`，不进入 data/config | 否 |
| `snap` | `~/snap/<应用>/<版本或 common>/.cache/**`，不跟随 current 链接 | 否 |
| `composer` | 缓存目录下的 composer；旧 ~/.composer 下的 cache/files/repo/vcs，保留授权信息 | 否 |
| `poetry` | 缓存目录下的 pypoetry/cache 和 pypoetry/artifacts，保留 virtualenvs | 否 |
| `ccache` | 缓存目录下的 ccache 与 ~/.ccache，仅十六进制分桶/tmp，保留 ccache.conf | 否 |
| `sccache` | 缓存目录下的 sccache，不处理配置目录 | 否 |
| `vscode` | Code/Insiders、VSCodium、Cursor、Windsurf/Next、Antigravity、Devin/Next 的指定缓存，保留 User、扩展和备份 | 否 |
| `discord` | discord/ptb/canary/development 的 Code Cache/GPUCache；保留 HTTP 缓存、收藏 GIF 和登录数据 | 否 |
| `slack` | ~/.config/Slack 的 Cache/Code Cache/GPUCache，不进入 Local/Session Storage | 否 |
| `thunderbird` | 缓存目录下的 thunderbird，不删除邮件 | 否 |
| `thunderbird-profiles` | ~/.thunderbird、~/.mozilla-thunderbird、~/.config/thunderbird、~/.config/mozilla/thunderbird 下各 profile 的缓存 | 否 |
| `steam-shaders` | 默认 Steam 安装的 steamapps/shadercache，保留游戏、存档和 compatdata | 否 |
| `steam-cache` | 默认 Steam 的 appcache/httpcache，以及 htmlcache 中指定 Chromium 缓存，不整根清空 htmlcache | 否 |
| `mesa-shaders` | 缓存目录下的 mesa_shader_cache、mesa_shader_cache_db、mesa_shader_cache_sf | 否 |
| `gstreamer` | 缓存目录下的 gstreamer-1.0，不删除插件 | 否 |
| `fontconfig` | 缓存目录下的 fontconfig，不删除字体或配置 | 否 |
| `winetricks` | 缓存目录下的 winetricks、旧 ~/.winetrickscache，不进入 Wine 前缀 | 否 |
| `jetbrains` | 缓存目录下 JetBrains/<产品版本> 的 caches/index/tmp，不删除本地历史 | 否 |
| `opera` | ~/.config/opera、opera-beta、opera-developer 根或 Default 下的指定缓存，保留账号/会话 | 否 |

新增规则参考 BleachBit 源码和工具官方文档，路径、来源、保留策略和明确跳过的危险项目见 **[RULE_SOURCES.md](RULE_SOURCES.md)**。

**v0.3 的安全修正：** 通用 cache / Flatpak / Snap 规则不再整根清理 uv、pypoetry、JetBrains、ccache、spotify，以及 Google/AndroidStudio*。这些目录可能包含环境、本地历史、配置或离线媒体。需要时显式选择 poetry/jetbrains/ccache（或 --all），它们只进入限定的可重建子目录；uv、Spotify 和 Android Studio 仍保守保留。

### 浏览器配置内缓存

`browser-profiles` 只检查以下默认路径：

- `~/.mozilla/firefox/<profile>/`、`~/.librewolf/<profile>/`
- `~/.config/{google-chrome,google-chrome-beta,chromium,microsoft-edge,vivaldi,vivaldi-snapshot}/<profile>/`
- `~/.config/BraveSoftware/Brave-Browser/<profile>/`

只进入 profile **直接子目录**中的 `Cache`、`Code Cache`、`GPUCache`、`DawnCache`、`GrShaderCache`、`ShaderCache`、`DawnGraphiteCache`、`DawnWebGPUCache`、`cache2`、`startupCache`。不删除 History、Cookies、Login Data、places.sqlite、logins.json 或 Service Worker/CacheStorage 等离线网站数据，不递归搜索任意同名目录。

### 路径约定

遵循 XDG 对缓存可再生数据的约定，`cache` 不限于白名单应用。**自定义 `XDG_CACHE_HOME` 相当于声明整个目录是可清理缓存**：必须是 HOME 内的绝对路径，不能是 HOME、常见敏感目录或其祖先/子目录，不能通过符号链接访问。工具不能判断应用是否错误地把重要数据存进缓存，先用 `--verbose` 检查。

除 `XDG_CACHE_HOME` 外，规则使用表中的默认位置；不读取各工具的自定义缓存配置或 `XDG_CONFIG_HOME`。不删除 node_modules、已安装的 NuGet 包、项目内的 .yarn/cache、Cargo 源码或个人文件。

## 排除路径、大小筛选与大文件排行

```sh
# 最小逻辑大小 10 MiB，保留指定文件/目录，查看最大的 20 个候选
linuxcleaner scan --all --min-size 10MiB --top 20 \
  --exclude '.cache/pip' --exclude '~/.config/Code/Cache'

# 真正清理时同样提供筛选条件；不使用 --top
linuxcleaner clean --all --min-size 10MiB \
  --exclude '.cache/pip' --exclude '~/.config/Code/Cache'
```

- `--exclude` 可重复指定文件或目录；排除目录时也排除其全部子树，优先于 --all/具体规则。相对路径以 **HOME 而非当前工作目录** 为基准；支持绝对 HOME 内路径、`~` 和 `~/`。可排除不存在的路径；不支持 glob 或 `~其他用户`，不解析符号链接别名，HOME 外路径报错。相似前缀目录不受影响（排除 pip 不会排除 pip-other）。
- `--min-size` 为包含边界的逻辑字节数下限，默认 0。只接受非负整数，可带 B/KB/MB/GB/TB 或 KiB/MiB/GiB/TiB（不区分大小写）；KB=1000，KiB=1024，不接受小数。年龄限制仍然生效。
- `--top N` 范围 1–1000，按筛选后的候选大小降序排列，同大小按路径排序。**仅用于 scan 或 clean --dry-run**，不缩小候选范围，更不表示只删除前 N 项；实际 clean 拒绝该参数，避免误解。
- 这些参数只用于用户扫描/清理，不用于 system 命令。每次执行重新扫描，先前预览的参数不会自动保存。

## 系统清理：独立、显式、需要 root

系统维护不经过用户缓存规则，也不受 `--all` 或 `--older-than` 影响。

```sh
linuxcleaner system list
linuxcleaner system scan packages
linuxcleaner system scan packages --manager pacman --keep-packages 3
linuxcleaner system scan journal --keep-days 30
linuxcleaner system clean journal --keep-days 30 --dry-run

# 下面会真正清理：必须由你显式运行，交互模式仍需输入 DELETE
sudo ./artifacts/linux-x64/linuxcleaner system clean packages
sudo ./artifacts/linux-x64/linuxcleaner system clean journal --keep-days 30
```

**system scan / --dry-run 仅展示完整计划命令及保留策略，不启动任何外部工具，不估算可释放空间，也不生成文件快照。** 实际候选由发行版工具在执行时决定。这与用户缓存的逐文件预览不同。

| 操作 | 使用的系统工具 | 策略 |
| --- | --- | --- |
| `packages --manager pacman` | `/usr/bin/paccache --remove --keep 2 --nocolor` | 每个包至少保留两版本；需要 pacman-contrib |
| `packages --manager apt` | `/usr/bin/apt-get clean` | 清空已下载包缓存；不卸载包或 autoremove |
| `packages --manager dnf` / `dnf5` | `/usr/bin/dnf[5] clean packages` | 只清下载包，不清元数据、不卸载包 |
| `journal --keep-days 14` | `/usr/bin/journalctl --no-pager --vacuum-time=14d` | 只处理过期归档 journal 文件，不 rotate 或清理活跃日志 |

`--manager auto` 为默认值，根据固定位置的包管理器检测；多种管理器共存时拒绝猜测，需显式指定。DNF5 与 DNF 同时存在时优先 DNF5。

`--keep-packages` 范围 2–100，必须配合 `--manager pacman`；APT / DNF 不支持该策略。`--keep-days` 范围 1–36500，仅用于 journal。

实际系统清理需要 root，不自动调用 sudo。固定使用 `/usr/bin` 下的已安装工具，不允许指定任意可执行文件，不拼接 shell 命令；启动子进程前清空继承环境，固定 PATH 和工作目录，避免加载用户的 shell 启动钩子、APT_CONFIG 等环境设置。工具自身的可信系统配置仍然生效。不要在包管理器正在运行时清理下载缓存。

参考：[paccache 手册](https://man.archlinux.org/man/paccache.8.en)、[journalctl 手册](https://www.freedesktop.org/software/systemd/man/latest/journalctl.html)、[DNF5 clean](https://dnf5.readthedocs.io/en/latest/commands/clean.8.html)。

## 自动化、JSON 和退出码

```sh
linuxcleaner scan --all --json --verbose > preview.json
linuxcleaner clean cargo npm --older-than 30 --yes --json > result.json
linuxcleaner system scan packages --json > system-plan.json

# 真正的系统清理；--yes 显式授权，务必先预览
sudo ./artifacts/linux-x64/linuxcleaner system clean packages --yes --json > system-result.json
```

非交互或 JSON 模式的实际清理必须使用 `--yes`。每次运行重新扫描/构建命令，不加载旧预览文件。

- JSON 输出到 stdout，参数/环境错误输出到 stderr。
- 用户 JSON 的 `Warnings` 为扫描警告，`Result.Errors` 为删除错误，各保留最多 100 条并提供完整计数；有警告时仍可清理已安全扫描的部分。
- 用户 JSON 的 `Result` 在预览或没有候选文件时为 `null`。`--verbose` 在 `Files` 中加入候选路径；`Filters` 回显 MinimumBytes 与规范化后的 ExcludedPaths，`TopFiles` 为 --top 的排行（未指定时为 null）。总数/总大小始终是应用所有筛选后的完整候选，不是排行数量。
- 系统 JSON 包含 `Plan`、`DryRun`、`Result`；`Result` 保留系统工具的 ExitCode/Output/Error。每个输出流最多保存 65536 字符，超出后截断但继续读取，避免阻塞。
- 系统命令无法保证释放多少空间，也不解析工具输出为“已释放字节”。用户报告的大小是**逻辑文件大小**，不等于保证释放的磁盘空间（压缩、稀疏文件、快照、仍打开的文件都会影响）。

退出码：`0` 完成、无候选文件或取消；`1` 用户扫描警告/部分失败或系统工具返回非零；`2` 参数、权限或环境错误；`130` Ctrl+C 中断。中断时可能已删除部分文件；运行中的系统子进程会被终止，但已完成的清理无法撤销。

## 用户缓存的安全边界

- 用户 scan/clean 禁止 root，不自动提权，不扫描整个 HOME。
- 通过 `openat(O_NOFOLLOW)` 逐级打开目录，使用目录描述符和 `unlinkat`；符号链接和符号链接祖先拒绝访问。
- 只删除当前用户的普通文件；跳过硬链接、设备、socket、FIFO 和相对各规则根目录的子挂载点。无法获得完整 statx 元数据（含挂载点 ID）时拒绝清理。
- 清理前检查根目录身份及文件 inode、设备、所有者、链接数、大小、修改/变更时间；预览后变化的文件跳过。本次扫描后新增的文件不会被本次清理删除。
- 保留目录，不递归整目录删除；不清空回收站、不扫描全局 /tmp、不任意清空 /var/log。系统级包缓存/journal 必须经单独的 system 命令授权。
- **删除不可撤销。** Linux 没有“校验 inode 并原子删除对应目录项”的系统调用，校验与 unlinkat 间仍有极小并发替换窗口。不适合与恶意同用户进程或持续修改的缓存并发运行。
- 候选快照占用与文件数量成正比的内存；系统外部工具不具有这些 inode 快照保证。工具不是安全擦除或数据恢复软件。

## 测试

不依赖测试框架：

```sh
dotnet run --project tests/LinuxCleaner.Tests.csproj -c Release
```

测试以普通用户运行，用户清理只操作随机临时 HOME。系统清理使用假执行器验证命令、确认、权限和结果，不执行真实包缓存/journal 清理。进程执行器仅使用 env/head/false/sleep 等无清理副作用的命令验证环境、输出限制和中断。

覆盖年龄/大小筛选、路径排除、排行、全部 31 条规则、重叠去重、账号/源码/本地历史/离线媒体保护、链接/FIFO、权限、路径替换、JSON、--all、确认、系统保留策略和取消。ARM64、真实子挂载点以及各发行版工具的实际 root 清理尚未实机测试。
