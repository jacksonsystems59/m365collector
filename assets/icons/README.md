# M365Collector icon pack

Original application and navigation artwork for M365Collector v0.0.3, created from geometric vector shapes. No external font or stock icon dependency.

- `application.ico`: embedded Windows icon at 16, 24, 32, 48, 64, 128 and 256 pixels.
- `application.svg` and `application-*.png`: navy/white/teal application mark.
- Named SVGs: editable teal navigation/action icons on transparent backgrounds.
- Named PNGs: 24-pixel white variants for dark application buttons.

Regenerate with PowerShell 7 on Windows: `./scripts/Build-Icons.ps1`. The vectors and raster application mark use the same M geometry; the SVG uses a rounded canvas, while Windows ICO/PNG uses a square canvas for crisp small-size rendering. No third-party artwork or fonts are redistributed.
