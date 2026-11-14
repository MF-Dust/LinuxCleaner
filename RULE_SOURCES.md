# 清理规则来源与取舍

核对日期：2026-11-14。本文记录参考依据，不代表所有应用/发行版版本均已实机测试。

通过 Google 检索官方文档，并实际阅读以下源码快照中的规则；内置规则只参考路径事实与数据语义，不捆绑或执行上游 XML / 脚本，清理逻辑独立实现。路径和筛选策略位于 [rules/builtin.json](rules/builtin.json)，审核/导入流程见 [RULE_FORMAT.md](RULE_FORMAT.md)。独立的 `rules import` 只将调用者提供的本地 XML 转成待审报告，不会启用或执行上游规则：

- [BleachBit cleaners，fad04d2](https://github.com/bleachbit/bleachbit/tree/fad04d2cb58dab37e1187404df86add511a52e87/cleaners)
- [BleachBit 社区 cleanerml，c483658](https://github.com/bleachbit/cleanerml/tree/c48365810206ff337b8af6d11173ef6174edbfa5)；其中 pending 规则不是已验证的安全白名单，不能直接套用。
- [XDG Base Directory Specification](https://specifications.freedesktop.org/basedir/latest/)

## 桌面应用

| 本工具规则 | 依据 | 实际采用及明确不采用的内容 |
| --- | --- | --- |
| `vscode` | [vscode.xml](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/vscode.xml) | Code、Code - Insiders、VSCodium、Cursor、Windsurf/Next、Antigravity、Devin/Next 的指定 Cache/CachedData/CachedExtensionVSIXs/JS/GPU 缓存；不采用 backup、history、blob_storage 或清理 User 的动作 |
| `discord` | [discord.xml](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/discord.xml) | 仅 Code Cache/GPUCache。上游明确警告 HTTP Cache 清理可能影响收藏 GIF，因此本工具连 HTTP Cache 也保留；不清 Cookies、Preferences、Local/Session Storage |
| `slack` | [slack.xml](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/slack.xml) | Cache/Code Cache/GPUCache；不采用 cookies、history、vacuum 操作，不进入工作区数据库 |
| `thunderbird`、`thunderbird-profiles` | [thunderbird.xml](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/thunderbird.xml) | 独立缓存及 profile 的 Cache/cache2/startupCache；不删除邮件、msf 索引、通讯录、账号或密码 |
| `browsers`、`browser-profiles`、`opera` | [Vivaldi](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/vivaldi.xml)、[Opera](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/opera.xml)、[LibreWolf](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/librewolf.xml)，以及同目录的 chromium、firefox、brave、google_chrome、microsoft_edge | 只采用缓存目录；不采用 history、passwords、cookies、session、site_data、sync 或 SQLite/JSON 修改操作。Opera 支持单 profile 根与 Default 层 |
| `jetbrains` | [JetBrains 系统目录说明](https://www.jetbrains.com/help/idea/tuning-the-ide.html#system-directory) | system 目录包含缓存及本地历史，不能整体清空；本工具仅进入产品版本目录下的 caches/index/tmp，保留 LocalHistory/localHistory、其他未知目录和配置 |
| `fontconfig` | [Fontconfig 用户手册](https://fontconfig.pages.freedesktop.org/fontconfig/fontconfig-user.html)、[社区规则](https://github.com/bleachbit/cleanerml/blob/c48365810206ff337b8af6d11173ef6174edbfa5/pending/fontconfig.xml) | 仅 XDG 用户字体缓存，不采用系统 /var/cache、~/fontconfig 或配置目录的广泛匹配 |
| `gstreamer` | [GStreamer registry 文档](https://gstreamer.freedesktop.org/documentation/gstreamer/gstregistry.html) | XDG 缓存目录中的 gstreamer-1.0，不删除已安装插件 |

## 开发工具

| 规则 | 依据 | 实际采用及保护 |
| --- | --- | --- |
| `composer` | [Composer cache-dir](https://getcomposer.org/doc/06-config.md#cache-dir)、[社区历史规则](https://github.com/bleachbit/cleanerml/blob/c48365810206ff337b8af6d11173ef6174edbfa5/release/composer.xml) | XDG composer 下载缓存、旧 ~/.composer 下的 cache/files/repo/vcs；保留 auth.json、config.json、项目 vendor |
| `poetry` | [Poetry cache-dir / virtualenvs.path](https://python-poetry.org/docs/configuration/) | 只处理 pypoetry/cache 和 pypoetry/artifacts。virtualenvs 默认位于同一缓存根，不能删除整个 pypoetry |
| `ccache` | [ccache 官方手册](https://ccache.dev/manual/latest.html) | XDG ccache 和旧 ~/.ccache 中的十六进制分桶及 tmp；保留 ccache.conf、根统计文件和未知内容。不采用社区 pending/ccache.xml 的整根删除 |
| `sccache` | [sccache 存储选项](https://github.com/mozilla/sccache#storage-options) | XDG sccache 对象缓存；配置目录 ~/.config/sccache 不在规则内 |
| `winetricks` | [Winetricks 源码中的 W_CACHE 默认值](https://github.com/Winetricks/winetricks/blob/master/src/winetricks)、[BleachBit 历史缓存路径](https://github.com/bleachbit/bleachbit/blob/fad04d2cb58dab37e1187404df86add511a52e87/cleaners/winetricks.xml) | XDG winetricks 和旧 ~/.winetrickscache 下载内容；不进入 ~/.wine 或其他 Wine 前缀 |

现有 npm / NuGet / pip / Yarn / Go / Cargo / Gradle 规则继续只使用 README 中声明的默认缓存路径，不动态读取或执行工具配置；不删除安装包目录、项目源码或工具链。直接清理缓存不替代各工具自身对锁和统计信息的管理，请先停止相关构建进程。

### 明确跳过的“缓存”

- [uv 官方缓存说明](https://docs.astral.sh/uv/concepts/cache/)：缓存必须由 uv 管理，符号链接安装模式还可能直接依赖缓存文件。本工具不增加 uv 整根删除规则，并从通用用户/沙箱缓存扫描中排除 uv。请自行使用 uv 官方维护命令并留意链接模式。
- [Poetry](https://python-poetry.org/docs/configuration/) 的 pypoetry/virtualenvs 是运行环境，不是下载垃圾。
- [JetBrains](https://www.jetbrains.com/help/idea/tuning-the-ide.html#system-directory) 的 Local History 不可重建；Google/AndroidStudio* 系统目录也按保守策略整体跳过通用清理。
- Spotify 缓存可能混入离线媒体，因此通用规则保留 spotify 整树，不提供整根删除规则。
- Conan、Maven 仓库、pnpm/Bun 包存储等可能包含本地构建产物、配置或被环境引用的内容，本轮不添加粗暴整根删除规则。

这些已知敏感缓存根的保护也应用到 Flatpak 的 cache 与 Snap 的 .cache；显式 poetry/jetbrains/ccache 规则只重新进入经过限定的安全子树。

## 游戏与图形缓存

| 规则 | 依据 | 实际采用及保护 |
| --- | --- | --- |
| `steam-cache` | [社区 Steam 规则](https://github.com/bleachbit/cleanerml/blob/c48365810206ff337b8af6d11173ef6174edbfa5/pending/steam.xml)，结合 Chromium 缓存与用户数据分离布局 | 仅 appcache/httpcache 和 htmlcache 中明确的 Cache/Code Cache/GPUCache 子目录；**不直接删除整个 htmlcache**，不清 Cookie、账号或 userdata |
| `steam-shaders` | [Arch Steam 文档](https://wiki.archlinux.org/title/Steam)、[Valve Fossilize](https://github.com/ValveSoftware/Fossilize) 对游戏着色器缓存的说明 | 默认 Steam 安装下 steamapps/shadercache；不进入 steamapps/common、compatdata、userdata、config 中的个人数据 |
| `mesa-shaders` | [Mesa 环境变量文档](https://docs.mesa3d.org/envvars.html) | mesa_shader_cache、mesa_shader_cache_db、mesa_shader_cache_sf；删除后会重编译着色器，可能短暂卡顿 |

Steam 仅识别 ~/.local/share/Steam 和真实目录形式的 ~/.steam 下的明确路径。不跟随 ~/.steam/steam、root 等符号链接，不解析库配置，不承诺覆盖自定义游戏库。源码参考规则中的 Windows 路径、模糊匹配和危险动作没有移植。

## 核验方式与局限

每条新规则都有隔离 HOME 测试：创建一个候选缓存和一个相邻重要文件，只允许前者删除；还测试全部规则重叠去重、编辑器历史/备份保护、Steam 存档保护、环境和本地历史保护。

这证明本工具的路径边界及删除逻辑，并不等于对所有应用版本的缓存格式做了端到端验证。应用升级或非标准布局应先 scan --verbose / --top 检查；不识别的布局宁可不清理。
