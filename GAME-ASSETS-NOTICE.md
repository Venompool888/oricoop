# 游戏原素材声明 / Game Assets Notice

## 中文

OriCoop 是一个免费、非商业的玩家自制模组，适用于《奥日与黑暗森林：终极版》（*Ori and the Blind Forest: Definitive Edition*）。

**发布的套件里有哪些来自游戏的内容**

已编译的 `OriCoop.dll`（release 里的套件 zip）内嵌了下面几项，它们来自游戏本身：

- **倒下标记图片**（`dead_marker.png`）：用游戏地图贴图里奥里的头像加工而成，用来标出倒下等待救援的队友。
- **`zh_extra.txt`**：我们为游戏中文本地化里空着的条目补的中文翻译。每条用对应英文原文的哈希值（SHA-1 前 16 位）做索引，文件里没有游戏英文原文；中文译文里保留了游戏的占位符（如 `[StructureInteraction]`）。
- **`zh_terms.txt`**：对游戏官方中文术语的少量修正，其中包含官方中文文本片段。

**权利归属**：这些内容的版权和其他权利归各自的权利人所有，包括 Moon Studios、Microsoft / Xbox Game Studios。"Ori" 及相关名称是其所有者的商标。OriCoop 与这些权利人没有任何关联，也没有得到他们的认可。

**使用范围**：上述内容只用于这个免费、非商业的模组，目的是让模组在游戏里正常显示。使用 OriCoop 必须拥有一份正版游戏，模组本身不包含、也不能替代游戏。

**源码仓库**：OriCoop 的源码里没有这些文件，没有它们也能编译、运行，只是少了倒下标记图片和这部分中文补译。独立中文补丁的仓库 [ori-de-zh-patch](https://github.com/Venompool888/ori-de-zh-patch) 包含 `zh_extra.txt`（只有哈希索引和我们的译文）和 `zh_terms.txt`，不含倒下标记图片。玩家可以用仓库里的脚本（`src/assets/dead-marker/make-dead-marker.py` 等）从自己的游戏副本生成。

**MIT 许可证只适用于我们自己写的代码**，不适用于上述游戏内容，也不授予任何与之相关的权利。

**删除请求**：权利人如果希望我们移除这些内容，请在 [GitHub Issues](https://github.com/Venompool888/oricoop/issues) 提出，我们会尽快移除，并从已发布的套件中撤下。

## English

OriCoop is a free, non-commercial fan-made mod for *Ori and the Blind Forest: Definitive Edition*.

**What in the released kit comes from the game**

The compiled `OriCoop.dll` in the release kit zip embeds the following items, which are derived from the game:

- **Downed-player marker image** (`dead_marker.png`), made from Ori's head in the game's world-map texture. It marks a teammate who is down and waiting to be revived.
- **`zh_extra.txt`**: our Chinese translations for entries that the game's own Chinese localisation leaves blank. Each one is keyed by a hash of the English line (first 16 hex digits of SHA-1), so the file holds no English game text; the translations keep the game's placeholders (such as `[StructureInteraction]`).
- **`zh_terms.txt`**: a few corrections to official Chinese terms, which contain fragments of the official Chinese text.

**Ownership**: copyright and all other rights in these items belong to their respective rights holders, including Moon Studios and Microsoft / Xbox Game Studios. "Ori" and related names are trademarks of their owners. OriCoop is not affiliated with or endorsed by them.

**Use**: these items are used only in this free, non-commercial mod so that it displays correctly in the game. You need your own legitimate copy of the game to use OriCoop. The mod neither contains nor replaces the game.

**Source repositories**: the OriCoop source code does not contain these files. The plugin builds and runs without them; only the marker image and those Chinese fixes are missing. The standalone Chinese patch repository, [ori-de-zh-patch](https://github.com/Venompool888/ori-de-zh-patch), contains `zh_extra.txt` (hash keys and our translations only) and `zh_terms.txt`, but no marker image. Players can generate them from their own copy of the game with the scripts in the repository (`src/assets/dead-marker/make-dead-marker.py` and others).

**The MIT licence covers only our own code.** It does not cover these game-derived items and grants no rights in them.

**Removal requests**: if a rights holder wants these items removed, please open an issue at [GitHub Issues](https://github.com/Venompool888/oricoop/issues). We will remove them promptly, including from published kits.
