import Foundation
import Darwin
import ScreenCaptureKit
import CoreMedia
import CoreImage
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

private struct CaptureOptions {
    let processId: pid_t
    let mode: String
    let outputPath: String
    let targetRect: CGRect?
}

@available(macOS 12.3, *)
private final class FrameReceiver: NSObject, SCStreamOutput {
    private let semaphore = DispatchSemaphore(value: 0)
    private let context = CIContext(options: [.useSoftwareRenderer: false])
    private let lock = NSLock()
    private var capturedImage: CGImage?

    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of outputType: SCStreamOutputType
    ) {
        guard outputType == .screen,
              sampleBuffer.isValid,
              let pixelBuffer = sampleBuffer.imageBuffer else {
            return
        }

        let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
        guard let image = context.createCGImage(ciImage, from: ciImage.extent) else {
            return
        }

        lock.lock()
        if capturedImage == nil {
            capturedImage = image
            semaphore.signal()
        }
        lock.unlock()
    }

    func waitForImage(timeout: TimeInterval) -> CGImage? {
        guard semaphore.wait(timeout: .now() + timeout) == .success else {
            return nil
        }

        lock.lock()
        defer { lock.unlock() }
        return capturedImage
    }
}

private func fail(_ code: String, _ message: String, exitCode: Int32) -> Never {
    writeJson(["success": false, "code": code, "message": message])
    exit(exitCode)
}

private func writeJson(_ value: [String: Any]) {
    if let data = try? JSONSerialization.data(withJSONObject: value, options: []),
       let json = String(data: data, encoding: .utf8) {
        FileHandle.standardOutput.write((json + "\n").data(using: .utf8)!)
    }
}

private func parseOptions() -> CaptureOptions {
    var values: [String: String] = [:]
    let arguments = Array(CommandLine.arguments.dropFirst())
    guard arguments.count % 2 == 0 else {
        fail("capture_failed", "Capture helper arguments must use --name value pairs.", exitCode: 5)
    }

    var index = 0
    while index < arguments.count {
        let key = arguments[index]
        guard key.hasPrefix("--") else {
            fail("capture_failed", "Capture helper arguments must use --name value pairs.", exitCode: 5)
        }
        values[String(key.dropFirst(2))] = arguments[index + 1]
        index += 2
    }

    guard let pidValue = values["pid"], let pid = pid_t(pidValue),
          let mode = values["mode"],
          let output = values["output"] else {
        fail("capture_failed", "Missing required capture helper arguments.", exitCode: 5)
    }

    guard mode == "editor" || mode == "window" else {
        fail("capture_failed", "mode must be editor or window.", exitCode: 5)
    }

    var targetRect: CGRect?
    if mode == "window" {
        guard let x = values["x"].flatMap(Double.init),
              let y = values["y"].flatMap(Double.init),
              let width = values["width"].flatMap(Double.init),
              let height = values["height"].flatMap(Double.init),
              width > 0,
              height > 0 else {
            fail("capture_failed", "The target capture rect is invalid.", exitCode: 5)
        }
        targetRect = CGRect(x: x, y: y, width: width, height: height)
    }

    return CaptureOptions(processId: pid, mode: mode, outputPath: output, targetRect: targetRect)
}

@available(macOS 12.3, *)
private func loadShareableContent() -> SCShareableContent {
    let semaphore = DispatchSemaphore(value: 0)
    var content: SCShareableContent?
    var captureError: Error?
    SCShareableContent.getExcludingDesktopWindows(true, onScreenWindowsOnly: true) { result, error in
        content = result
        captureError = error
        semaphore.signal()
    }

    guard semaphore.wait(timeout: .now() + 5) == .success else {
        fail("capture_failed", "Timed out while enumerating Unity windows.", exitCode: 5)
    }
    if let error = captureError {
        FileHandle.standardError.write((error.localizedDescription + "\n").data(using: .utf8)!)
        fail("permission_denied", "macOS Screen Recording permission is required.", exitCode: 3)
    }
    guard let content = content else {
        fail("capture_failed", "ScreenCaptureKit returned no shareable windows.", exitCode: 5)
    }
    return content
}

private func intersectionRatio(_ target: CGRect, _ host: CGRect) -> CGFloat {
    let intersection = target.intersection(host)
    guard !intersection.isNull, target.width > 0, target.height > 0 else {
        return 0
    }
    return (intersection.width * intersection.height) / (target.width * target.height)
}

@available(macOS 12.3, *)
private func selectWindow(
    options: CaptureOptions,
    content: SCShareableContent
) -> (SCWindow, CGRect) {
    let windows = content.windows.filter {
        $0.owningApplication?.processID == options.processId && $0.isOnScreen && $0.windowLayer == 0
    }
    guard !windows.isEmpty else {
        fail("target_not_visible", "No visible Unity window was found for the requested process.", exitCode: 4)
    }

    if options.mode == "editor" {
        let window = windows.max { left, right in
            left.frame.width * left.frame.height < right.frame.width * right.frame.height
        }!
        return (window, window.frame)
    }

    guard let target = options.targetRect else {
        fail("capture_failed", "The Editor window target rect is missing.", exitCode: 5)
    }
    let matching = windows.compactMap { window -> (SCWindow, CGFloat)? in
        let ratio = intersectionRatio(target, window.frame)
        return ratio >= 0.5 ? (window, ratio) : nil
    }.sorted { left, right in
        if abs(left.1 - right.1) > 0.0001 {
            return left.1 > right.1
        }
        return left.0.frame.width * left.0.frame.height < right.0.frame.width * right.0.frame.height
    }

    guard let host = matching.first?.0 else {
        fail("target_not_visible", "The requested Editor window is not inside a visible Unity window.", exitCode: 4)
    }
    return (host, target.intersection(host.frame))
}

@available(macOS 12.3, *)
private func selectDisplay(for rect: CGRect, from content: SCShareableContent) -> SCDisplay {
    let center = CGPoint(x: rect.midX, y: rect.midY)
    if let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(center) }) {
        return display
    }
    guard let display = content.displays.max(by: {
        CGDisplayBounds($0.displayID).intersection(rect).width * CGDisplayBounds($0.displayID).intersection(rect).height
            < CGDisplayBounds($1.displayID).intersection(rect).width * CGDisplayBounds($1.displayID).intersection(rect).height
    }) else {
        fail("target_not_visible", "No display contains the requested Unity window.", exitCode: 4)
    }
    return display
}

@available(macOS 12.3, *)
private func capture(options: CaptureOptions) {
    let content = loadShareableContent()
    let (window, captureRect) = selectWindow(options: options, content: content)
    guard !captureRect.isNull, captureRect.width > 0, captureRect.height > 0 else {
        fail("target_not_visible", "The requested Editor window has an empty capture area.", exitCode: 4)
    }

    let display = selectDisplay(for: captureRect, from: content)
    let displayFrame = CGDisplayBounds(display.displayID)
    let scale = CGFloat(display.width) / displayFrame.width
    let sourceRect = CGRect(
        x: captureRect.minX - displayFrame.minX,
        y: captureRect.minY - displayFrame.minY,
        width: captureRect.width,
        height: captureRect.height
    )

    guard let owningApplication = window.owningApplication else {
        fail("target_not_visible", "The selected Unity window has no owning application.", exitCode: 4)
    }
    let excludedWindows = content.windows.filter {
        $0.windowID != window.windowID
    }
    let filter = SCContentFilter(
        display: display,
        including: [owningApplication],
        exceptingWindows: excludedWindows
    )
    let configuration = SCStreamConfiguration()
    configuration.sourceRect = sourceRect
    configuration.width = max(1, Int((captureRect.width * scale).rounded()))
    configuration.height = max(1, Int((captureRect.height * scale).rounded()))
    configuration.minimumFrameInterval = CMTime(value: 1, timescale: 60)
    configuration.queueDepth = 2
    configuration.showsCursor = false

    let receiver = FrameReceiver()
    let stream = SCStream(filter: filter, configuration: configuration, delegate: nil)
    do {
        try stream.addStreamOutput(receiver, type: .screen, sampleHandlerQueue: DispatchQueue(label: "cn.lys.aibridge.editor-capture"))
    } catch {
        FileHandle.standardError.write((error.localizedDescription + "\n").data(using: .utf8)!)
        fail("capture_failed", "Failed to configure ScreenCaptureKit output.", exitCode: 5)
    }

    let startSemaphore = DispatchSemaphore(value: 0)
    var startError: Error?
    stream.startCapture { error in
        startError = error
        startSemaphore.signal()
    }
    guard startSemaphore.wait(timeout: .now() + 5) == .success else {
        fail("capture_failed", "Timed out while starting ScreenCaptureKit.", exitCode: 5)
    }
    if let error = startError {
        FileHandle.standardError.write((error.localizedDescription + "\n").data(using: .utf8)!)
        fail("permission_denied", "macOS Screen Recording permission is required.", exitCode: 3)
    }

    guard let image = receiver.waitForImage(timeout: 5) else {
        stream.stopCapture(completionHandler: nil)
        fail("capture_failed", "ScreenCaptureKit did not return a frame.", exitCode: 5)
    }
    stream.stopCapture(completionHandler: nil)

    let outputUrl = URL(fileURLWithPath: options.outputPath) as CFURL
    guard let destination = CGImageDestinationCreateWithURL(
        outputUrl,
        UTType.png.identifier as CFString,
        1,
        nil
    ) else {
        fail("capture_failed", "Failed to create the PNG output.", exitCode: 5)
    }
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        fail("capture_failed", "Failed to encode the PNG output.", exitCode: 5)
    }

    writeJson(["success": true, "width": image.width, "height": image.height])
}

private let options = parseOptions()
guard #available(macOS 12.3, *) else {
    fail("unsupported_platform", "Editor window capture requires macOS 12.3 or newer.", exitCode: 2)
}
capture(options: options)
