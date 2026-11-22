import AppKit

@MainActor
final class FileShelf: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSWindowDelegate {
    private var urls: [URL] = []
    private let table = NSTableView()
    private let panel = NSPanel(contentRect: NSRect(x:0,y:0,width:330,height:190),styleMask:[.titled,.closable,.utilityWindow],backing:.buffered,defer:false)
    override init() {
        super.init()
        panel.title = "Float"; panel.level = .floating; panel.delegate = self
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("file"))
        table.addTableColumn(column); table.headerView = nil; table.dataSource = self; table.delegate = self
        table.allowsMultipleSelection = true; table.setDraggingSourceOperationMask(.copy,forLocal:false)
        let scroll = NSScrollView(frame:NSRect(x:0,y:46,width:330,height:144))
        scroll.documentView = table; scroll.hasVerticalScroller = true
        panel.contentView?.addSubview(scroll)
        let buttons = NSStackView(views:[NSButton(title:"Remove",target:self,action:#selector(removeRows)),
                                         NSButton(title:"Clear",target:self,action:#selector(clear))])
        buttons.frame = NSRect(x:8,y:5,width:175,height:34); panel.contentView?.addSubview(buttons)
    }
    func add(_ files: [URL]) throws {
        let added = files.filter { !urls.contains($0) }
        guard urls.count + added.count <= 64 else { throw FileProblem("Float holds up to 64 items.") }
        urls += added; table.reloadData(); panel.makeKeyAndOrderFront(nil)
    }
    @objc private func removeRows() { for row in table.selectedRowIndexes.reversed() { urls.remove(at:row) }; table.reloadData() }
    @objc private func clear() { urls = []; table.reloadData() }
    func windowWillClose(_ notification: Notification) { clear() }
    func numberOfRows(in tableView: NSTableView) -> Int { urls.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        NSTextField(labelWithString: urls[row].lastPathComponent)
    }
    func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? { urls[row] as NSURL }
}
