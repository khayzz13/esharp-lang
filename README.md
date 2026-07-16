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

## Versioning

E# follows semantic versioning — `major.minor.patch` — with each field read deliberately.

A **patch** marks a public sync, cut when enough work has accumulated to be worth
distributing rather than on a fixed schedule; releases are irregular. **Minor** increments
are rare. The first especially is a genuine step in the project's life rather than a routine
tick — less a statement about the compiler than a sign that E# has grown into more than one
person's work in progress. The ones after it stay infrequent by intent.

**`v1.0.0` is treated as a commitment, not a milestone.** It is withheld until the language
is production-capable across its entire surface and a stance on breaking changes is settled:
either a no-breaking-changes guarantee or a documented account of where breaks may still
occur. Realistically that is a .NET 14+ horizon, if it is reached at all — stated
plainly here rather than pinned to a date that could not be stood behind.

Contributions bring that stage closer: writing real programs in E#, sending friction and
ideas back to the compiler team, or taking part in development directly.

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
