module Auth
import "AuthDemo"

data LoginRequest {
    email: string
    password: string
    ip: string
}

data Session {
    token: string
    userId: Guid
}

choice AuthError {
    invalidCredentials
    accountDisabled
}

func login(
    req: LoginRequest,
    users: IUserStore,
    hasher: IPasswordHasher,
    tokens: ISessionTokens
) -> Result<Session, AuthError> {
    let user = users.findByEmail(req.email) else {
        return error(AuthError.invalidCredentials())
    }

    if user.disabled {
        return error(AuthError.accountDisabled())
    }

    if !hasher.verify(req.password, user.passwordHash) {
        return error(AuthError.invalidCredentials())
    }

    return ok(Session {
        token: tokens.issue(user.id)
        userId: user.id
    })
}

func describeError(err: AuthError) -> string {
    match (err: AuthError) {
        .invalidCredentials { return "invalid credentials" }
        .accountDisabled { return "account disabled" }
        default { return "unknown error" }
    }
}
