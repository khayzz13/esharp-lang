# E#

E# is a general-purpose language for .NET that compiles directly to CLR assemblies. It
combines value-oriented data, explicit control over representation, structured
concurrency, and direct access to the .NET Base Class Library.

The canonical language documentation is published at
[esharp-lang.vercel.app](https://esharp-lang.vercel.app/); the normative language
specification begins at [the specification index](https://esharp-lang.vercel.app/spec/).

## Repository scope

This repository is the curated public source distribution for E#. It contains:

- the compiler, build integration, SDK, templates, E# standard library, and language
  server under `src/`;
- compiler, language-server, and fuzz regression tests under `tests/`;
- the supported IL inspection and assembly-running utilities under `tools/`.

Design tickets, internal planning, diagnostic repro history, experimental corpora, and
non-canonical samples are maintained outside this mirror. Their absence does not imply
that this repository is the complete private development workspace.

## Build and test

E# currently targets .NET 10.

```sh
dotnet build src/Esharp.Compiler/Esharp.Compiler.csproj
dotnet test tests/Esharp.Tests
```

The direct IL compiler is the primary implementation and correctness target.

## Tools

- `tools/esDumpIL` is the E# implementation of the IL inspection utility.
- `tools/dump_il` is the C# fallback used when the E# toolchain itself is unavailable.
- `tools/run_dll` invokes a static method from a compiled assembly for focused runtime
  inspection.

## License

E# is available under the MIT License. See `LICENSE`.
