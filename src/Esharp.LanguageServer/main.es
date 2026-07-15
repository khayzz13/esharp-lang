namespace Esharp.LanguageServer

// esharp-lsp — the E# language server, written in E#, speaking LSP over stdio.
// Any LSP client (VS Code, Neovim, Helix, Rider) attaches to the process; stdout
// is the transport, stderr the log.
func main() -> int {
    try {
        let server = LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput())
        return server.run()
    } catch (Exception e) {
        Console.Error.WriteLine("esharp-lsp: fatal: {e}")
        return 1
    }
}
