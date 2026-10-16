# Preset Sender website branding

The chosen logo is **Amber Gradient**: an amber-to-orange effects pedal with a dark forwarding arrow and softly shaded circular footswitch. The user selected this version on 14 October 2026. The generated artwork is preserved; only file-format conversion and resizing have been applied. The former DIN Send socket mark now belongs to MIDI Forwarder.

- `preset-sender-logo.png`: original transparent mark, 1254 × 1254.
- `preset-sender-512.png`: general-purpose website/logo image.
- `preset-sender-256.png`: compact website logo or social/profile mark.
- `preset-sender-16.png` through `preset-sender-128.png`: small PNG variants.
- `favicon.ico`: ten embedded sizes, 16 through 256 pixels.

Use the same mark on light and dark backgrounds. Keep the name as live website text for sharp, accessible typography; avoid placing the full app name inside a tiny favicon.

Example website header and favicon references, after copying these assets into your site's `/assets/` directory:

```html
<link rel="icon" href="/assets/favicon.ico" sizes="any">
<link rel="icon" type="image/png" href="/assets/preset-sender-32.png" sizes="32x32">

<a class="brand" href="/" aria-label="Preset Sender home">
  <img src="/assets/preset-sender-256.png" width="40" height="40" alt="">
  <span>Preset Sender</span>
</a>
```

The mark is decorative alongside the readable app name; the link's accessible name describes the destination. Preserve square proportions and leave the existing transparent padding intact.

The selected source is `../NewOptions/PedalGradients/pedal-1-amber-gradient.png`. The earlier concepts remain under `../Options/` and `../NewOptions/`. To regenerate these size/format exports on Windows, run `../Export-BrandAssets.ps1`. It also writes the executable ICO and app image to `../../Assets/`. No website source or hosted site was supplied in this task, so these are ready-to-use files rather than a deployed website change.
