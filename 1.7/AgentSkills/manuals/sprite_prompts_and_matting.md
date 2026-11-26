# 画风 1 指定格式与贴图处理工具说明（sprite_prompts_and_matting）

## 一、画风 1（唯一指定画风）与提示词校验规则

`generate_image` 的 `prompt` 参数由 `BuiltInPromptGuard` 执行强制字面校验，必须 100% 逐字保留以下四段式英文结构中的固定原文（只允许填充尖括号槽位及部件描述，不得删改或缩写任何内置固定词句）：

```text
Strict 90-degree overhead orthographic top-down modular part sheet on a SOLID PURE FLAT #00FF00 background with wide green spacing between parts and zero cast shadows. <N> separated components all strictly facing UP toward 12 o'clock:
(Part 1) <英文描述内容>;
...
Color palette: primary surfaces in <LIGHT_HEX> stepping through <MID_HEX> to <SHADOW_HEX>, secondary structural blocks in <STRUCT_HEX>, and accent details in <ACCENT_HEX>; darkest value anywhere clamped at <DARKEST_HEX_LUM_GE_40>, no bright specular rim highlights, no glossy lip-gloss highlights.
Art medium and brushwork: clean lineless vector-and-airbrush industrial sci-fi illustration with zero lineart layer, zero wireframe, and zero brush or sponge grain—combining smooth curved cylindrical shells, circular rings, and rounded-arc armor cowlings shaded with continuous soft cylindrical airbrush gradients alongside rich multi-layered overlapping chamfered armor plates and stepped mechanical collars with crisp edge-to-edge tonal steps (never simplistic empty pills, and strictly NO triangular or diamond low-poly crystal tessellation), meeting neighboring shapes and #00FF00 directly edge-to-edge.
No text.
```

### `BuiltInPromptGuard` 校验参数说明

1. **槽位格式**：
   - `<N>`：仅填纯数字正整数（如 `1`、`2`、`4`），后面必须**一字不差原样紧接** ` separated components all strictly facing UP toward 12 o'clock:`（严禁在 `components` 与 `all strictly` 之间插入 `side by side`、`in two columns` 等任何额外英文词；排版要求统一写在 `(Part 1)` 槽位内部）。后续换行紧跟 `(Part 1)` 等描述。
   - 颜色色值槽位：6 位十六进制 `#RRGGBB`（后可附带英文色名说明）；其中 `<DARKEST_HEX_LUM_GE_40>` 的明度 `0.299*R + 0.587*G + 0.114*B` 必须 `>= 39.5`（例如 `#3E4248` 或更亮）。
   - 绿幕抠图色差：背景固定为纯 `#00FF00`，前景颜色需满足 `G - max(R, B) <= 20` 以免被绿幕抠除。
2. **自定义槽位禁用词清单（命中即抛错拦截）**：
   - 第三方或内置名称：`RimWorld`, `Monster Hunter`, `Rathalos`, `Milira`, `Monolyn`, `DMS`, `UFHI`, `Wolfein`, `MechScorpion`, `AbyssalDragonBeast`, `MechColossus`, `ProtossCarrier`, `PanzerIV`。
   - 线条诱发禁用词：`2D sprite`, `cel-shaded`, `3D CAD model`, `wireframe`, `outer contour`, `black`, `lines`, `panel recesses`, `seam`, `outlines`, `2px highlight strips`, `thin trim ribbons`, `inlay strips`, `access hatches`, `slits`, `color-field painting`, `flat solid patches`, `paper-cutout`。
3. **参考图与防图生图限制**：
   - 严禁传入任何用户提供、外部导入或模组既有的图片；`referencePaths` 仅接受当前生物 `Work/` 目录下经 `generate_image` 签名认证的自产 PNG。

## 二、抠图与 `512x512` 变换工具参数说明

1. **`extract_sheet`**
   - `inputPath`：`Work/` 下由 `generate_image` 生成的 `#00FF00` 绿幕 PNG 相对路径。
   - `outputDir`：切分结果输出子目录（生成 `part_0.png .. part_N.png` 及每件的 `width`、`height`、`columnBox`、`cropOffset`）。
   - `columns`：`0`（默认）按水平空白自动分栏；`>= 1` 按等宽列数切分。
   - `fillAllHoles`：`false`（默认）保留内部封闭绿幕镂空为透明孔；`true` 填充内部封闭绿洞。

2. **`warp_bone`**
   - 画布坐标系：输出固定为 `512x512` RGBA PNG，连续 UV 像素坐标原点 `(0, 0)` 位于**左上角**，`x` 向右递增（`0..512`），`y` 向下递增（`0..512`）；画布几何中心 `UV = (0.5, 0.5)` 对应连续像素坐标 `(256.0, 256.0)` 与 `0..511` 纹素数组中心 `(255.5, 255.5)`，水平翻转 `FlipHorizontal` 满足纹素索引映射 `x' = 511 - x`（连续像素坐标 `u' = 512.0 - u`）。
   - `srcPivot` / `dstPivot`：源切片上的枢轴像素 `'x,y'` 映射到目标 `512x512` 画布上的 `'x,y'`（默认 `'256,400'`）。
   - `scale` / `angleDeg`：缩放倍率 `'sx,sy'`（或单值 `'s'`）与顺时针旋转角度（度）。
   - `drawSize`：该骨骼在 `GiantRigDef` 中的最大世界格尺寸（用于换算并沿 Alpha 外边界向内注入 `#14161A` 外轮廓像素宽度）。
   - `maxInteriorBlackPct`：剥离外轮廓后的深层内部近黑像素占比门禁百分比（默认 `'1.0'`，传负值关闭）。
   - `sockets`：字符串数组 `'name:x,y'`（源切片坐标），返回变换至 `512x512` 后的目标像素坐标 `sockets[name].xml`。
   - `mirrorOutputPath`：可选对称贴图输出路径（位于 `Textures/` 下）。系统围绕 `dstPivot` 原地水平翻转导出镜像 PNG，返回 `mirrorPivotPixel`（与 `dstPivot` 的 `x` 相同，直接填入 XML `<pivotPixel>`，无需再做 `512 - x` 计算）与 `mirrorSockets`。

3. **`preview_sheet`**
   - `inputPaths`：工作区内待拼合预览的 PNG 路径数组。
   - `outputPath` / `tileSize`：输出深灰双色棋盘格总览图路径及单格像素尺寸（`64..512`，默认 `256`）。
