# Third-party notices

quickshell is released under the MIT licence ([LICENSE](LICENSE)). The release archive carries the
components below beside it, each under its own licence, every one of them OSI-approved and
compatible with MIT. Their notices are reproduced here as those licences ask.

The archive is self-contained: it carries the .NET runtime and the Windows Desktop runtime (WPF)
it runs on, so the client needs nothing installed first. Both are MIT, from the .NET Foundation,
and the third-party components inside them are listed in their own notices,
[dotnet/runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) and
[dotnet/wpf](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT).

The Packages column names each NuGet package or runtime pack exactly as the build resolves it, and
`release.cmd` refuses an archive that ships one this table does not list at that version under an
OSI licence (QS378).

| Library | Packages | Version | Licence | Copyright |
|---|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | Microsoft.NETCore.App.Runtime.win-x64 | 10.0.14 | MIT | © .NET Foundation and Contributors |
| [Windows Desktop runtime](https://github.com/dotnet/wpf) (WPF) | Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.14 | MIT | © .NET Foundation and Contributors |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH.NET | 2026.0.0 | MIT | Copyright © Renci 2010-2026 |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | BouncyCastle.Cryptography | 2.7.0 | MIT | Copyright © Legion of the Bouncy Castle Inc. 2000-2026 |
| [NSec.Cryptography](https://github.com/ektrah/nsec) | NSec.Cryptography | 26.4.0 | MIT | © 2026 Klaus Hartke |
| [libsodium](https://github.com/jedisct1/libsodium) | libsodium | 1.0.22 | ISC | © 2026 Frank Denis |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | Vortice.Direct3D11, Vortice.DXGI, Vortice.Direct2D1, Vortice.D3DCompiler, Vortice.DirectX | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics) | Vortice.Mathematics | 2.1.0 | MIT | Copyright (c) Amer Koleci and Contributors |
| [SharpGen.Runtime](https://github.com/SharpGenTools/SharpGenTools) | SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | © Microsoft Corporation |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | © Microsoft Corporation |
| [PDFsharp and MigraDoc](https://github.com/empira/PDFsharp) | PDFsharp, PDFsharp-MigraDoc | 6.2.4 | MIT | © 2026 empira Software GmbH |

System.Security.Cryptography.ProtectedData is referenced by the transport, and the copy that ships
is the Windows Desktop runtime's own, under that row.

The client also carries one list that is not a library: the [EFF large wordlist for
passphrases](https://www.eff.org/dice) (2016), 7,776 words, built into `quickshell.exe` and used to
generate a vault passphrase. © Electronic Frontier Foundation, licensed under [Creative Commons
Attribution 3.0 United States](https://creativecommons.org/licenses/by/3.0/us/); it is carried with
the dice numbers removed and the words unchanged.

## The MIT licence

Every library above marked MIT is licensed under these terms, with its own copyright line in place
of the placeholder:

> Copyright (c) &lt;the copyright holders above&gt;
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
> NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
> OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## The ISC licence (libsodium)

> Copyright (c) 2013-2026 Frank Denis &lt;j at pureftpd dot org&gt;
>
> Permission to use, copy, modify, and/or distribute this software for any purpose with or without
> fee is hereby granted, provided that the above copyright notice and this permission notice appear
> in all copies.
>
> THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS
> SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE
> AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
> WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
> OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS
> SOFTWARE.
