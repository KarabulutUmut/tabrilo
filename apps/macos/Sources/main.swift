import AppKit
import Foundation

struct CommandCatalog: Decodable {
    struct Command: Decodable {
        let id: String
        let label: String
        let scope: String
        let shortcut: String?
        let kinds: [String]
    }

    let version: Int
    let commands: [Command]

    static func bundled() throws -> CommandCatalog {
        guard let url = Bundle.main.url(forResource: "commands", withExtension: "json") else {
            throw CocoaError(.fileNoSuchFile)
        }
        let catalog = try JSONDecoder().decode(CommandCatalog.self, from: Data(contentsOf: url))
        guard catalog.version == 1, Set(catalog.commands.map(\.id)).count == catalog.commands.count else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return catalog
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 680, height: 440),
            styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Tabrilo"
        window.center()

        let heading = NSTextField(labelWithString: "Tabrilo")
        heading.font = .systemFont(ofSize: 24, weight: .semibold)
        let message: String
        do {
            _ = try CommandCatalog.bundled()
            message = "File processing will be available in the Mac implementation."
        } catch {
            message = "The shared command catalog could not be loaded."
        }
        let detail = NSTextField(wrappingLabelWithString: message)
        detail.textColor = .secondaryLabelColor
        let stack = NSStackView(views: [heading, detail])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.translatesAutoresizingMaskIntoConstraints = false
        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: content.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 24)
        ])
        window.contentView = content
        self.window = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
