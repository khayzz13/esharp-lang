module Ops

func greet(u: User) -> string {
    return "hello {u.name}, age {u.age}"
}

func isAdult(u: User) -> bool {
    return u.age >= 18
}

func makePair<A, B>(a: A, b: B) -> Pair<A, B> {
    return Pair<A, B> { first: a, second: b }
}

func wrapSome<T>(value: T) -> Option<T> {
    return .some(value)
}
