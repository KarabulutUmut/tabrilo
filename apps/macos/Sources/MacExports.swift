import AppKit
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
import Darwin

struct FileProblem: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

enum MacExports {
    static let imageExtensions: Set<String> = ["png","jpg","jpeg","bmp","tif","tiff","heic","heif","webp"]
    static func targets(_ urls: [URL], spec: WheelSpec) -> [String] {
        guard !urls.isEmpty else { return [] }
        let support = Set((CGImageSourceCopyTypeIdentifiers() as? [String]) ?? [])
        return spec.conversion.filter { id in
            guard ["convert.jpg","convert.png","convert.pdf"].contains(id) else { return false }
            return urls.allSatisfy { url in
                let ext = url.pathExtension.lowercased()
                guard (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true,
                    ext == "pdf" || (imageExtensions.contains(ext) && UTType(filenameExtension: ext).map { support.contains($0.identifier) } == true)
                else { return false }
                return String(id.dropFirst(8)) != (ext == "jpeg" ? "jpg" : ext)
            }
        }
    }

    private static func image(_ url: URL) throws -> CGImage {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(src) == 1,
            let meta = CGImageSourceCopyPropertiesAtIndex(src,0,nil) as? [CFString: Any],
            let width = meta[kCGImagePropertyPixelWidth] as? Int, let height = meta[kCGImagePropertyPixelHeight] as? Int,
            width > 0, height > 0, width <= 45_000_000 / height else { throw FileProblem("Choose a still image under 45 megapixels.") }
        let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways:true,
            kCGImageSourceCreateThumbnailWithTransform:true,kCGImageSourceThumbnailMaxPixelSize:max(width,height)]
        guard let bitmap = CGImageSourceCreateThumbnailAtIndex(src,0,options as CFDictionary) else { throw FileProblem("Cannot decode image.") }
        return bitmap
    }

    private static func pixels(_ width: Int, _ height: Int, white: Bool) throws -> CGContext {
        guard width > 0, height > 0, width <= 45_000_000 / height, let space = CGColorSpace(name: CGColorSpace.sRGB),
            let context = CGContext(data:nil,width:width,height:height,bitsPerComponent:8,bytesPerRow:0,space:space,
                                    bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue) else { throw FileProblem("Image exceeds render limits.") }
        if white { context.setFillColor(CGColor(gray:1,alpha:1)); context.fill(CGRect(x:0,y:0,width:width,height:height)) }
        return context
    }

    private static func save(_ image: CGImage, to url: URL, jpg: Bool) throws {
        let context = try pixels(image.width,image.height,white:jpg)
        context.draw(image,in:CGRect(x:0,y:0,width:image.width,height:image.height))
        guard let output = context.makeImage(),
            let target = CGImageDestinationCreateWithURL(url as CFURL, (jpg ? UTType.jpeg : UTType.png).identifier as CFString,1,nil)
            else { throw FileProblem("Cannot create output image.") }
        CGImageDestinationAddImage(target,output,[kCGImageDestinationLossyCompressionQuality:0.88] as CFDictionary)
        guard CGImageDestinationFinalize(target), let check = CGImageSourceCreateWithURL(url as CFURL,nil),
            let decoded = CGImageSourceCreateImageAtIndex(check,0,nil), decoded.width == image.width, decoded.height == image.height
            else { throw FileProblem("Exported image failed validation.") }
    }

    private static func publish(_ draft: URL, parent: URL, stem: String, ext: String) throws -> URL {
        for i in 1...1000 {
            try Task.checkCancellation()
            let candidate = parent.appendingPathComponent(stem + (i == 1 ? "" : " (\(i))") + ext)
            if renamex_np(draft.path, candidate.path, UInt32(RENAME_EXCL)) == 0 { return candidate }
            if errno != EEXIST { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        }
        throw FileProblem("No available output name.")
    }

    static func run(_ id: String, urls: [URL], combined: URL?) throws -> [URL] {
        let fm = FileManager.default
        let isPDF = id == "convert.pdf", jpg = id == "convert.jpg"
        var outputs: [URL] = []
        for url in isPDF ? Array(urls.prefix(1)) : urls {
            try Task.checkCancellation()
            let parent = combined ?? url.deletingLastPathComponent()
            let stage = parent.appendingPathComponent(".tabrilo-" + UUID().uuidString, isDirectory: true)
            try fm.createDirectory(at:stage,withIntermediateDirectories:false,attributes:[.posixPermissions:0o700])
            defer { try? fm.removeItem(at:stage) }
            let stem = url.deletingPathExtension().lastPathComponent
            let draft = stage.appendingPathComponent("output")
            if isPDF {
                var box = CGRect(x:0,y:0,width:595.28,height:841.89)
                guard let ctx = CGContext(draft as CFURL,mediaBox:&box,nil), urls.count <= 200 else { throw FileProblem("Cannot create PDF.") }
                var total = 0
                for source in urls.sorted(by: { $0.path < $1.path }) {
                    try Task.checkCancellation()
                    let bitmap = try image(source)
                    total += bitmap.width * bitmap.height
                    guard total <= 60_000_000 else { throw FileProblem("Combined images exceed 60 megapixels.") }
                    ctx.beginPDFPage(nil)
                    let scale = min((box.width-48)/CGFloat(bitmap.width),(box.height-48)/CGFloat(bitmap.height))
                    let w = CGFloat(bitmap.width)*scale, h = CGFloat(bitmap.height)*scale
                    ctx.draw(bitmap,in:CGRect(x:(box.width-w)/2,y:(box.height-h)/2,width:w,height:h))
                    ctx.endPDFPage()
                }
                ctx.closePDF()
                guard CGPDFDocument(draft as CFURL)?.numberOfPages == urls.count else { throw FileProblem("Invalid output PDF.") }
                outputs.append(try publish(draft,parent:parent,stem:stem,ext:".pdf"))
            } else if url.pathExtension.lowercased() == "pdf" {
                guard let doc = CGPDFDocument(url as CFURL), doc.isUnlocked, (1...2000).contains(doc.numberOfPages)
                    else { throw FileProblem("Choose an unlocked PDF of up to 2000 pages.") }
                if doc.numberOfPages > 1 { try fm.createDirectory(at:draft,withIntermediateDirectories:false) }
                for i in 1...doc.numberOfPages {
                    try Task.checkCancellation()
                    guard let page = doc.page(at:i) else { throw FileProblem("Missing PDF page.") }
                    let box = page.getBoxRect(.cropBox), sideways = abs(page.rotationAngle % 180) == 90
                    let w = ceil((sideways ? box.height : box.width)*150/72), h = ceil((sideways ? box.width : box.height)*150/72)
                    guard w.isFinite, h.isFinite, w >= 1, h >= 1, w*h <= 20_000_000 else { throw FileProblem("PDF page exceeds 20 megapixels.") }
                    let rect = CGRect(x:0,y:0,width:w,height:h)
                    let ctx = try pixels(Int(w),Int(h),white:true)
                    ctx.concatenate(page.getDrawingTransform(.cropBox,rect:rect,rotate:0,preserveAspectRatio:true))
                    ctx.drawPDFPage(page)
                    guard let bitmap = ctx.makeImage() else { throw FileProblem("Could not render PDF page.") }
                    try save(bitmap,to:doc.numberOfPages == 1 ? draft : draft.appendingPathComponent(String(format:"Page %04d.",i)+(jpg ? "jpg":"png")),jpg:jpg)
                }
                outputs.append(try publish(draft,parent:parent,stem:stem+(doc.numberOfPages > 1 ? " pages":""),ext:doc.numberOfPages > 1 ? "" : jpg ? ".jpg":".png"))
            } else {
                try save(try image(url),to:draft,jpg:jpg)
                outputs.append(try publish(draft,parent:parent,stem:stem,ext:jpg ? ".jpg":".png"))
            }
        }
        return outputs
    }
}
