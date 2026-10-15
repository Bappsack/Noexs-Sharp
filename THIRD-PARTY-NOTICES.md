# Third-party notices

## Noexes (original project)

Noexs Rewritten is based on Noexes by mdbell, "a graphical remote debugger for the Nintendo
Switch": https://github.com/mdbell/Noexes

The PC client (`App/`) is a C# port of its JavaFX client, and the sysmodule (`sysmodule/`) is a
modified version of its server. The wire protocol, the `.dmp` dump format and the watch list
format come from it.

Noexes is licensed under the GNU General Public License v3.0
(https://github.com/mdbell/Noexes/blob/master/LICENSE).

Its acknowledgments: L0nk, dcx2 and others from the old WiiRd forums, and roblabla.

## PointerSearcher

The pointer search (`App/Noexs/Pointers/PointerSearcher.cs`, Pointer Search tab) is ported from
PointerSearcher by Takumi4685 — https://github.com/Takumi4685/PointerSearcher

```
MIT License

Copyright (c) 2019 Takumi4685

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## NoExesDumpParse

The Dump Parser tab re-implements the features of NoExesDumpParse by rydoginator —
https://github.com/rydoginator/NoExesDumpParse. That repository has no license file, so no
code was copied; the tab is an independent implementation of the same functionality.
