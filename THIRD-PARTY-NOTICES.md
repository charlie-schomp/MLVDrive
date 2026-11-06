# Third-party notices

MLV Drive is free software: you can redistribute it or modify it under the terms of the GNU General
Public License as published by the Free Software Foundation, either version 2 of the License or
(at your option) any later version. See `LICENSE`. It comes with no warranty.

## Code and data MLV Drive is based on

| Work | Used for | Copyright | Licence |
|---|---|---|---|
| [MLVFS](https://github.com/davidmilligan/MLVFS) | DNG tag layout, older Canon colour matrices, white-balance maths | (C) 2014 David Milligan | GPL v2 or later |
| UFRaw, through MLVFS | Kelvin to channel multipliers | UFRaw's authors | GPL v2 or later, as carried by MLVFS |
| Andy600's matrices, through MLVFS | Colour matrices of older Canon bodies, from Adobe DNG Converter | As credited in MLVFS | As carried by MLVFS |
| [LibRaw](https://github.com/LibRaw/LibRaw) | Colour matrices of newer Canon bodies | LibRaw LLC | LGPL 2.1 or CDDL 1.0 |
| Adobe's "Canon EOS 6D Mark II Adobe Standard" profile | Measured colour values for that camera | Adobe | Values only; no Adobe file is included |

The idea of patching MLVFS for Resolve comes from
[MLV Resolve FS](https://github.com/SpicyBoi82/MLVS-is-Davinci-Resolve-magic-lantern-). MLV Drive is a separate
implementation.

## Included in the downloads

### WinFsp (Windows downloads)

**WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos**
<https://github.com/winfsp/winfsp>

`winfsp-msil.dll` is WinFsp's unmodified .NET layer, from its 2.1.25156 release, under GPLv3 with WinFsp's exception
for free and open-source software. Licence: `licenses/winfsp-License.txt`. Source for that release:
<https://github.com/winfsp/winfsp/tree/v2.1>. The installer can also fetch WinFsp's own unmodified installer.

### .NET runtime (all downloads)

Each program includes parts of the .NET 8 runtime, (C) .NET Foundation and Contributors, under the MIT licence.
Licence: `licenses/dotnet-LICENSE.txt`. Its own third-party notices: `licenses/dotnet-ThirdPartyNotices.txt`.

## Needed but not included

You install these yourself, under their own licences: FUSE-T or macFUSE on macOS, and libfuse on Linux.
