module Types

data Pair<A, B> {
    first: A
    second: B
}

choice Option<T> {
    some(value: T)
    none
}

data User {
    name: string
    age: int
}

enum Role {
    admin
    editor
    viewer
}
