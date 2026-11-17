Ori DE 简体中文补丁 1.0.0 / Simplified Chinese patch
================================================

中文
----
仅适用于《奥日与黑暗森林：终极版》
Ori and the Blind Forest: Definitive Edition（oriDE.exe）。
普通版未经测试，不保证兼容。

这是给未安装模组的零售游戏使用的独立简体中文补丁：
- 解锁游戏自带简体中文，添加 Options > Language > 简体中文，并记住选择；
- 安装内嵌的文楷中文字形图集，补齐缺失字形；
- 为游戏空白中文条目补译，修正术语，角色名字保留英文。
没有联机/本地合作代码，也没有额外菜单或其他游戏功能。

安装：
1. 退出游戏。将本压缩包全部解压到自己的游戏目录，与 oriDE.exe 放在一起。
2. 启动游戏，选择 Options > Language > 简体中文。
3. 此后会记住语言选择。高级设置位于 BepInEx\config\oricoop.zhpatch.cfg 的 [Language]。

请勿与 OriCoop 合作模组一起安装：OriCoop 已包含本补丁。
若同时发现 OriCoop，BepInEx 会禁用此独立补丁并在日志说明不兼容。

卸载：退出游戏，删除 BepInEx\plugins\OriZhPatch.dll 和
BepInEx\config\oricoop.zhpatch.cfg。如果 BepInEx 仅为此补丁安装，
也可删除 BepInEx 目录、winhttp.dll、doorstop_config.ini、.doorstop_version。
若还有其他 BepInEx 模组，不要删除这些共享加载器文件。

压缩包不含游戏文件、存档或游戏安装包，必须自备正版游戏。
补丁内嵌少量游戏原文、中文补译及术语片段；相应游戏内容的权利归
Moon Studios、Microsoft / Xbox Game Studios 等各权利人所有。
这是非官方、非商业玩家补丁，未获上述权利人认可或背书。
独立补丁没有 OriCoop 的倒下标记图片；随附 GAME-ASSETS-NOTICE.md 是项目通用声明。

字体使用 LXGW WenKai Light（霞鹜文楷），由 LXGW 及 Klee Project Authors 创作，
遵循 SIL Open Font License 1.1（OFL）。图集为嵌入式字形子集/格式转换。
完整版权和许可见 LXGW-WenKai-OFL.txt；加载器等第三方许可见 THIRD-PARTY-LICENSES.txt。

English
-------
ONLY for Ori and the Blind Forest: Definitive Edition (oriDE.exe).
The standard edition is untested; compatibility is not guaranteed.

A standalone Simplified Chinese patch for an UNMODDED retail game:
- Unlocks the game's built-in Chinese, adds 简体中文 to Options > Language,
  and remembers the selection.
- Installs the embedded WenKai Chinese glyph atlas, including missing glyphs.
- Supplies text for blank Chinese entries and corrects terms, keeping character names in English.
No co-op code, extra menus, or other gameplay features.

Install: close the game, unzip everything into your own game folder next to
oriDE.exe, start the game, then choose Options > Language > 简体中文.
Advanced settings: [Language] in BepInEx\config\oricoop.zhpatch.cfg.

Do NOT install alongside the OriCoop co-op mod: it already contains this patch.
BepInEx disables this standalone patch and logs the incompatibility if OriCoop is present.

Uninstall: close the game; remove BepInEx\plugins\OriZhPatch.dll and
BepInEx\config\oricoop.zhpatch.cfg. If no other mods use this loader, you can
also remove BepInEx, winhttp.dll, doorstop_config.ini, and .doorstop_version.
Keep shared loader files if you use other BepInEx mods.

No game files or saves are included. You need your own legitimate game copy.
The plugin embeds small game-text excerpts, translation fallbacks and term fragments;
rights to the game content remain with their respective owners, including Moon Studios
and Microsoft / Xbox Game Studios. This unofficial, non-commercial fan patch is not
endorsed by or affiliated with them. No co-op downed-marker image is included;
GAME-ASSETS-NOTICE.md is the shared project notice.

Font: LXGW WenKai Light, by LXGW and the Klee Project Authors, under the
SIL Open Font License 1.1 (OFL). The atlas is an embedded glyph subset/format conversion.
See LXGW-WenKai-OFL.txt for the full copyright and licence, and
THIRD-PARTY-LICENSES.txt for the bundled loader components' notices.
