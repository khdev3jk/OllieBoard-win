// Draws the paw-print app icon at the usual Windows sizes and writes them as a multi-image .ico (PNG entries).
// Usage (macOS): swift scripts/make_icon.swift Assets/OllieBoard.ico
import AppKit

func paw(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px) / 32
    let pink = NSColor(srgbRed: 0.96, green: 0.45, blue: 0.62, alpha: 1)
    let outline = NSColor(srgbRed: 0.42, green: 0.16, blue: 0.27, alpha: 1)
    func oval(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) {
        let path = NSBezierPath(ovalIn: CGRect(x: x * s, y: y * s, width: w * s, height: h * s))
        pink.setFill(); path.fill()
        outline.setStroke(); path.lineWidth = max(1, 1.6 * s); path.stroke()
    }
    oval(8, 3, 16, 14)                 // pad
    oval(2.5, 14, 7, 9)                // toes
    oval(9, 20, 7, 9.5)
    oval(16, 20, 7, 9.5)
    oval(22.5, 14, 7, 9)
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let sizes = [16, 20, 24, 32, 48, 64, 256]
let images = sizes.map(paw)
var ico = Data([0, 0, 1, 0, UInt8(sizes.count), 0])
var offset = 6 + 16 * sizes.count
for (size, png) in zip(sizes, images) {
    let side = UInt8(size >= 256 ? 0 : size)
    ico += [side, side, 0, 0, 1, 0, 32, 0]
    withUnsafeBytes(of: UInt32(png.count).littleEndian) { ico += $0 }
    withUnsafeBytes(of: UInt32(offset).littleEndian) { ico += $0 }
    offset += png.count
}
for png in images { ico += png }
try! ico.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
print("wrote \(CommandLine.arguments[1]) (\(ico.count) bytes)")
