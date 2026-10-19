# Revit MCP — English asset production prompts

Product name: **Revit MCP**. Publisher: **OOXGN**. Platform: Autodesk Revit 2026 for Windows. The product connects a desktop building model to a local MCP client. The visual identity should communicate precise engineering work and a dependable local connection.

The supplied OOXGN artwork is authoritative. Attach `docs/assets/ooxgn-wordmark-dark.svg`, `ooxgn-wordmark-light.svg`, `ooxgn-monogram-dark.svg` and `ooxgn-monogram-light.svg` as reference files wherever a prompt mentions the publisher mark. Reuse their exact outlines. A new product symbol may be designed; the OOXGN logo must not be redesigned.

## Art direction shared by every prompt

- Palette: black `#000000`, charcoal `#2B2B2B`, warm white `#FAF9F7`, light divider gray `#D9D9D9`, supporting gray `#9CA3AF`, restrained slate gray `#6B7280`.
- Flat, compact, functional engineering graphics. Consistent geometry, generous internal negative space and deliberate optical alignment.
- No gradients, shadows, glow, glass, metallic effects, three-dimensional mockups, decorative X shapes, robots, brains, sparkles, circuit-board decoration or generic AI imagery.
- No Autodesk/Revit logos, copied application icons, upstream product marks, watermarks, incidental text or invented lettering.
- Typography for composed graphics: Neue Haas Grotesk or Helvetica Neue if licensed; Segoe UI is the practical Windows fallback. Keep text as editable layers and do not distribute unlicensed font files.
- Export isolated assets as individual files. Do not deliver a contact sheet, a branding presentation or a photographed screen instead of the requested asset.

## Production inventory

| Asset | Intended files | Required exports | Current implementation |
| --- | --- | --- | --- |
| Product symbol | `revit-mcp-app-master.svg`, `revit-mcp-app-1024.png` | Square master; PNG at 16/24/32/48/64/128/256/512/1024 px | No dedicated product symbol |
| Connection command | `connection-light-*`, `connection-dark-*` | Transparent PNG at 16 and 32 px; editable vector master | Reuses the OOXGN monogram |
| Auto-confirm command | `auto-confirm-light-*`, `auto-confirm-dark-*` | Transparent PNG at 16 and 32 px; editable vector master | Monochrome programmatic check mark |
| Installer icon | `installer/setup.ico` | ICO containing 16/24/32/48/64/128/256 px | Standard Windows executable icon |
| Publisher/product lockup | `revit-mcp-lockup-light.svg`, `revit-mcp-lockup-dark.svg` | SVG plus transparent PNG at 2× and 4× | README uses the existing OOXGN wordmark |
| GitHub social preview | `revit-mcp-social-1280x640.png` | 1280 × 640 px PNG plus editable master | No custom preview |
| Optional release cover | `revit-mcp-0.0.1-release-1600x900.png` | 1600 × 900 px PNG plus editable master | No release cover |

New artwork listed above is specified here and is not included as a generated image in version 0.0.1. Existing OOXGN SVG files remain usable without regeneration.

## 1. Dedicated product symbol

**Prompt — use independently with the common art direction:**

> Design one original, neutral application symbol for a Windows desktop engineering plugin named Revit MCP, published by OOXGN. The symbol must express a precise local connection to a building information model, without relying on letters, a robot, a brain, a magic wand or the Autodesk/Revit logo. Build a single compact silhouette from a simplified orthogonal model frame and two opposing connection terminals meeting across a small central gap. The relationship between the frame and the terminals should be structurally clear, as if both belong to the same engineered object. Use a front-facing, flat, two-dimensional construction with no perspective and no decorative geometry. Keep the number of visual parts low: one main frame, two terminals, and at most one small joining element. Use consistent stroke weights and squared or subtly chamfered corners. Make the silhouette recognizable at 16 pixels and balanced at 256 pixels. Design the master on a 32 × 32 unit grid with the live artwork inside a 26 × 26 unit safe area; keep a full 3 unit clear margin. Do not place the OOXGN wordmark inside the small symbol. The publisher identity is handled separately. Produce a transparent 1024 × 1024 pixel master, using only warm white #FAF9F7, charcoal #2B2B2B and black #000000. Also provide the same exact symbol in black for a light background and in warm white for a dark background. Present each version as an individual clean asset, without captions, sample devices, surrounding UI or a contact sheet. Supply an editable vector version with outlined geometry and no linked fonts. If vector output is unavailable, keep the raster edges crisp and suitable for faithful vector tracing.

**Export details:** preserve the same outer silhouette across all sizes. Redraw the 16 px export optically on its own pixel grid: important strokes should remain at least 1.5–2 px wide, with no subpixel gaps that disappear. Export a 32 px version with 2 px principal strokes. The icon must remain distinguishable from the connection and auto-confirm command icons.

## 2. Connection / restore local bridge command

**Prompt:**

> Create one compact ribbon command icon for the action “Connect / restore local MCP bridge” in a professional Windows building-modeling application. Use a direct engineering metaphor: two equal rectangular connector ends facing each other horizontally, joined by one short uninterrupted central bridge. The connector ends should be visibly separate components, but the overall form must read as a single stable connection. Use orthogonal lines and a flat front view. Do not include a cable coil, a globe, a cloud, radio waves, arrows circling the icon, a padlock, lettering or a decorative enclosing badge. This is a local connection and recovery action; it does not represent an internet service or security certification. Construct the icon on a 32 × 32 unit grid, with all meaningful artwork within x=4..28 and y=6..26. Use a uniform 2 unit stroke with balanced internal gaps. Provide one transparent warm-white #FAF9F7 version for a charcoal #2B2B2B ribbon and one transparent black #000000 version for a light ribbon. Produce a disabled-state counterpart of the same geometry using supporting gray #9CA3AF on dark surfaces and slate gray #6B7280 on light surfaces. Keep all four files geometrically aligned. The design language is OOXGN: quiet monochrome geometry, no gradients, shadows, glow, blue highlights or AI decoration. Deliver separate editable vector masters and separately optimized transparent PNG files at exactly 16 × 16 and 32 × 32 pixels. No text, watermark, screenshot, contact sheet or surrounding ribbon.

**File names:** `connection-dark-16.png`, `connection-dark-32.png`, `connection-light-16.png`, `connection-light-32.png`; add `-disabled` before the size for disabled counterparts. “Dark” means the file is intended for a dark surface. Do not include the preview background in transparent exports.

## 3. Auto-confirm command

**Prompt:**

> Design one monochrome ribbon icon for “Automatic confirmation of local model operations” in Revit MCP, an engineering desktop plugin. Use one clear check mark inside a simple open rectangular outline that suggests a small operation panel. The check should be the dominant element, centered optically and immediately recognizable at 16 pixels. Use the exact stroke weight, corner treatment and overall visual density of the supplied connection command icon. Build on a 32 × 32 grid with a 3 unit safe margin and a 2 unit principal stroke. Keep the check mark clear of the outline by at least 2 units. Do not use a shield, lock, lightning bolt, robot, brain, gear, green circle or text: this action controls confirmation behavior and must not imply that changes have been certified as safe. Produce transparent warm-white #FAF9F7 artwork for a charcoal interface and black #000000 artwork for a light interface. Supply matching disabled versions using #9CA3AF for dark surfaces and #6B7280 for light surfaces. If an enabled/disabled toggle-state pair is requested, preserve the outline in both and change only the presence of the check; do not introduce a second metaphor or new color. Deliver separate editable vector masters plus hand-tuned transparent 16 × 16 and 32 × 32 pixel PNG exports. Every file must contain one isolated icon and no surrounding application UI or labels.

**File names:** `auto-confirm-dark-16.png`, `auto-confirm-dark-32.png`, `auto-confirm-light-16.png`, `auto-confirm-light-32.png`; use `-disabled` for unavailable-control states. Future toggle-state artwork must be distinguished from disabled-control artwork in the file names and integration.

## 4. Installer / Windows application ICO

**Prompt — attach the approved product-symbol master from prompt 1:**

> Adapt the supplied, already approved Revit MCP product symbol into a Windows application icon suitable for an installer executable, Installed Apps and an uninstaller. Preserve the symbol exactly: do not redesign it, add a download arrow, add the letters EXE, use a cardboard package, or combine it with a second symbol. Use warm-white #FAF9F7 artwork on one solid charcoal #2B2B2B square backing with a restrained corner radius of no more than 3 units on a 32 unit grid. Maintain enough padding that the silhouette stays distinct at 16 pixels. The backing must be an intentional part of the icon, with transparent pixels outside its corners. Keep a flat two-dimensional appearance, with no bevels, lighting, shadows, glow or gradients. Deliver a transparent 1024 × 1024 pixel master and separately optimized 16, 24, 32, 48, 64, 128 and 256 pixel PNGs. The smaller files must be optically tuned and crisp, rather than blurred reductions of the master. Package these exact sizes into one valid multi-resolution Windows ICO file if the production tool supports ICO; otherwise deliver the PNGs for lossless ICO assembly. No text, promotional composition, mockup, border around the entire canvas or contact sheet.

**Integration:** assemble the PNGs into `installer/setup.ico`, restore the `<ApplicationIcon>setup.ico</ApplicationIcon>` property in the installer project, rebuild and inspect the executable at small and large Windows icon sizes. ICO conversion is a deterministic export step, not a separate creative design.

## 5. Publisher and product lockup

**Prompt — attach the existing exact OOXGN wordmark SVG:**

> Compose a restrained horizontal publisher-and-product lockup using the supplied OOXGN vector wordmark and the exact product name “Revit MCP”. Reuse the wordmark paths without redrawing, retyping, restyling, recoloring beyond the approved monochrome variants, distorting or changing its letter spacing. Place the OOXGN mark on the left and the product name to its right, separated by clear negative space; use a thin vertical divider only if it improves the reading order. The product name should be legible and neutral, with no new monogram or custom lettering. Use Neue Haas Grotesk or Helvetica Neue only if licensed and available; otherwise use Segoe UI. Make the OOXGN mark and product name feel optically balanced without giving the plugin a louder visual identity than the publisher. Work on a 1400 × 260 pixel transparent canvas with at least 60 pixels of clear space around the live artwork. Produce a light-surface version in black #000000 and a dark-surface version in warm white #FAF9F7, preserving identical layout and proportions. Keep typography as editable text in the source and provide an outlined SVG export plus transparent PNG exports at 2× and 4×. Do not synthesize the OOXGN logo with an image model: composite the supplied SVG exactly. Do not include a version number, slogan, external application logo, decorative X, shadows, gradients or a background panel.

**Use:** documentation and product-identification surfaces with enough room for text. The existing README wordmark does not require replacement to use the plugin.

## 6. GitHub social preview

**Prompt — attach the exact OOXGN wordmark and approved product symbol:**

> Create a precise, understated GitHub repository social preview for “Revit MCP” by OOXGN. Canvas: exactly 1280 × 640 pixels. Use a flat charcoal #2B2B2B background with warm-white #FAF9F7 typography. Keep all meaningful content at least 80 pixels from the edges so that cropping does not damage the identity. Place the exact supplied OOXGN wordmark in the upper-left region at a restrained scale. Place the exact title “Revit MCP” beneath it with a strong but compact modern sans-serif hierarchy. Use one secondary line only: “Local MCP bridge for Autodesk Revit 2026”. A small line at the bottom may read “Windows x64 · Open source”. Put the approved product symbol in the right half, aligned with the title and large enough to be recognizable, but do not add a decorative illustration, fake 3D building, dashboard, code listing or stock AI imagery. Use clear alignment and negative space to communicate an engineering tool. Maintain the OOXGN mark exactly as supplied; composite its vector paths rather than asking the image model to invent the letters. Keep all text in editable layers and verify the spelling independently. Do not add the Autodesk or Revit logo, a release version, marketing claims, gradients, shadows, glow, badges, fake statistics or extra slogans. Deliver a clean 1280 × 640 PNG and the editable layered source. The design must stay readable when the whole image is reduced to approximately 320 × 160 pixels.

**Integration:** upload as the repository social preview. It is a repository setting and is not embedded into the installer.

## 7. Optional release cover

**Prompt — attach the exact OOXGN wordmark and approved product symbol:**

> Produce a minimal release cover for version 0.0.1 of Revit MCP, published by OOXGN. Canvas: 1600 × 900 pixels, landscape. Use the OOXGN palette only: charcoal #2B2B2B background, warm white #FAF9F7 main text, and #9CA3AF supporting text. Place the exact supplied OOXGN wordmark in a small publisher position. Use the exact title “Revit MCP” and the exact version “0.0.1” as two deliberate typographic levels. Include only one platform line: “Autodesk Revit 2026 · Windows x64”. Position the approved product symbol as a supporting element with ample clear space. Use a calm, compact technical composition with a 96 pixel safe margin. Reuse the supplied mark exactly and preserve editable typography. Do not create a fake screenshot of Revit, invent features or show unverified engineering results. Do not include an installer button, testimonial, promotional claim, roadmap, decorative X, gradient, lighting effect, 3D mockup, watermark or any other brand logo. Deliver one final 1600 × 900 PNG and its editable layered master, with all text checked for exact spelling and the version set to 0.0.1.

This cover is optional. Actual application screenshots should come from the running application, with suitable sample data, rather than generated illustrations.

## Final asset acceptance

Check alpha channels on dark and light backgrounds, exact canvas dimensions, legibility at 16/32 px, equal visual weight between ribbon commands, absence of inherited marks, accurate OOXGN outlines and exact product spelling. Keep vector masters alongside PNG exports. Review the actual ribbon at 100% and 150% display scale before integrating replacements.
