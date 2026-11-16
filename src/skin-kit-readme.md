# Ori 第二玩家（P2）皮肤 · 美术包 / Ori P2 skin artist kit

## 这是什么

游戏里的 Ori 是**序列帧动画**，不是 3D 模型。所有动作的每一帧都已经画好，拼在下面这些大图（图集 / atlas）里。
你在这些图上直接重绘 Ori 的样子（换颜色、花纹、发光、换造型都行），我们把改好的图换给第二个玩家用，第一个玩家不变。

## 预览舞台（边画边看效果）

双击打开 `预览舞台_preview.html`（建议用 Chrome 或 Edge）：
1. 点 **“选择美术包文件夹”**，选中这个解压出来的文件夹。左边会列出全部 441 个动作，点哪个播哪个。
2. 点 **“加载改好的图”**，选你重绘好的 PNG，也可以直接把 PNG 拖进窗口。之后点 **“对比”**，原版和新版并排播放同一个动作。
3. 右边会显示当前这一帧在哪张图、哪个位置，改哪里一目了然。背景选 **“抠像前”** 能看到游戏实际拿到的图；如果在“夜晚森林”背景下人物身上有地方变透明了，就是那里的颜色离抠像色太近。

网页只在你的电脑上读图，不会上传任何东西。尺寸或文件名不对的图，网页会直接提示。

## 必须遵守的规则（不遵守游戏里会错位或出现色块）

1. **尺寸不变、文件名不变。** 每张图原来多大，交回来还是多大，文件名一字不改。
2. **每一帧的位置不变。** 每个 Ori 只能在它原来那块区域里改，不能挪动、缩放或换位置。
   `guides/` 里有每张图的参考图，框出了每一帧的范围。人物可以比原来稍微胖一点或瘦一点，但不要超出框。
3. **背景是抠像色，不是透明。** 原图没有透明通道，人物背后是纯红 `#FF0000` 或纯绿 `#00FF00`，游戏会把这个颜色抠掉。
   每张图用的是哪种颜色，见下表。
   - 交回来的图背景请保持原来的纯色。**也可以交透明背景的 PNG**，我们会自动补上抠像色。
   - **人物身上不要用跟背景接近的颜色**：红背景的图里不要画大红色，绿背景的图里不要画纯绿色，否则那一块会被抠成透明。
     在红背景图上想要红色调，用偏粉或偏橙的红，并检查抠像后的效果。
4. **蓝框的帧是横着放的**（在图里旋转了 90°）。黄框是正着放的。重绘蓝框时别弄错方向，光影要和其他帧一致。
5. 所有图要改成**同一套风格**。只改一部分的话，第二个玩家做某些动作时会闪回原版的样子。

## 要改的文件

`atlases/` 里是 Ori 本体的所有动作，**都需要改**。
`optional/` 里是可选的：`seinFeatherAtlas0` 是滑翔时拿的羽毛，`seinEffectsAtlas0` 是二段跳的特效。

| 文件 | 尺寸 | 背景色（抠像色） | 帧数 |
|---|---|---|---|
{{TABLE}}

`atlases.json` 列出了每一帧的名字和位置（x、y、宽、高，左上角为原点，`rotated` = 旋转 90°），写工具时可以用。

## 交付方式

把改好的 PNG 放进一个 zip，**文件名与原图完全相同**（比如 `seinPlatformingAtlas0.png`），直接放在 zip 根目录，不要套文件夹。
没改的图不用放进去。可以另附一个 `skin.txt`，写上皮肤名字和作者。

建议：先只改 `seinPlatformingAtlas0.png`（站立、跑步等常见动作）发回来，我们在游戏里试过效果再继续改剩下的图。

---

## English summary

Ori is a flipbook: every frame lives inside these atlas PNGs. Repaint Ori in place for player 2.
Rules: same image size and file name; never move or scale a frame (see `guides/`: yellow boxes are upright, **blue boxes are rotated 90°**);
the background is a chroma key (pure red or green, see the table), not transparency. Keep it, or return a transparent PNG and we fill it in.
Don't use colors close to the key color on Ori. Repaint every file in `atlases/` in one consistent style; `optional/` is the glide feather and the double-jump effect.
Open `预览舞台_preview.html` (Chrome/Edge), pick this folder, then load your repainted PNGs to play every animation side by side with the original.
Return a zip with the changed PNGs at its root, using the original file names. Start with `seinPlatformingAtlas0.png` so we can test it in game first.
