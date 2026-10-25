# Third-party notices

## vgmstream (clHCA decoder)

`managed/CriMw.CriWare.Runtime/Hca/HcaDecoder.cs` is a C# port of
`src/coding/libs/clhca.c` and `HcaTables.cs` is generated from `clhca_data.h`
of [vgmstream](https://github.com/vgmstream/vgmstream). clHCA credits:
nyaga (original decompilation), kode54 (C port), bnnm (HCA v3), Thealexbarney
(VGAudio reference).

```
Copyright (c) 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm,
                        Christopher Snowhill, NicknineTheEagle, bxaimc,
                        Thealexbarney, CyberBotX, EdnessP, et al

Permission to use, copy, modify, and distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Tools used at build time (not redistributed)

- [AssetRipper](https://github.com/AssetRipper/AssetRipper) (GPL-3.0) — downloaded by CI to export the game project.
- [UnityPy](https://github.com/K0lb3/UnityPy) (MIT) — reads shader data in `tools/shaderport`.
- [Mono.Cecil](https://github.com/jbevain/cecil) (MIT) — `tools/PortTool`.
- Unity editor images by [GameCI](https://game.ci).
