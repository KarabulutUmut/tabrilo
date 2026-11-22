import AppKit

struct WheelSpec: Decodable {
    let version: Int
    let diameter, innerRadius, outerRadius: Double
    let bindings, conversion, tools, actions: [String]

    func sector(_ point: NSPoint, count: Int) -> Int? {
        guard count > 0 else { return nil }
        let dx = point.x - diameter / 2, dy = point.y - diameter / 2
        guard (innerRadius...outerRadius).contains(hypot(dx, dy)) else { return nil }
        let step = 2 * Double.pi / Double(max(2, count))
        let angle = (atan2(dx, dy) + 2 * .pi).truncatingRemainder(dividingBy: 2 * .pi)
        let index = Int((angle / step).rounded()) % max(2, count)
        let gap = abs((angle + step / 2).truncatingRemainder(dividingBy: step) - step / 2)
        return index < count && gap <= step / 2 - 0.026 ? index : nil
    }
}

@MainActor
final class RingView: NSView {
    let spec: WheelSpec
    var labels: [String] = []
    var center = ""
    var highlight: Int? { didSet { needsDisplay = true } }
    var inspect: ((NSDraggingInfo, Int?) -> Bool)?
    var drop: ((NSDraggingInfo, Int?) -> Bool)?
    var leave: (() -> Void)?
    var key: ((NSEvent) -> Void)?
    init(_ spec: WheelSpec) {
        self.spec = spec
        super.init(frame: NSRect(x: 0, y: 0, width: spec.diameter, height: spec.diameter))
        registerForDraggedTypes([.fileURL])
    }
    required init?(coder: NSCoder) { nil }
    override var acceptsFirstResponder: Bool { true }
    override func keyDown(with event: NSEvent) { key?(event) }
    private func slot(_ sender: NSDraggingInfo) -> Int? {
        spec.sector(convert(sender.draggingLocation, from: nil), count: labels.count)
    }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { draggingUpdated(sender) }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        let i = slot(sender)
        guard inspect?(sender, i) == true else { highlight = nil; return [] }
        highlight = slot(sender)
        return highlight == nil ? [] : .copy
    }
    override func draggingExited(_ sender: NSDraggingInfo?) { highlight = nil; leave?() }
    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { slot(sender) != nil }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool { drop?(sender, slot(sender)) ?? false }

    override func draw(_ dirtyRect: NSRect) {
        let mid = NSPoint(x: bounds.midX, y: bounds.midY)
        NSColor(srgbRed: 0.10, green: 0.14, blue: 0.18, alpha: 1).setFill()
        NSBezierPath(ovalIn: bounds.insetBy(dx: 1, dy: 1)).fill()
        let step = 360 / CGFloat(max(2, labels.count))
        for (index, label) in labels.enumerated() {
            let angle = 90 - CGFloat(index) * step, half = step / 2 - 1.5
            let sector = NSBezierPath()
            sector.appendArc(withCenter: mid, radius: spec.outerRadius, startAngle: angle + half, endAngle: angle - half, clockwise: true)
            sector.appendArc(withCenter: mid, radius: spec.innerRadius, startAngle: angle - half, endAngle: angle + half, clockwise: false)
            sector.close()
            (index == highlight ? NSColor(srgbRed: 0.08, green: 0.37, blue: 0.34, alpha: 1)
                : NSColor(srgbRed: 0.15, green: 0.20, blue: 0.26, alpha: 1)).setFill()
            sector.fill()
            NSColor.lightGray.setStroke()
            sector.lineWidth = index == highlight ? 2.5 : 0.6
            sector.stroke()
            let r = (spec.innerRadius + spec.outerRadius) / 2
            drawText(label, at: NSPoint(x: mid.x + r * cos(angle * .pi / 180), y: mid.y + r * sin(angle * .pi / 180)))
        }
        drawText(center, at: mid)
    }
    private func drawText(_ text: String, at point: NSPoint) {
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        let attr: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12, weight: .semibold),
            .foregroundColor: NSColor.white, .paragraphStyle: style]
        (text as NSString).draw(in: NSRect(x: point.x - 43, y: point.y - 22, width: 86, height: 32), withAttributes: attr)
    }
}

private final class WheelPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

@MainActor
final class DragWheel {
    let spec: WheelSpec
    let ring: RingView
    let panel: NSPanel
    let catalog: CommandCatalog
    var dispatch: ((String, [URL]) -> Void)?
    var bindings: [String]
    private var monitors: [Any] = []
    private var timer: Timer?
    private var origin: NSPoint?
    private var files: [URL] = []
    private var commands: [String] = []
    private var sequence: Int?
    private var mode = 0
    private var previous: NSEvent.ModifierFlags = []
    private var cancelled = false
    private var released = false
    private var releaseSlot: Int?
    private var epoch = 0
    private var keyboard = false
    private var pasteboardGeneration = -1

    init(spec: WheelSpec, catalog: CommandCatalog) {
        self.spec = spec; self.catalog = catalog
        let saved = UserDefaults.standard.stringArray(forKey: "wheelBindings") ?? []
        bindings = saved.count == 3 && Set(saved) == Set(spec.bindings) ? saved : spec.bindings
        ring = RingView(spec)
        panel = WheelPanel(contentRect: ring.bounds, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.level = .floating
        panel.hidesOnDeactivate = false; panel.contentView = ring
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        ring.inspect = { [weak self] sender, _ in self?.acquire(sender) ?? false }
        ring.drop = { [weak self] sender, slot in
            guard let self, !self.cancelled, self.acquire(sender), let slot,
                !self.released || self.releaseSlot == slot, self.commands.indices.contains(slot) else { self?.cancel(); return false }
            let id = self.commands[slot], urls = self.files
            self.cancel()
            DispatchQueue.main.async { self.dispatch?(id, urls) }
            return true
        }
        ring.leave = { [weak self] in if self?.released == true { self?.cancel() } }
        ring.key = { [weak self] event in self?.key(event) }
        let mask: NSEvent.EventTypeMask = [.leftMouseDown, .leftMouseUp, .flagsChanged, .keyDown]
        if let m = NSEvent.addGlobalMonitorForEvents(matching: mask, handler: { [weak self] event in self?.event(event) }) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: mask, handler: { [weak self] event in self?.event(event); return event }) { monitors.append(m) }
        timer = Timer(timeInterval: 0.025, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        RunLoop.main.add(timer!, forMode: .common)
    }

    private func event(_ event: NSEvent) {
        switch event.type {
        case .leftMouseDown:
            if keyboard { return }
            cancel()
            previous = NSEvent.modifierFlags
            origin = NSEvent.mouseLocation
            pasteboardGeneration = NSPasteboard(name: .drag).changeCount
            cancelled = false; released = false
        case .leftMouseUp:
            if let point = event.cgEvent?.location, let primary = NSScreen.screens.first {
                release(NSPoint(x: point.x, y: primary.frame.maxY - point.y))
            } else { cancel() }
        case .flagsChanged: flags(event.modifierFlags)
        case .keyDown: if event.keyCode == 53 { cancel() }
        default: break
        }
    }
    private func poll() {
        guard !keyboard, origin != nil, !cancelled else { return }
        if NSEvent.pressedMouseButtons & 1 == 0 { release(NSEvent.mouseLocation) }
        else { flags(NSEvent.modifierFlags) }
    }
    private func flags(_ flags: NSEvent.ModifierFlags) {
        let fresh = flags.subtracting(previous)
        previous = flags
        guard !cancelled, !released else { return }
        for (i, flag) in [NSEvent.ModifierFlags.shift, .control, .option].enumerated() where fresh.contains(flag) {
            guard let next = bindings.firstIndex(of: spec.bindings[i]) else { return }
            if keyboard { mode = next; rebuild(); return }
            guard let start = origin, NSEvent.pressedMouseButtons & 1 != 0 else { return }
            let point = NSEvent.mouseLocation
            guard panel.isVisible || hypot(start.x-point.x, start.y-point.y) >= 4 else { return }
            let drag = NSPasteboard(name: .drag)
            guard drag.changeCount != pasteboardGeneration, drag.types?.contains(.fileURL) == true else { return }
            mode = next
            if !panel.isVisible {
                panel.alphaValue = 0.01
                show(at: point)
                let generation = epoch
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.55) { [weak self] in
                    if self?.epoch == generation && self?.sequence == nil { self?.cancel() }
                }
            }
            rebuild()
            return
        }
    }
    private func acquire(_ sender: NSDraggingInfo) -> Bool {
        guard panel.isVisible, !keyboard, !cancelled, sender.draggingSourceOperationMask.contains(.copy),
            sequence == nil || sequence == sender.draggingSequenceNumber,
            let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty, urls.count <= 200,
            files.isEmpty || files == urls else { return false }
        if sequence == nil {
            files = urls; sequence = sender.draggingSequenceNumber; rebuild(); panel.alphaValue = 1
        }
        return true
    }
    private func release(_ point: NSPoint) {
        guard !keyboard, !cancelled, !released else { return }
        released = true
        releaseSlot = spec.sector(panel.convertPoint(fromScreen: point), count: commands.count)
        guard panel.isVisible, sequence != nil, releaseSlot != nil else { cancel(); return }
        let generation = epoch
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { [weak self] in
            if self?.epoch == generation { self?.cancel() }
        }
    }
    func cancel() {
        epoch += 1; cancelled = true; keyboard = false; origin = nil; sequence = nil
        files = []; commands = []; ring.highlight = nil; panel.orderOut(nil)
    }
    func openKeyboard(_ urls: [URL]) {
        cancel(); keyboard = true; cancelled = false; released = false; mode = 0; files = urls
        panel.alphaValue = 1; previous = NSEvent.modifierFlags; rebuild(); show(at: NSEvent.mouseLocation)
        panel.makeKey(); panel.makeFirstResponder(ring)
    }
    private func key(_ event: NSEvent) {
        guard keyboard else { return }
        if event.keyCode == 53 { cancel(); return }
        guard !commands.isEmpty else { return }
        switch event.keyCode {
        case 123, 126: ring.highlight = ((ring.highlight ?? 0) + commands.count - 1) % commands.count
        case 124, 125, 48: ring.highlight = ((ring.highlight ?? -1) + 1) % commands.count
        case 36, 76:
            guard let index = ring.highlight else { return }
            let id = commands[index], urls = files
            cancel(); dispatch?(id, urls)
        default: break
        }
    }
    private func show(at point: NSPoint) {
        guard let area = (NSScreen.screens.first { NSMouseInRect(point, $0.frame, false) } ?? NSScreen.main)?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: max(area.minX, min(area.maxX-spec.diameter, point.x-spec.diameter/2)),
            y: max(area.minY, min(area.maxY-spec.diameter, point.y-spec.diameter/2))))
        panel.orderFrontRegardless()
    }
    private func rebuild() {
        commands = mode == 2 ? spec.actions : mode == 0 ? MacExports.targets(files, spec: spec) : []
        ring.labels = commands.map { id in catalog.commands.first { $0.id == id }?.label ?? id }
        ring.center = commands.isEmpty ? "No " + ["formats","tools","actions"][mode] : ["Convert","Tools","Actions"][mode]
        ring.highlight = keyboard && !commands.isEmpty ? 0 : nil
        ring.needsDisplay = true
    }
}
