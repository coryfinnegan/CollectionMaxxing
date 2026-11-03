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

/// What a text pass is asked for. With no options it is the request the scanner always made: accurate, no language correction, the whole
/// picture. `regions` are in the contract's frame (normalised, origin top-left); each is read as its own request.
struct CmTextOptions {
  var fast = false
  var languageCorrection = false
  var regions: [CGRect] = []

  init() {}

  init(_ options: [String: Any]?) throws {
    guard let options = options else { return }
    fast = (options["level"] as? String) == "fast"
    languageCorrection = (options["languageCorrection"] as? Bool) ?? false
    guard let value = options["regions"], !(value is NSNull) else { return }
    guard let raw = value as? [[String: Any]] else { throw CmVisionBadRegionException("regions that are not boxes") }
    regions = try raw.map { region in
      guard let x = (region["x"] as? NSNumber)?.doubleValue,
            let y = (region["y"] as? NSNumber)?.doubleValue,
            let width = (region["width"] as? NSNumber)?.doubleValue,
            let height = (region["height"] as? NSNumber)?.doubleValue else {
        throw CmVisionBadRegionException("a region without all four numbers")
      }
      return CGRect(x: x, y: y, width: width, height: height)
    }
  }
}

enum CmText {
  /// One text pass over the picture turned `rotation` degrees clockwise (0, 90, 180 or 270), with every box mapped
  /// back to the upright picture. 90 reads lines that run top to bottom, 270 lines that run bottom to top, 180 upside down.
  /// Every line says its pass. With regions the pass is upright and reads each region as its own request: the boxes stay inside
  /// their region (TypeScript puts them back on the picture) and each line says the index of the region that read it.
  static func recognize(_ image: CmLoadedImage, rotation: Int, options: CmTextOptions = CmTextOptions()) throws -> [[String: Any]] {
    guard options.regions.isEmpty || rotation == 0 else { throw CmVisionBadRegionException(String(rotation)) }

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

    if options.regions.isEmpty {
      let request = makeRequest(options)
      try handler.perform([request])
      return lines(of: request, rotation: rotation, region: nil)
    }

    var read: [[String: Any]] = []
    for (index, region) in options.regions.enumerated() {
      let request = makeRequest(options)
      request.regionOfInterest = CmContract.visionRegion(region)
      try handler.perform([request])
      read.append(contentsOf: lines(of: request, rotation: rotation, region: index))
    }
    return read
  }

  private static func makeRequest(_ options: CmTextOptions) -> VNRecognizeTextRequest {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = options.fast ? .fast : .accurate
    request.usesLanguageCorrection = options.languageCorrection
    return request
  }

  private static func lines(of request: VNRecognizeTextRequest, rotation: Int, region: Int?) -> [[String: Any]] {
    let observations: [VNRecognizedTextObservation] = request.results ?? []
    return observations.compactMap { observation in
      guard let top = observation.topCandidates(1).first else { return nil }
      let box = CmContract.unrotated(observation.boundingBox, degrees: rotation)
      return CmContract.textLine(text: top.string, confidence: top.confidence, box: box, pass: rotation, region: region)
    }
  }
}

final class CmVisionBadOrientationException: GenericException<String> {
  override var reason: String {
    "Text can be read at 0, 90, 180 or 270 degrees, not \(param)"
  }
}

final class CmVisionBadRegionException: GenericException<String> {
  override var reason: String {
    "A text region is x, y, width and height, read upright: \(param)"
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
