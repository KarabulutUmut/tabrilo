import AppKit
import ImageIO

func macSelfCheck(_ input: URL) throws {
    let catalog = try CommandCatalog.bundled()
    guard let file = Bundle.main.url(forResource:"wheel",withExtension:"json") else { throw CocoaError(.fileNoSuchFile) }
    let spec = try JSONDecoder().decode(WheelSpec.self,from:Data(contentsOf:file))
    guard spec.actions.count == 6, Set(spec.actions).isSubset(of:Set(catalog.commands.map(\.id))),
        spec.sector(NSPoint(x:164,y:164),count:6) == nil, spec.sector(NSPoint(x:-25,y:0),count:6) == nil,
        spec.sector(NSPoint(x:164,y:290),count:6) == 0 else { throw FileProblem("Wheel geometry or catalog mismatch.") }
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("tabrilo-test-" + UUID().uuidString)
    try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:false)
    defer { try? FileManager.default.removeItem(at:dir) }
    let fixture = dir.appendingPathComponent("source.pdf")
    try FileManager.default.copyItem(at:input,to:fixture)
    let original = try Data(contentsOf:fixture)
    let pages = try MacExports.run("convert.png",urls:[fixture],combined:nil)
    guard let output = pages.first else { throw FileProblem("Missing page folder.") }
    let images = try FileManager.default.contentsOfDirectory(at:output,includingPropertiesForKeys:nil)
    guard images.count == 3, images.allSatisfy({ CGImageSourceCreateWithURL($0 as CFURL,nil) != nil }) else { throw FileProblem("Missing page images.") }
    let jpg = try MacExports.run("convert.jpg",urls:[images[0]],combined:nil)
    let pdf = try MacExports.run("convert.pdf",urls:images,combined:nil)
    guard CGImageSourceCreateWithURL(jpg[0] as CFURL,nil) != nil, CGPDFDocument(pdf[0] as CFURL)?.numberOfPages == 3,
        try Data(contentsOf:fixture) == original else { throw FileProblem("Conversion validation failed.") }
}
