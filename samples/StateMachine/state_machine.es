module StateMachine

choice ConnectionState {
    disconnected
    connecting
    connected
    failed(reason: string)
}

data Client {
    state: ConnectionState
    name: string
}

func startConnect(client: *Client) {
    match client.state {
        .disconnected {
            client.state = ConnectionState.connecting()
        }
        default { }
    }
}

func markConnected(client: *Client) {
    client.state = ConnectionState.connected()
}

func markFailed(client: *Client, reason: string) {
    client.state = ConnectionState.failed(reason)
}

func describe(client: Client) -> string {
    match client.state {
        .disconnected { return "disconnected" }
        .connecting { return "connecting" }
        .connected { return "connected" }
        .failed(reason) { return reason }
        default { return "unknown" }
    }
}

func greet(client: Client) -> string {
    let status = describe(client)
    return "client {client.name} is {status}"
}
