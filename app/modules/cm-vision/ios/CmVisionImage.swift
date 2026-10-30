import CoreImage
import ExpoModulesCore
import Foundation
import ImageIO
import Vision

/// A picture decoded once, with the orientation its file says it has. Vision and Foundation Models
/// both take the pixels and the orientation separately, so nothing is re-encoded or written to disk.
struct CmLoadedImage {
  let cgImage: CGImage
  let orientation: CGImagePropertyOrientation
}

enum CmImage {
  static func fileURL(_ imageUri: String) throws -> URL {
    if let parsed = URL(string: imageUri), parsed.isFileURL {
      return parsed
    }
    if imageUri.hasPrefix("/") {
      return URL(fileURLWithPath: imageUri)
    }
    throw CmVisionBadImageException(imageUri)
  }

  static func load(_ imageUri: String) throws -> CmLoadedImage {
    let url = try fileURL(imageUri)
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let cgImage = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
      throw CmVisionBadImageException(imageUri)
    }
    var orientation = CGImagePropertyOrientation.up
    if let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
       let raw = (properties[kCGImagePropertyOrientation] as? NSNumber)?.uint32Value,
       let parsed = CGImagePropertyOrientation(rawValue: raw) {
      orientation = parsed
    }
    return CmLoadedImage(cgImage: cgImage, orientation: orientation)
  }
}

enum CmText {
  /// One text pass over the picture turned `rotation` degrees clockwise (0, 90, 180 or 270), with every box mapped
  /// back to the upright picture. 90 reads lines that run top to bottom, 270 lines that run bottom to top, 180 upside down.
  static func recognize(_ image: CmLoadedImage, rotation: Int) throws -> [[String: Any]] {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = false

    let handler: VNImageRequestHandler
    switch rotation {
    case 0:
      handler = VNImageRequestHandler(cgImage: image.cgImage, orientation: image.orientation, options: [:])
    case 90, 180, 270:
      let upright = CIImage(cgImage: image.cgImage).oriented(image.orientation)
      let turn: CGImagePropertyOrientation = rotation == 90 ? .right : (rotation == 180 ? .down : .left)
      let turned = upright.oriented(turn)
      handler = VNImageRequestHandler(ciImage: turned, options: [:])
    default:
      throw CmVisionBadOrientationException(String(rotation))
    }
    try handler.perform([request])

    let observations: [VNRecognizedTextObservation] = request.results ?? []
    return observations.compactMap { observation in
      guard let top = observation.topCandidates(1).first else { return nil }
      let box = CmContract.unrotated(observation.boundingBox, degrees: rotation)
      return CmContract.textLine(text: top.string, confidence: top.confidence, box: box)
    }
  }
}

final class CmVisionBadOrientationException: GenericException<String> {
  override var reason: String {
    "Text can be read at 0, 90, 180 or 270 degrees, not \(param)"
  }
}

enum CmDevice {
  static func thermalName(_ state: ProcessInfo.ThermalState) -> String {
    switch state {
    case .nominal: return "nominal"
    case .fair: return "fair"
    case .serious: return "serious"
    case .critical: return "critical"
    @unknown default: return "fair"
    }
  }

  static func osVersion() -> String {
    let v = ProcessInfo.processInfo.operatingSystemVersion
    return "\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
  }
}
