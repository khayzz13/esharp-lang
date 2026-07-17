# Third-party notices

E# is licensed under the MIT License — see [LICENSE](LICENSE).

This file records third-party source code incorporated into E#, and the licenses it
carries. It covers **source only**: code in this repository that is derived from
another project.

---

## .NET Compiler Platform ("Roslyn") — github.com/dotnet/roslyn

License: **MIT** · Copyright (c) .NET Foundation and Contributors

Roslyn's MIT license permits this reuse; it requires that the copyright notice and
permission notice below travel with the ported portions. That is what this file
records.

### Ported code

These files contain code ported from Roslyn. They are transliterations of Roslyn
logic onto E#'s own types (Mono.Cecil rather than Roslyn's internal writer), not
verbatim copies, but the substance — the tables, the invariants, the semantics — is
Roslyn's:

| File | Derived from |
|---|---|
| `src/Esharp.Compiler/Emit/ILOpCodeFacts.cs` | `src/Compilers/Core/Portable/CodeGen/ILOpCodeExtensions.cs` — the stack-behavior, control-transfer, and short-form-folding fact tables |
| `src/Esharp.Compiler/Emit/ILBuilder.cs` | `Microsoft.CodeAnalysis.CodeGen.ILBuilder` — the verb surface and its emit invariants (no raw control transfer; stack-depth tracking; balance asserted at labels) |

### Design influence (no code copied)

These follow designs Roslyn established, but were written independently. They are
listed for honesty about provenance, not because the license requires it:

- `src/Esharp.Compiler/Syntax/GreenNode.cs` — the green/red syntax-tree split, and a
  slot model in the shape of Roslyn's.
- `src/Esharp.Compiler/Emit/ILLabel.cs` — the branch/label balance invariants.
- `src/Esharp.Compiler/BoundTree/BoundNodes.Update.cs` — mirrors the shape of
  Roslyn's `BoundNodes.xml`-generated `Update(...)` methods.
- `src/Esharp.Compiler/Syntax/Parsing/Parser.cs` — a stack-guard in the manner of
  Roslyn's `StackGuard`.
- `src/Esharp.Compiler/Syntax/Lexing/TriviaScanner.cs` — the `///` doc-comment rule.
- `src/Esharp.Compiler/Lowering/AsyncLowering.cs` — the async state-machine's
  `SetResult`-after-`try` ordering.
- `src/Esharp.LanguageServer/` — the language server's serial-dispatch architecture.

Separately, E# *uses* Roslyn as a library (`Microsoft.CodeAnalysis.*`) for its C#
interop surface. Calling a library's public API is not derivation, and the files that
do so (`Compilation/RoslynSymbolAdapter.cs`, `Compilation/Compilation.cs`, and
others) are E#'s own code.

### License

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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
