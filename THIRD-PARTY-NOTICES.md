# Third-Party Notices

The Sendspin .NET SDK is distributed under the MIT License (see `LICENSE`). It includes the
third-party components listed below, each under its own license. The notices here are
reproduced in full as those licenses require; the license text at the top of each vendored
source file is authoritative and must not be removed or rewritten.

Components taken as NuGet package references (Concentus, Noise.NET, and their transitive
dependencies) carry their own notices in their packages and are not restated here. This file
covers source vendored **into** this repository.

---

## WdlResampler

- **Where:** `src/Sendspin.SDK/Audio/Resampling/ThirdParty/WdlResampler.cs`
- **Upstream:** [NAudio](https://github.com/naudio/NAudio) — `NAudio.Core/Dsp/WdlResampler.cs`
- **Vendored:** 2026-08-25
- **License:** MIT (NAudio), over a zlib-style license (Cockos WDL)
- **Modifications:** namespace changed to `Sendspin.SDK.Audio.Resampling.ThirdParty`; the type
  made `internal`; `#nullable disable` and analyzer suppressions added. The resampling algorithm
  is unmodified. The file's header records the same list.

Vendored rather than referenced: the SDK is a cross-platform library and NAudio is a Windows
audio stack. Taking the package for one platform-neutral DSP file would put a Windows-oriented
dependency on every consumer, including the Linux and macOS ones.

NAudio is MIT-licensed:

```
Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

The class is a C# port of the resampler from Cockos WDL, used in NAudio with permission from
Justin Frankel. The original WDL license (zlib-style) applies to the ported algorithm:

```
Copyright (C) 2005 and later Cockos Incorporated

Portions copyright other contributors, see each source file for more information

This software is provided 'as-is', without any express or implied warranty.  In no event will
the authors be held liable for any damages arising from the use of this software.

Permission is granted to anyone to use this software for any purpose, including commercial
applications, and to alter it and redistribute it freely, subject to the following
restrictions:

  1. The origin of this software must not be misrepresented; you must not claim that you wrote
     the original software. If you use this software in a product, an acknowledgment in the
     product documentation would be appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be misrepresented as
     being the original software.
  3. This notice may not be removed or altered from any source distribution.
```

---

## SimpleFlac

- **Where:** `src/Sendspin.SDK/Audio/Codecs/ThirdParty/SimpleFlac.cs`
- **Upstream:** [jdpurcell/SimpleFlac](https://github.com/jdpurcell/SimpleFlac), itself derived
  from [Project Nayuki's Simple FLAC implementation](https://www.nayuki.io/page/simple-flac-implementation)
- **Vendored:** 2024-12-22
- **License:** MIT

```
Copyright (c) J.D. Purcell (C# port and enhancements)
Copyright (c) Project Nayuki (Simple FLAC decoder in Java)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
