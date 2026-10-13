import ExpoModulesCore
import Vision

// Deliberately thin: call Vision, shape the result with CmContract, nothing else.
// NAT-2 seam (not implemented): Foundation Models image input uses
// `Attachment(cgImage, orientation:)` inside the prompt builder, guarded by
// `SystemLanguageModel.default.capabilities.contains(.vision)`.
public class CmVisionModule: Module {
  public func definition() -> ModuleDefinition {
    Name("CmVision")

    AsyncFunction("isAvailable") { () -> Bool in
      return true
    }

    AsyncFunction("recognizeText") { (imageUri: String) -> [[String: Any]] in
      let request = VNRecognizeTextRequest()
      request.recognitionLevel = .accurate
      request.usesLanguageCorrection = false
      try CmVisionRunner.perform(request, imageUri: imageUri)
      let observations: [VNRecognizedTextObservation] = request.results ?? []
      return observations.compactMap { observation in
        guard let top = observation.topCandidates(1).first else { return nil }
        return CmContract.textLine(text: top.string, confidence: top.confidence, box: observation.boundingBox)
      }
    }

    AsyncFunction("detectRectangles") { (imageUri: String) -> [[String: Any]] in
      let request = VNDetectRectanglesRequest()
      request.maximumObservations = 0
      request.minimumConfidence = 0.5
      request.minimumSize = 0.1
      request.minimumAspectRatio = 0.1
      request.quadratureTolerance = 30
      try CmVisionRunner.perform(request, imageUri: imageUri)
      let observations: [VNRectangleObservation] = request.results ?? []
      return observations.map { CmContract.rect($0.boundingBox) }
    }

    AsyncFunction("detectBarcodes") { (imageUri: String) -> [[String: Any]] in
      let request = VNDetectBarcodesRequest()
      request.symbologies = [.ean13, .ean8, .upce, .qr, .code128]
      try CmVisionRunner.perform(request, imageUri: imageUri)
      let observations: [VNBarcodeObservation] = request.results ?? []
      return observations.compactMap { observation in
        guard let value = observation.payloadStringValue,
              let name = CmVisionRunner.symbologyName(observation.symbology) else { return nil }
        return CmContract.detectedCode(value: value, symbology: name, bounds: observation.boundingBox)
      }
    }

    AsyncFunction("featurePrint") { (imageUri: String, cropBox: [String: Double]?) -> [String: Any] in
      let request = VNGenerateImageFeaturePrintRequest()
      if let box = cropBox, let x = box["x"], let y = box["y"], let w = box["width"], let h = box["height"] {
        // Contract boxes are top-left; Vision regions are bottom-left.
        request.regionOfInterest = CGRect(x: x, y: 1 - y - h, width: w, height: h)
      }
      try CmVisionRunner.perform(request, imageUri: imageUri)
      guard let observation = request.results?.first else {
        throw CmVisionNoResultException("feature print")
      }
      let data = try NSKeyedArchiver.archivedData(withRootObject: observation, requiringSecureCoding: true)
      return CmContract.featurePrint(revision: request.revision, data: data)
    }

    AsyncFunction("featurePrintDistance") { (a: [String: Any], b: [String: Any]) -> Double in
      let first = try CmVisionRunner.decodePrint(a)
      let second = try CmVisionRunner.decodePrint(b)
      var distance: Float = 0
      try first.computeDistance(&distance, to: second)
      return Double(distance)
    }
  }
}

final class CmVisionBadImageException: GenericException<String> {
  override var reason: String {
    "Could not open the image at \(param)"
  }
}

final class CmVisionNoResultException: GenericException<String> {
  override var reason: String {
    "Vision returned no \(param)"
  }
}

final class CmVisionBadPrintException: Exception {
  override var reason: String {
    "That is not a feature print this device can read"
  }
}

enum CmVisionRunner {
  static func perform(_ request: VNRequest, imageUri: String) throws {
    let url: URL
    if let parsed = URL(string: imageUri), parsed.isFileURL {
      url = parsed
    } else if imageUri.hasPrefix("/") {
      url = URL(fileURLWithPath: imageUri)
    } else {
      throw CmVisionBadImageException(imageUri)
    }
    let handler = VNImageRequestHandler(url: url, options: [:])
    try handler.perform([request])
  }

  static func decodePrint(_ print: [String: Any]) throws -> VNFeaturePrintObservation {
    guard let base64 = print["data"] as? String,
          let data = Data(base64Encoded: base64),
          let observation = try NSKeyedUnarchiver.unarchivedObject(ofClass: VNFeaturePrintObservation.self, from: data)
    else {
      throw CmVisionBadPrintException()
    }
    return observation
  }

  static func symbologyName(_ symbology: VNBarcodeSymbology) -> String? {
    switch symbology {
    case .ean13: return "ean-13"
    case .ean8: return "ean-8"
    case .upce: return "upc-e"
    case .qr: return "qr"
    case .code128: return "code-128"
    default: return nil
    }
  }
}
