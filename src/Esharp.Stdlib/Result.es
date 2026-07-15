namespace Esharp.Stdlib

// ═════════════════════════════════════════════════════════════════════════════
// Result<TValue, TError> — the standard library's own error-as-value type, written
// in E#.
//
// The rest of the language treats `Result` as a compiler builtin: `ok(x)` /
// `error(e)` construct it, `?` propagates it, `match` destructures it, and
// `.IsOk` / `.IsError` / `.Value` / `.Error` read it. Those surfaces are lowered
// by the compiler against THIS type, resolved by metadata name
// (`Esharp.Stdlib.Result\`2`) off the compiled stdlib. The stdlib that *supplies*
// the builtin can't lean on the builtin to define itself, so here `Result` is an
// ordinary value `struct` — its plain declaration shadows the builtin within this
// namespace (a registered type beats the intrinsic; an importing consumer that
// also uses the builtin qualifies on overlap).
//
// It is a value type by contract. The three fields are public so the compiler's
// accessor intrinsics read them directly across the assembly boundary; the
// variant invariant (Value valid iff IsOk, Error valid iff !IsOk) is upheld by
// construction — `ok`/`error` and the combinators are the only ways a Result is built.
// ═════════════════════════════════════════════════════════════════════════════

pub struct Result<TValue, TError> {
    pub IsOk: bool
    pub Value: TValue
    pub Error: TError
}

// --- construction helpers ---------------------------------------------------
// First parameter is a bare type parameter, so these are NOT promoted — they are
// internal free functions the combinators call to assemble a fresh Result with
// the unused variant slot zeroed. The compiler's `ok(x)` / `error(e)` intrinsics
// lower to the same shape directly at the use site.

func ofOk<TValue, TError>(value: TValue) -> Result<TValue, TError> {
    return Result<TValue, TError> { IsOk: true, Value: value, Error: default(TError) }
}

func ofErr<TValue, TError>(error: TError) -> Result<TValue, TError> {
    return Result<TValue, TError> { IsOk: false, Value: default(TValue), Error: error }
}

// --- combinators (value receivers on `Result`, so each reads as a method
//     `r.Map(...)`, `r.Unwrap()`, …) ----------------------------------------

// Transform the success value; the error passes through unchanged.
pub func (r: Result<TValue, TError>) Map<TValue, TError, TNew>(f: Func<TValue, TNew>) -> Result<TNew, TError> {
    if r.IsOk {
        return ofOk<TNew, TError>(f(r.Value))
    }
    return ofErr<TNew, TError>(r.Error)
}

// Transform the error value; the success passes through unchanged.
pub func (r: Result<TValue, TError>) MapErr<TValue, TError, TNew>(f: Func<TError, TNew>) -> Result<TValue, TNew> {
    if r.IsOk {
        return ofOk<TValue, TNew>(r.Value)
    }
    return ofErr<TValue, TNew>(f(r.Error))
}

// Chain a Result-returning operation onto the success value (monadic bind).
pub func (r: Result<TValue, TError>) Bind<TValue, TError, TNew>(f: Func<TValue, Result<TNew, TError>>) -> Result<TNew, TError> {
    if r.IsOk {
        return f(r.Value)
    }
    return ofErr<TNew, TError>(r.Error)
}

// Fold both variants into one output type.
pub func (r: Result<TValue, TError>) Match<TValue, TError, TOut>(ok: Func<TValue, TOut>, err: Func<TError, TOut>) -> TOut {
    if r.IsOk {
        return ok(r.Value)
    }
    return err(r.Error)
}

// Side-effect on the success value; returns the Result unchanged for chaining.
pub func (r: Result<TValue, TError>) Inspect<TValue, TError>(onOk: Action<TValue>) -> Result<TValue, TError> {
    if r.IsOk {
        onOk(r.Value)
    }
    return r
}

// Side-effect on the error value; returns the Result unchanged for chaining.
pub func (r: Result<TValue, TError>) InspectErr<TValue, TError>(onErr: Action<TError>) -> Result<TValue, TError> {
    if not r.IsOk {
        onErr(r.Error)
    }
    return r
}

// Extract the success value, or a constant fallback on error.
pub func (r: Result<TValue, TError>) UnwrapOr<TValue, TError>(fallback: TValue) -> TValue {
    if r.IsOk {
        return r.Value
    }
    return fallback
}

// Extract the success value, or compute a fallback from the error.
pub func (r: Result<TValue, TError>) UnwrapOrElse<TValue, TError>(fallback: Func<TError, TValue>) -> TValue {
    if r.IsOk {
        return r.Value
    }
    return fallback(r.Error)
}

// Extract the success value. Throws on error — the conscious hot-path exception.
pub func (r: Result<TValue, TError>) Unwrap<TValue, TError>() -> TValue {
    if not r.IsOk {
        throw InvalidOperationException("called Unwrap on an error Result")
    }
    return r.Value
}

// Extract the error value. Throws on success.
pub func (r: Result<TValue, TError>) UnwrapErr<TValue, TError>() -> TError {
    if r.IsOk {
        throw InvalidOperationException("called UnwrapErr on a success Result")
    }
    return r.Error
}

// --- static factory class ---------------------------------------------------
// A `static Result` static class mirrors the C# seed's `public static class
// Result` — the canonical factory
// surface for C# and E# consumers: `Result.Ok<int, string>(7)` /
// `Result.Error<int, string>("e")`. It is distinct from the `struct Result<…>` above
// by ARITY at the CLR level (`Result` vs `Result`2`); the arity-keyed type registry
// is what lets both live under the one name `Result`. These delegate to the same
// internal `ofOk` / `ofErr` the combinators use — never the `ok()` / `error()`
// intrinsics, which inline-construct the struct on the hot path (so the factories
// exist for the interop contract, not for the compiler's own construction).

pub static Result {
    pub func Ok<TValue, TError>(value: TValue) -> Result<TValue, TError> = ofOk<TValue, TError>(value)
    pub func Error<TValue, TError>(error: TError) -> Result<TValue, TError> = ofErr<TValue, TError>(error)
}
