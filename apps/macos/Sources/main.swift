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
    private var wheel: DragWheel?
    private let shelf = FileShelf()
    private let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private var notice: NSPanel?
    private var worker: Task<[URL], Error>?
    private var last: [URL] = []
    private var sharePicker: NSSharingServicePicker?

    func applicationDidFinishLaunching(_ notification: Notification) {
        do {
            let catalog = try CommandCatalog.bundled()
            guard let url = Bundle.main.url(forResource:"wheel",withExtension:"json") else { throw CocoaError(.fileNoSuchFile) }
            let spec = try JSONDecoder().decode(WheelSpec.self,from:Data(contentsOf:url))
            guard spec.version == 1, spec.actions.count == 6 else { throw CocoaError(.fileReadCorruptFile) }
            wheel = DragWheel(spec:spec,catalog:catalog)
            wheel?.dispatch = { [weak self] id, urls in self?.perform(id,urls) }
            status.button?.title = "Tabrilo"
            let menu = NSMenu()
            for (name,action,key) in [("Choose files…",#selector(choose),"o"),("Wheel keys…",#selector(bindings),""),
                                       ("Quit Tabrilo",#selector(quit),"q")] {
                let item = menu.addItem(withTitle:name,action:action,keyEquivalent:key); item.target = self
            }
            status.menu = menu
            if !UserDefaults.standard.bool(forKey:"guideSeen") {
                message("Drag a file, then tap Shift, Control or Option. Quit from the Tabrilo menu.")
                UserDefaults.standard.set(true,forKey:"guideSeen")
            }
        } catch { message(error.localizedDescription) }
    }

    @objc private func choose() {
        let picker = NSOpenPanel(); picker.canChooseDirectories = true; picker.allowsMultipleSelection = true
        if picker.runModal() == .OK { wheel?.openKeyboard(picker.urls) }
    }
    @objc private func bindings() {
        guard let wheel else { return }
        let dialog = NSAlert(); dialog.messageText = "Wheel keys"
        let stack = NSStackView(); stack.orientation = .vertical
        let choices = ["Convert","Tools","Actions"].enumerated().map { index, name in
            let button = NSPopUpButton(); button.addItems(withTitles:wheel.spec.bindings); button.selectItem(withTitle:wheel.bindings[index])
            stack.addArrangedSubview(NSStackView(views:[NSTextField(labelWithString:name),button])); return button
        }
        stack.frame = NSRect(x:0,y:0,width:220,height:100); dialog.accessoryView = stack
        dialog.addButton(withTitle:"Save"); dialog.addButton(withTitle:"Reset defaults"); dialog.addButton(withTitle:"Cancel")
        let response = dialog.runModal()
        guard response == .alertFirstButtonReturn || response == .alertSecondButtonReturn else { return }
        let keys = response == .alertSecondButtonReturn ? wheel.spec.bindings : choices.compactMap { $0.titleOfSelectedItem }
        guard Set(keys).count == 3 else { message("Choose a different key for each wheel."); return }
        wheel.bindings = keys; UserDefaults.standard.set(keys,forKey:"wheelBindings")
    }
    private func perform(_ id: String, _ urls: [URL]) {
        guard worker == nil, !urls.isEmpty else { return }
        do {
            switch id {
            case "pdf.metadata":
                let source = urls[0]
                Task {
                    do {
                        let details = try await Task.detached { try MacExports.pdfInformation(source) }.value
                        let alert = NSAlert()
                        alert.messageText = source.lastPathComponent
                        alert.informativeText = details
                        alert.runModal()
                    } catch { message(error.localizedDescription) }
                }
            case "action.copy", "action.path":
                let pasteboard = NSPasteboard.general; pasteboard.clearContents()
                if id == "action.copy" { guard pasteboard.writeObjects(urls as [NSURL]) else { throw FileProblem("Clipboard unavailable.") } }
                else { pasteboard.setString(urls.map(\.path).joined(separator:"\n"),forType:.string) }
                message(id == "action.copy" ? "File references copied" : "Paths copied")
            case "action.float": try shelf.add(urls)
            case "action.share":
                guard !NSSharingService.sharingServices(forItems:urls).isEmpty, let button = status.button else { throw FileProblem("No sharing service accepts this selection.") }
                sharePicker = NSSharingServicePicker(items:urls); sharePicker?.show(relativeTo:button.bounds,of:button,preferredEdge:.minY)
            case "action.terminal":
                let dirs = Array(Set(urls.map { $0.hasDirectoryPath ? $0 : $0.deletingLastPathComponent() })).sorted { $0.path < $1.path }
                var dir = dirs[0]
                if dirs.count > 1 {
                    let dialog = NSAlert(); dialog.messageText = "Open Terminal in"
                    let pick = NSPopUpButton(); pick.addItems(withTitles:dirs.map(\.path)); dialog.accessoryView = pick
                    dialog.addButton(withTitle:"Open"); dialog.addButton(withTitle:"Cancel")
                    guard dialog.runModal() == .alertFirstButtonReturn else { return }; dir = dirs[pick.indexOfSelectedItem]
                }
                guard let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier:"com.apple.Terminal") else { throw FileProblem("Terminal is unavailable.") }
                NSWorkspace.shared.open([dir],withApplicationAt:terminal,configuration:NSWorkspace.OpenConfiguration()) { _,error in
                    if let error { DispatchQueue.main.async { self.message(error.localizedDescription) } }
                }
            case "action.move":
                guard let dir = folder() else { return }
                var moved = 0
                do {
                    for source in urls {
                        try FileManager.default.moveItem(at:source,to:dir.appendingPathComponent(source.lastPathComponent)); moved += 1
                    }
                } catch { throw FileProblem("\(moved)/\(urls.count) moved. " + error.localizedDescription) }
                message("\(moved) moved")
            default:
                let mixed = Set(urls.map { $0.deletingLastPathComponent() }).count > 1
                var combined: URL?
                if mixed && id == "convert.pdf" || urls.contains(where: { !FileManager.default.isWritableFile(atPath: $0.deletingLastPathComponent().path) }) {
                    guard let chosen = folder() else { return }
                    combined = chosen
                }
                message("Converting…")
                let task = Task.detached { try MacExports.run(id,urls:urls,combined:combined) }
                worker = task
                Task {
                    defer { worker = nil }
                    do {
                        last = try await task.value
                        message(id == "image.compress" ? "\(last.count)/\(urls.count) smaller copies" : "\(last.count) export(s) complete")
                    }
                    catch { message(error.localizedDescription) }
                }
            }
        } catch { message(error.localizedDescription) }
    }

    private func folder() -> URL? {
        let picker = NSOpenPanel(); picker.canChooseFiles = false; picker.canChooseDirectories = true
        return picker.runModal() == .OK ? picker.url : nil
    }
    private func message(_ text: String) {
        notice?.close()
        let panel = NSPanel(contentRect:NSRect(x:0,y:0,width:350,height:105),styleMask:[.titled,.closable,.utilityWindow,.nonactivatingPanel],backing:.buffered,defer:false)
        panel.title = "Tabrilo"; panel.level = .floating
        let info = NSTextField(wrappingLabelWithString:text); info.frame = NSRect(x:12,y:42,width:326,height:53); panel.contentView?.addSubview(info)
        let button = NSButton(title:text == "Converting…" ? "Cancel" : "Reveal",target:self,action:text == "Converting…" ? #selector(cancelJob) : #selector(reveal))
        button.isHidden = text != "Converting…" && last.isEmpty
        button.frame = NSRect(x:12,y:4,width:85,height:30); panel.contentView?.addSubview(button)
        if let screen = NSScreen.main { panel.setFrameTopLeftPoint(NSPoint(x:screen.visibleFrame.maxX-360,y:screen.visibleFrame.maxY-10)) }
        notice = panel; panel.orderFrontRegardless()
    }
    @objc private func cancelJob() { worker?.cancel() }
    @objc private func reveal() { NSWorkspace.shared.activateFileViewerSelecting(last) }
    @objc private func quit() { cancelJob(); NSApp.terminate(nil) }
}

if CommandLine.arguments.count == 3 && CommandLine.arguments[1] == "--self-check" {
    do {
        try macSelfCheck(URL(fileURLWithPath: CommandLine.arguments[2]))
        print("AppKit contract, hit geometry, PDF raster and combined image export passed.")
    } catch { FileHandle.standardError.write(Data(error.localizedDescription.utf8)); exit(1) }
} else {
    MainActor.assumeIsolated {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}
