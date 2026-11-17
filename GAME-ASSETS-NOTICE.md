# 游戏原素材声明 / Game Assets Notice

## 中文

OriCoop 是一个免费、非商业的玩家自制模组，适用于《奥日与黑暗森林：终极版》（*Ori and the Blind Forest: Definitive Edition*）。

**发布的套件里有哪些来自游戏的内容**

已编译的 `OriCoop.dll`（release 里的套件 zip）内嵌了下面几项，它们来自游戏本身：

- **倒下标记图片**（`dead_marker.png`）：用游戏地图贴图里奥里的头像加工而成，用来标出倒下等待救援的队友。
- **`zh_extra.txt`**：游戏英文原文，以及我们为这些文本补的中文翻译（游戏自带的中文本地化里这些条目是空的）。
- **`zh_terms.txt`**：对游戏官方中文术语的少量修正，其中包含官方中文文本片段。

**权利归属**：这些内容的版权和其他权利归各自的权利人所有，包括 Moon Studios、Microsoft / Xbox Game Studios。"Ori" 及相关名称是其所有者的商标。OriCoop 与这些权利人没有任何关联，也没有得到他们的认可。

**使用范围**：上述内容只用于这个免费、非商业的模组，目的是让模组在游戏里正常显示。使用 OriCoop 必须拥有一份正版游戏，模组本身不包含、也不能替代游戏。

**源码仓库不包含这些内容**：GitHub 上的源码里没有这些文件。没有它们也能编译、运行，只是少了倒下标记图片和这部分中文补译。玩家可以用仓库里的脚本（`src/assets/dead-marker/make-dead-marker.py` 等）从自己的游戏副本生成。

**MIT 许可证只适用于我们自己写的代码**，不适用于上述游戏内容，也不授予任何与之相关的权利。

**删除请求**：权利人如果希望我们移除这些内容，请在 [GitHub Issues](https://github.com/Venompool888/oricoop/issues) 提出，我们会尽快移除，并从已发布的套件中撤下。

## English

OriCoop is a free, non-commercial fan-made mod for *Ori and the Blind Forest: Definitive Edition*.

**What in the released kit comes from the game**

The compiled `OriCoop.dll` in the release kit zip embeds the following items, which are derived from the game:

- **Downed-player marker image** (`dead_marker.png`), made from Ori's head in the game's world-map texture. It marks a teammate who is down and waiting to be revived.
- **`zh_extra.txt`**: original English game text, plus our Chinese translations for entries that the game's own Chinese localisation leaves blank.
- **`zh_terms.txt`**: a few corrections to official Chinese terms, which contain fragments of the official Chinese text.

**Ownership**: copyright and all other rights in these items belong to their respective rights holders, including Moon Studios and Microsoft / Xbox Game Studios. "Ori" and related names are trademarks of their owners. OriCoop is not affiliated with or endorsed by them.

**Use**: these items are used only in this free, non-commercial mod so that it displays correctly in the game. You need your own legitimate copy of the game to use OriCoop. The mod neither contains nor replaces the game.

**Not in the source repository**: the source code on GitHub does not contain these files. The plugin builds and runs without them; only the marker image and those Chinese fixes are missing. Players can generate them from their own copy of the game with the scripts in the repository (`src/assets/dead-marker/make-dead-marker.py` and others).

**The MIT licence covers only our own code.** It does not cover these game-derived items and grants no rights in them.

**Removal requests**: if a rights holder wants these items removed, please open an issue at [GitHub Issues](https://github.com/Venompool888/oricoop/issues). We will remove them promptly, including from published kits.
