import ExpoModulesCore
import Vision

// Deliberately thin: call Apple (Vision, Foundation Models), shape the result with CmContract, nothing else.
// The prompt, timeouts and limits arrive from TypeScript; no prompt text lives here.
public class CmVisionModule: Module {
  public func definition() -> ModuleDefinition {
    Name("CmVision")

    // One `onItem` event per finished item while a read runs; the payload is a ModelItem.
    Events("onItem")

    // "The module is loaded". Whether the model can read pictures is `modelCapabilities`.
    AsyncFunction("isAvailable") { () -> Bool in
      return true
    }

    AsyncFunction("recognizeText") { (imageUri: String, orientations: [Int]?) -> [[String: Any]] in
      let image = try CmImage.load(imageUri)
      let passes = (orientations?.isEmpty ?? true) ? [0] : orientations!
      var lines: [[String: Any]] = []
      for rotation in passes {
        lines.append(contentsOf: try CmText.recognize(image, rotation: rotation))
      }
      return lines
    }

    AsyncFunction("modelCapabilities") { () -> [String: Any] in
      if #available(iOS 27.0, *) {
        return CmModelReader.capabilities()
      }
      if #available(iOS 26.0, *) {
        return CmTextReader.capabilities()
      }
      return CmContract.modelCapabilities(
        available: false, vision: false, guidedGeneration: false, contextSize: nil, reason: "osTooOld")
    }

    // Never rejects: a declined, timed-out, oversized or unavailable read resolves with its outcome.
    AsyncFunction("readItems") { [weak self] (imageUri: String, options: [String: Any], promise: Promise) in
      let unavailable = CmContract.readItemsResult(items: [], unreadableCount: 0, outcome: "unavailable", elapsedMs: 0)
      guard let request = CmReadRequest(options) else {
        promise.resolve(unavailable)
        return
      }
      guard #available(iOS 27.0, *) else {
        promise.resolve(unavailable)
        return
      }
      Task.detached(priority: .userInitiated) {
        let result = await CmModelReader.read(imageUri: imageUri, request: request) { item in
          self?.sendEvent("onItem", item)
        }
        promise.resolve(result)
      }
    }

    // Spike #505: typed extraction from OCR text, no picture. Never rejects either.
    AsyncFunction("extractItems") { [weak self] (ocrText: String, options: [String: Any], promise: Promise) in
      let unavailable = CmContract.readItemsResult(items: [], unreadableCount: 0, outcome: "unavailable", elapsedMs: 0)
      guard let request = CmReadRequest(options), !ocrText.isEmpty else {
        promise.resolve(unavailable)
        return
      }
      guard #available(iOS 26.0, *) else {
        promise.resolve(unavailable)
        return
      }
      Task.detached(priority: .userInitiated) {
        let result = await CmTextReader.extract(ocrText: ocrText, request: request) { item in
          self?.sendEvent("onItem", item)
        }
        promise.resolve(result)
      }
    }

    AsyncFunction("deviceStatus") { () -> [String: Any] in
      return CmContract.deviceStatus(
        thermalState: CmDevice.thermalName(ProcessInfo.processInfo.thermalState),
        osVersion: CmDevice.osVersion())
    }

    // With no options the request is exactly the shelf splitter's: Vision's own limit on how wide a shape may be (0.5, short side over
    // long side) still applies. A caller that wants a case, which is wider than that, names a higher `maximumAspectRatio`.
    AsyncFunction("detectRectangles") { (imageUri: String, options: [String: Double]?) -> [[String: Any]] in
      let request = VNDetectRectanglesRequest()
      request.maximumObservations = 0
      request.minimumConfidence = 0.5
      request.minimumSize = 0.1
      request.minimumAspectRatio = 0.1
      request.quadratureTolerance = 30
      if let minimumSize = options?["minimumSize"] { request.minimumSize = Float(minimumSize) }
      if let maximumAspectRatio = options?["maximumAspectRatio"] { request.maximumAspectRatio = Float(maximumAspectRatio) }
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
    let url = try CmImage.fileURL(imageUri)
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
