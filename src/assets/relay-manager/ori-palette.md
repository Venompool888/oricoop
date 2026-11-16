# Forest co-op UI — Unity OnGUI rebuild guide
All six PNGs: 1920×1080; built-in image generation, then bicubic resampling.
Generation prompt set: native forest serif UI; method / hosting / join / connection states / HUD / disconnect.
These are rebuild targets from the generation prompts; generated pixels and letterforms vary.
- Background navy: #07131F; deep teal: #0C2935; moonlit blue: #164B70.
- Primary ivory: #DDEAE5; selected white: #F1FCFF; secondary: #8AA7AB.
- Cyan glow: #91DFF4; signal/motes: #63DDE8; moss accent: #617747.
- Controller A: #91C95B; controller B: #D76659; caution uses secondary ivory.
- Chinese: Noto Serif SC Light (300), Regular (400) for smaller labels.
- English: Cormorant Garamond Regular (400), with modest tracking for codes.
- Both font families use SIL OFL 1.1, permitting commercial embedding; retain license notices.
- [Noto Serif license](https://github.com/notofonts/noto-cjk/blob/main/Serif/LICENSE) · [Cormorant license](https://github.com/CatharsisFonts/Cormorant/blob/master/OFL.txt).
- Typography at 1080p: heading 60–68px; menu Chinese 46px / English 25px; helper 24/20px.
- Chinese above English, 10–13px line gap; menu groups 40–56px apart; center alignment.
- Safe margin: 80px; bottom-right prompts 24px, 24px icon rings, 36px between actions.
- Room code: 120–144px; six join slots about 112×96px, 32–40px horizontal gaps.
- Letter grid: 4×8, minimum 80×68px controller hit areas, 44px serif capitals; omit I/O/0/1.
- Selected item: cyan horizontal streak 280–420px wide; 3px core, 12px bloom at 25% opacity.
- Text shadow: #02080E at 65%, offset (0,2px); selected text halo 6–10px at 15%.
- Progress ring: 160px target diameter, 3px core, 12px soft bloom, incomplete arc and motes.
- HUD: name 24px, latency 20px; toast Chinese 34px / English 22px; signal bars 6×8/14/20/26px.
- HUD concept shows visible-partner tag and off-screen locator together as alternate-state examples.
- Disconnect: feathered #02080E scrim at roughly 55–65%; no opaque dialog rectangle.
- OnGUI: scale all coordinates by Screen.height/1080; center the 1920px reference width.
- No solid menu panels; reserve thin outlines for code slots; feather glow textures at their edges.
